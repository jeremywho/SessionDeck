// A chat pane: one Claude session spoken to in stream-json through its host, rendered as a
// conversation instead of a terminal. ChatModel turns the host's lines into items and knows nothing
// of the page; ChatPane draws them and owns the socket.
(function (root) {
  'use strict';

  const INTERRUPTED = '[Request interrupted by user';

  function textOf(content) {
    if (typeof content === 'string') return content;
    if (!Array.isArray(content)) return '';
    return content.filter(b => b && b.type === 'text' && typeof b.text === 'string').map(b => b.text).join('\n');
  }

  function resultText(content) {
    if (typeof content === 'string') return content;
    if (!Array.isArray(content)) return content == null ? '' : JSON.stringify(content);
    return content.map(b => {
      if (b && b.type === 'text') return b.text || '';
      if (b && b.type === 'image') return '[image]';
      return JSON.stringify(b);
    }).join('\n');
  }

  class ChatModel {
    constructor() { this.reset(); }

    reset() {
      this.items = [];
      this.dirty = new Set();
      this.tools = new Map();
      this.requests = new Map();
      this.pending = [];
      this.drafts = [];
      this.current = null;
      this.busy = false;
      this.model = '';
      this.ended = null;
      this.cleared = true;
    }

    add(item) {
      this.items.push(item);
      this.dirty.add(item);
      return item;
    }

    touch(item) { this.dirty.add(item); }

    get waiting() {
      for (const r of this.requests.values()) if (r.open) return true;
      return false;
    }

    // A message typed here, shown at once and matched with the session's own copy when that arrives.
    sent(text) {
      const item = this.add({ kind: 'user', text, pending: true });
      this.pending.push(item);
      return item;
    }

    // A line kept in the host's scrollback: the conversation itself.
    durable(line) {
      if (!line || typeof line !== 'object') return;
      switch (line.type) {
        case 'user': this.user(line); break;
        case 'assistant': this.assistant(line); break;
        case 'control_request': this.request(line); break;
        case 'control_response': this.answered(line); break;
        case 'control_cancel_request': this.close(line.request_id, 'cancelled'); break;
        case 'result': this.result(line); break;
        case 'system': this.system(line); break;
        case 'host': this.add({ kind: 'note', tone: line.subtype === 'stderr' ? 'error' : 'plain', text: String(line.text || '') }); break;
      }
    }

    // A line only viewers attached at the time get: token deltas and turn bookkeeping.
    live(line) {
      if (!line || typeof line !== 'object') return;
      if (line.type === 'system' && line.subtype === 'init') {
        if (line.model) this.model = line.model;
        this.busy = true;
        return;
      }
      if (line.type !== 'stream_event' || line.parent_tool_use_id) return;
      const ev = line.event || {};
      if (ev.type === 'message_start') this.settleDrafts();
      else if (ev.type === 'content_block_start') {
        const kind = ev.content_block && ev.content_block.type;
        this.current = null;
        if (kind === 'text' || kind === 'thinking') {
          this.current = this.add({ kind: kind === 'text' ? 'assistant' : 'thinking', text: '', draft: true });
          this.drafts.push(this.current);
        }
      } else if (ev.type === 'content_block_delta' && this.current) {
        const d = ev.delta || {};
        const more = d.type === 'text_delta' ? d.text : d.type === 'thinking_delta' ? d.thinking : '';
        if (more) { this.current.text += more; this.touch(this.current); }
      }
    }

    user(line) {
      if (line.parent_tool_use_id) return;
      const content = line.message ? line.message.content : '';
      if (Array.isArray(content)) {
        let handled = false;
        for (const b of content) {
          if (!b || b.type !== 'tool_result') continue;
          handled = true;
          const tool = this.tools.get(b.tool_use_id);
          if (!tool) continue;
          tool.result = { text: resultText(b.content), error: !!b.is_error };
          this.touch(tool);
        }
        if (handled) return;
      }
      const text = textOf(content);
      if (!text) return;
      if (text.trim().startsWith(INTERRUPTED)) { this.settleDrafts(); this.add({ kind: 'note', tone: 'plain', text: 'Interrupted' }); return; }
      if (line.compactSummary) { this.add({ kind: 'note', tone: 'plain', text: 'Earlier conversation was summarized', detail: text }); return; }
      if (!line.isReplay && !line.history) {
        // Said by the CLI on the person's behalf (a finished background task): a note, unless it is markup meant for the model alone.
        if (!text.trim().startsWith('<')) this.add({ kind: 'note', tone: 'plain', text: text.length > 300 ? text.slice(0, 300) + '…' : text });
        return;
      }
      if (!line.history) this.busy = true;
      const i = this.pending.findIndex(p => p.text === text);
      if (i >= 0) {
        const item = this.pending.splice(i, 1)[0];
        item.pending = false;
        this.touch(item);
        return;
      }
      this.add({ kind: 'user', text, history: !!line.history });
    }

    assistant(line) {
      if (line.parent_tool_use_id) return;
      if (!line.history) this.busy = true;
      const content = line.message && Array.isArray(line.message.content) ? line.message.content : [];
      for (const b of content) {
        if (!b) continue;
        if (b.type === 'text' || b.type === 'thinking') {
          const kind = b.type === 'text' ? 'assistant' : 'thinking';
          const text = (b.type === 'text' ? b.text : b.thinking) || '';
          const at = this.drafts.findIndex(d => d.kind === kind);
          if (at >= 0) {
            const draft = this.drafts.splice(at, 1)[0];
            if (this.current === draft) this.current = null;
            draft.text = text || draft.text;
            this.settle(draft);
          } else if (text) this.add({ kind, text });
        } else if (b.type === 'tool_use') {
          const tool = this.add({ kind: 'tool', id: b.id, name: b.name || 'tool', input: b.input || {}, result: null, request: null });
          this.tools.set(b.id, tool);
        }
      }
    }

    settle(draft) {
      draft.draft = false;
      this.touch(draft);
      if (!draft.text) this.remove(draft);
    }

    // Drafts whose finished message never came (an interrupt) stay as what was said so far.
    settleDrafts() {
      for (const d of this.drafts) this.settle(d);
      this.drafts = [];
      this.current = null;
    }

    remove(item) {
      const i = this.items.indexOf(item);
      if (i >= 0) this.items.splice(i, 1);
      item.removed = true;
      this.dirty.add(item);
    }

    request(line) {
      const req = line.request || {};
      if (req.subtype !== 'can_use_tool') return;
      let tool = this.tools.get(req.tool_use_id);
      if (!tool) {
        tool = this.add({ kind: 'tool', id: req.tool_use_id || line.request_id, name: req.tool_name || 'tool', input: req.input || {}, result: null, request: null });
        this.tools.set(tool.id, tool);
      }
      tool.request = { id: line.request_id, input: req.input || {}, description: req.description || '', open: true, decision: '', answers: null };
      this.requests.set(line.request_id, tool.request);
      tool.request.tool = tool;
      this.touch(tool);
    }

    answered(line) {
      const r = line.response || {};
      const inner = r.response || {};
      const req = this.requests.get(r.request_id);
      if (!req) return;
      req.open = false;
      req.decision = r.subtype === 'error' ? 'deny' : inner.behavior || 'allow';
      req.answers = inner.updatedInput && inner.updatedInput.answers ? inner.updatedInput.answers : null;
      this.touch(req.tool);
    }

    close(requestId, decision) {
      const req = this.requests.get(requestId);
      if (!req || !req.open) return;
      req.open = false;
      req.decision = decision;
      this.touch(req.tool);
    }

    result(line) {
      this.busy = false;
      this.settleDrafts();
      for (const [id, req] of this.requests) if (req.open) this.close(id, 'cancelled');
      for (const tool of this.tools.values()) if (!tool.result && !tool.stopped) { tool.stopped = true; this.touch(tool); }
      if (line.is_error && line.subtype !== 'error_during_execution') {
        const text = typeof line.result === 'string' && line.result ? line.result : String(line.subtype || 'error').replace(/_/g, ' ');
        this.add({ kind: 'note', tone: 'error', text });
      }
    }

    system(line) {
      if (line.subtype === 'compact_boundary') this.add({ kind: 'note', tone: 'plain', text: 'Conversation compacted' });
      else if (line.subtype === 'api_error' || line.subtype === 'api_retry') {
        const why = (line.error && (line.error.message || line.error.type)) || line.message || 'API error';
        this.add({ kind: 'note', tone: 'error', text: String(why) });
      }
    }

    exited(code) {
      this.ended = code;
      this.busy = false;
      this.settleDrafts();
      for (const [id, req] of this.requests) if (req.open) this.close(id, 'cancelled');
    }

    get state() {
      if (this.ended !== null) return 'ended';
      if (this.waiting) return 'waiting';
      return this.busy ? 'busy' : 'idle';
    }
  }

  // ---- what a viewer sends ----

  const Wire = {
    user: text => JSON.stringify({ type: 'user', message: { role: 'user', content: text }, parent_tool_use_id: null, session_id: '' }),
    interrupt: () => JSON.stringify({ type: 'control_request', request_id: 'interrupt-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8), request: { subtype: 'interrupt' } }),
    allow: (request, answers) => JSON.stringify({
      type: 'control_response',
      response: { subtype: 'success', request_id: request.id, response: { behavior: 'allow', updatedInput: answers ? Object.assign({}, request.input, { answers }) : request.input } },
    }),
    deny: request => JSON.stringify({
      type: 'control_response',
      response: { subtype: 'success', request_id: request.id, response: { behavior: 'deny', message: 'The user declined this.' } },
    }),
  };

  // Splits the host's byte stream into lines. A scrollback that no longer starts at its first byte
  // starts mid-line; that fragment is dropped.
  class LineReader {
    constructor(skipFirst) { this.decoder = new TextDecoder('utf-8'); this.rest = ''; this.skip = !!skipFirst; }
    push(bytes) {
      this.rest += this.decoder.decode(bytes, { stream: true });
      const out = [];
      let i;
      while ((i = this.rest.indexOf('\n')) >= 0) {
        const line = this.rest.slice(0, i);
        this.rest = this.rest.slice(i + 1);
        if (this.skip) { this.skip = false; continue; }
        if (!line.trim()) continue;
        try { out.push(JSON.parse(line)); } catch { }
      }
      return out;
    }
  }

  // The one-line description beside a tool's name.
  function toolBrief(name, input) {
    const i = input || {};
    const pick = i.command || i.file_path || i.path || i.pattern || i.description || i.prompt || i.query || i.url || i.skill || '';
    let s = typeof pick === 'string' ? pick : '';
    if (!s && Array.isArray(i.questions) && i.questions[0]) s = String(i.questions[0].question || '');
    if (!s) {
      const first = Object.values(i).find(v => typeof v === 'string' && v);
      s = first || '';
    }
    s = s.replace(/\s+/g, ' ').trim();
    return s.length > 140 ? s.slice(0, 139) + '…' : s;
  }

  const api = { ChatModel, LineReader, Wire, toolBrief, textOf };
  if (typeof module !== 'undefined' && module.exports) { module.exports = api; return; }

  // ================= the page side =================

  const SAFE_TAGS = new Set(['P', 'BR', 'HR', 'EM', 'STRONG', 'DEL', 'CODE', 'PRE', 'BLOCKQUOTE', 'UL', 'OL', 'LI', 'A', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6',
    'TABLE', 'THEAD', 'TBODY', 'TR', 'TH', 'TD', 'INPUT', 'SPAN', 'DIV']);

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  let markedReady = false;
  function markdown(text) {
    if (!root.marked) return '<p>' + escapeHtml(text).replace(/\n/g, '<br>') + '</p>';
    if (!markedReady) {
      markedReady = true;
      root.marked.use({
        gfm: true,
        breaks: true,
        renderer: {
          // The model's text is not trusted to carry markup: what it writes as HTML is shown as the text it is.
          html(token) { return escapeHtml(token.text || token.raw || ''); },
          image(token) { return escapeHtml(token.text || token.href || ''); },
        },
      });
    }
    let html;
    try { html = root.marked.parse(text); } catch { return '<p>' + escapeHtml(text) + '</p>'; }
    const tpl = document.createElement('template');
    tpl.innerHTML = html;
    scrub(tpl.content);
    return tpl.innerHTML;
  }

  // Whatever the markdown pass produced, only plain formatting survives it: no scripts, handlers, styles or remote loads.
  function scrub(node) {
    for (const el of Array.from(node.querySelectorAll('*'))) {
      if (!SAFE_TAGS.has(el.tagName)) { el.replaceWith(document.createTextNode(el.textContent || '')); continue; }
      for (const attr of Array.from(el.attributes)) {
        const name = attr.name.toLowerCase();
        if (el.tagName === 'A' && name === 'href' && /^(https?:|mailto:)/i.test(attr.value.trim())) continue;
        if (el.tagName === 'CODE' && name === 'class' && /^language-[\w+#.-]+$/.test(attr.value)) continue;
        if ((el.tagName === 'TH' || el.tagName === 'TD') && name === 'align' && /^(left|right|center)$/.test(attr.value)) continue;
        if (el.tagName === 'INPUT' && (name === 'type' && attr.value === 'checkbox' || name === 'checked' || name === 'disabled')) continue;
        el.removeAttribute(attr.name);
      }
      if (el.tagName === 'INPUT' && el.getAttribute('type') !== 'checkbox') el.remove();
      if (el.tagName === 'INPUT') el.setAttribute('disabled', '');
    }
  }

  function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text !== undefined) el.textContent = text;
    return el;
  }

  function clip(s, max) {
    s = String(s);
    return s.length > max ? s.slice(0, max) + `\n… ${s.length - max} more characters` : s;
  }

  function create(opts) {
    const { el, id, port, token, post } = opts;
    const model = new ChatModel();
    const nodes = new Map();
    let ws = null, seq = 0, reader = new LineReader(false), closed = false, retries = 0, connected = false, frame = 0;

    el.classList.add('chat');
    const scroll = h('div', 'chat-scroll');
    const column = h('div', 'chat-column');
    const empty = h('div', 'chat-empty', 'Type a message to start.');
    column.appendChild(empty);
    scroll.appendChild(column);
    const compose = h('div', 'chat-compose');
    const box = h('div', 'chat-box');
    const input = document.createElement('textarea');
    input.rows = 1;
    input.placeholder = 'Message Claude';
    input.spellcheck = false;
    const action = h('button', 'chat-send', 'Send');
    action.type = 'button';
    box.append(input, action);
    const status = h('div', 'chat-status');
    const statusText = h('span', 'chat-state');
    const statusModel = h('span', 'chat-model');
    status.append(statusText, statusModel);
    compose.append(box, status);
    el.append(scroll, compose);

    const sendLine = line => { if (ws && ws.readyState === WebSocket.OPEN) { ws.send(JSON.stringify({ type: 'input', data: line + '\n' })); return true; } return false; };

    function nearBottom() { return scroll.scrollHeight - scroll.scrollTop - scroll.clientHeight < 60; }

    function schedule() {
      if (frame) return;
      frame = requestAnimationFrame(() => { frame = 0; paint(); });
    }

    function paint() {
      const stick = nearBottom();
      if (model.cleared) {
        model.cleared = false;
        for (const n of nodes.values()) n.remove();
        nodes.clear();
      }
      for (const item of model.dirty) {
        let node = nodes.get(item);
        if (item.removed) { if (node) { node.remove(); nodes.delete(item); } continue; }
        const fresh = render(item, node);
        if (!node) {
          nodes.set(item, fresh);
          const next = model.items[model.items.indexOf(item) + 1];
          const before = next ? nodes.get(next) : null;
          if (before && before.parentNode === column) column.insertBefore(fresh, before); else column.appendChild(fresh);
        } else if (fresh !== node) {
          node.replaceWith(fresh);
          nodes.set(item, fresh);
        }
      }
      model.dirty.clear();
      empty.style.display = model.items.length ? 'none' : '';
      paintStatus();
      if (stick) scroll.scrollTop = scroll.scrollHeight;
    }

    function paintStatus() {
      const state = !connected && model.ended === null ? 'offline' : model.state;
      el.dataset.state = state;
      statusText.textContent = state === 'busy' ? 'Working…'
        : state === 'waiting' ? 'Answer required'
        : state === 'ended' ? (model.ended === '' ? 'Session ended' : `Session ended (exit ${model.ended})`)
        : state === 'offline' ? 'Reconnecting…'
        : 'Ready';
      statusModel.textContent = model.model;
      const stop = state === 'busy' && !input.value.trim();
      action.textContent = stop ? 'Stop' : 'Send';
      action.classList.toggle('stop', stop);
      action.disabled = state === 'ended' || state === 'offline' || (!stop && !input.value.trim());
      input.disabled = state === 'ended';
    }

    // Builds the element for an item, or updates the one it has and returns it.
    function render(item, node) {
      switch (item.kind) {
        case 'user': {
          node = node || h('div', 'msg user');
          node.classList.toggle('pending', !!item.pending);
          node.textContent = item.text;
          return node;
        }
        case 'assistant': {
          node = node || h('div', 'msg assistant md');
          node.classList.toggle('draft', !!item.draft);
          node.innerHTML = markdown(item.text);
          decorate(node);
          return node;
        }
        case 'thinking': {
          if (!node) {
            node = h('details', 'thinking');
            node.append(h('summary', '', 'Thinking'), h('div', 'thinking-body'));
          }
          node.querySelector('.thinking-body').textContent = item.text;
          return node;
        }
        case 'note': {
          node = node || h('div', 'note ' + (item.tone || 'plain'));
          node.textContent = item.text;
          if (item.detail) node.title = clip(item.detail, 2000);
          return node;
        }
        case 'tool': return renderTool(item, node);
      }
      return node || h('div');
    }

    function decorate(node) {
      for (const a of node.querySelectorAll('a[href]')) {
        a.addEventListener('click', ev => { ev.preventDefault(); post({ type: 'open', uri: a.getAttribute('href') }); });
      }
      for (const pre of node.querySelectorAll('pre')) {
        const copy = h('button', 'copy', 'Copy');
        copy.type = 'button';
        copy.addEventListener('click', () => {
          post({ type: 'copy', text: (pre.querySelector('code') || pre).textContent || '' });
          copy.textContent = 'Copied';
          setTimeout(() => { copy.textContent = 'Copy'; }, 1200);
        });
        pre.appendChild(copy);
      }
    }

    function renderTool(item, node) {
      const wasOpen = node ? node.open : false;
      const fresh = h('details', 'tool');
      const req = item.request;
      const asking = req && req.open;
      fresh.open = wasOpen || !!asking;
      const state = asking ? 'asking' : item.result ? (item.result.error ? 'failed' : 'done') : req && req.decision === 'deny' ? 'failed' : item.stopped ? 'stopped' : 'running';
      fresh.dataset.state = state;
      const summary = h('summary');
      const word = { asking: 'answer required', failed: req && req.decision === 'deny' ? 'declined' : 'failed', stopped: 'stopped', running: 'running' }[state];
      summary.append(h('span', 'tool-name', item.name), h('span', 'tool-brief', toolBrief(item.name, item.input)));
      if (word) summary.appendChild(h('span', 'tool-state', word));
      fresh.appendChild(summary);
      const body = h('div', 'tool-body');
      if (item.name === 'AskUserQuestion') body.appendChild(questionCard(item));
      else {
        body.appendChild(h('pre', 'tool-input', clip(JSON.stringify(item.input, null, 2), 6000)));
        if (asking) body.appendChild(permissionCard(req));
      }
      if (item.result) body.appendChild(h('pre', 'tool-result' + (item.result.error ? ' error' : ''), clip(item.result.text, 12000)));
      fresh.appendChild(body);
      return fresh;
    }

    function permissionCard(req) {
      const card = h('div', 'ask');
      card.appendChild(h('div', 'ask-title', 'Permission required'));
      if (req.description) card.appendChild(h('div', 'ask-text', req.description));
      const row = h('div', 'ask-row');
      const allow = h('button', 'primary', 'Allow');
      const deny = h('button', '', 'Deny');
      allow.type = deny.type = 'button';
      allow.addEventListener('click', () => { if (sendLine(Wire.allow(req))) { allow.disabled = deny.disabled = true; } });
      deny.addEventListener('click', () => { if (sendLine(Wire.deny(req))) { allow.disabled = deny.disabled = true; } });
      row.append(allow, deny);
      card.appendChild(row);
      return card;
    }

    function questionCard(item) {
      const req = item.request;
      const questions = Array.isArray(item.input.questions) ? item.input.questions : [];
      const open = req && req.open;
      const card = h('div', 'ask');
      const chosen = new Map();
      const submit = h('button', 'primary', 'Answer');
      submit.type = 'button';
      const ready = () => questions.every(q => (chosen.get(q.question) || []).length > 0);
      const send = () => {
        const answers = {};
        for (const q of questions) answers[q.question] = (chosen.get(q.question) || []).join(', ');
        if (sendLine(Wire.allow(req, answers))) for (const b of card.querySelectorAll('button, input')) b.disabled = true;
      };
      for (const q of questions) {
        const block = h('div', 'question');
        if (q.header) block.appendChild(h('span', 'question-header', q.header));
        block.appendChild(h('div', 'ask-text', q.question || ''));
        const given = req && req.answers ? req.answers[q.question] : null;
        if (!open) {
          // Once answered there is nothing left to press: the question stays, with what was picked.
          if (given) {
            const line = h('div', 'answer', 'Answer: ');
            line.appendChild(h('b', '', String(given)));
            block.appendChild(line);
          }
          card.appendChild(block);
          continue;
        }
        const options = h('div', 'options');
        const set = values => { chosen.set(q.question, values); submit.disabled = !ready(); };
        for (const o of Array.isArray(q.options) ? q.options : []) {
          const b = h('button', 'option');
          b.type = 'button';
          b.append(h('span', 'option-label', o.label || ''), h('span', 'option-detail', o.description && o.description !== o.label ? o.description : ''));
          b.addEventListener('click', () => {
            const now = chosen.get(q.question) || [];
            if (q.multiSelect) {
              const next = now.includes(o.label) ? now.filter(v => v !== o.label) : now.concat(o.label);
              b.classList.toggle('picked', next.includes(o.label));
              set(next);
            } else {
              for (const other of options.querySelectorAll('.option')) other.classList.remove('picked');
              b.classList.add('picked');
              set([o.label]);
              if (questions.length === 1) send();
            }
          });
          options.appendChild(b);
        }
        block.appendChild(options);
        {
          const other = document.createElement('input');
          other.type = 'text';
          other.className = 'other';
          other.placeholder = 'Something else…';
          other.addEventListener('input', () => {
            if (!other.value.trim()) { set((chosen.get(q.question) || []).filter(v => v !== other.dataset.last)); other.dataset.last = ''; return; }
            for (const picked of options.querySelectorAll('.option.picked')) if (!q.multiSelect) picked.classList.remove('picked');
            const kept = q.multiSelect ? (chosen.get(q.question) || []).filter(v => v !== other.dataset.last) : [];
            other.dataset.last = other.value.trim();
            set(kept.concat(other.value.trim()));
          });
          other.addEventListener('keydown', ev => { if (ev.key === 'Enter' && ready()) { ev.preventDefault(); send(); } });
          block.appendChild(other);
        }
        card.appendChild(block);
      }
      if (open) {
        submit.disabled = true;
        submit.addEventListener('click', send);
        const row = h('div', 'ask-row');
        const skip = h('button', '', 'Skip');
        skip.type = 'button';
        skip.addEventListener('click', () => { if (sendLine(Wire.deny(req))) for (const b of card.querySelectorAll('button, input')) b.disabled = true; });
        row.append(submit, skip);
        card.appendChild(row);
      } else if (req && req.decision && req.decision !== 'allow') {
        card.appendChild(h('div', 'tool-verdict', req.decision === 'cancelled' ? 'Not answered' : 'Skipped'));
      }
      return card;
    }

    // ---- composing ----

    function grow() {
      input.style.height = 'auto';
      // scrollHeight leaves out the border, which the box's own height (border-box) includes.
      input.style.height = Math.min(input.scrollHeight + 2, Math.max(120, el.clientHeight * 0.4)) + 'px';
    }

    function submit() {
      const text = input.value.trim();
      if (!text) return;
      if (!sendLine(Wire.user(text))) return;
      model.sent(text);
      input.value = '';
      grow();
      schedule();
      scroll.scrollTop = scroll.scrollHeight;
    }

    function interrupt() { if (model.state === 'busy' || model.state === 'waiting') sendLine(Wire.interrupt()); }

    input.addEventListener('input', () => { grow(); paintStatus(); });
    input.addEventListener('keydown', ev => {
      if (ev.key === 'Enter' && !ev.shiftKey && !ev.ctrlKey && !ev.altKey && !ev.isComposing) { ev.preventDefault(); submit(); }
      else if (ev.key === 'Escape') { ev.preventDefault(); interrupt(); }
    });
    input.addEventListener('focus', () => { if (opts.onFocus) opts.onFocus(); });
    action.addEventListener('click', () => { if (action.classList.contains('stop')) interrupt(); else submit(); input.focus(); });
    el.addEventListener('keydown', ev => {
      // F13 to F16 move through the session list, as they do from a terminal pane.
      if (/^F1[3-6]$/.test(ev.key)) { post({ type: 'listKey', key: ev.key }); ev.preventDefault(); }
    });
    scroll.addEventListener('mousedown', () => { if (opts.onFocus) opts.onFocus(); });

    // ---- the socket ----

    function connect() {
      if (closed) return;
      const sock = new WebSocket(`ws://127.0.0.1:${port}/attach?token=${encodeURIComponent(token)}&after=${seq}`);
      sock.binaryType = 'arraybuffer';
      ws = sock;
      let openedAt = 0;
      sock.onopen = () => { openedAt = Date.now(); connected = true; post({ type: 'connected', id }); schedule(); };
      sock.onmessage = ev => {
        if (typeof ev.data === 'string') {
          let msg;
          try { msg = JSON.parse(ev.data); } catch { return; }
          if (msg.type === 'snapshot') {
            if (msg.start !== seq) { model.reset(); reader = new LineReader(msg.start > 0); }
            seq = msg.start;
            if (msg.title) post({ type: 'title', id, title: msg.title });
          } else if (msg.type === 'live') model.live(msg.line);
          else if (msg.type === 'title') post({ type: 'title', id, title: msg.title });
          else if (msg.type === 'exit') { model.exited(msg.code); closed = true; post({ type: 'exit', id, code: msg.code }); }
          schedule();
          return;
        }
        const bytes = new Uint8Array(ev.data);
        seq += bytes.length;
        for (const line of reader.push(bytes)) model.durable(line);
        schedule();
      };
      sock.onclose = () => {
        if (ws !== sock) return;
        ws = null;
        connected = false;
        schedule();
        if (closed) return;
        if (openedAt && Date.now() - openedAt > 15000) retries = 0;
        const delay = Math.min(30000, 500 * Math.pow(2, Math.min(retries++, 6)));
        post({ type: 'disconnected', id, retryMs: delay });
        setTimeout(connect, delay);
      };
      sock.onerror = () => { };
    }

    if (opts.exited) { model.exited(''); closed = true; }
    paintStatus();
    if (!closed) connect();

    return {
      model,
      focus() { input.focus(); },
      paste(text) {
        if (!text) return;
        const at = input.selectionStart ?? input.value.length;
        input.setRangeText(text, at, input.selectionEnd ?? at, 'end');
        grow();
        paintStatus();
      },
      kill() { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'kill' })); },
      applyLook(look) {
        el.style.setProperty('--chat-mono', `"${look.font}", "Cascadia Code", Consolas, monospace`);
        el.style.setProperty('--chat-size', Math.round(look.size * 96 / 72) + 'px');
        for (const [name, value] of Object.entries(look.ink || {})) el.style.setProperty(name, value);
        el.style.colorScheme = look.onDark ? 'dark' : 'light';
      },
      dispose() {
        closed = true;
        if (frame) cancelAnimationFrame(frame);
        try { if (ws) ws.close(); } catch { }
      },
    };
  }

  root.ChatPane = Object.assign({ create }, api);
})(typeof window !== 'undefined' ? window : globalThis);
