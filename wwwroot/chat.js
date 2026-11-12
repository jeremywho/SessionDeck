// A chat pane: one Claude session spoken to in stream-json through its host, rendered as a
// conversation instead of a terminal. ChatModel turns the host's lines into items and knows nothing
// of the page; ChatPane draws them and owns the socket.
(function (root) {
  'use strict';

  const INTERRUPTED = '[Request interrupted by user';
  const SET_PREFIX = 'sd-set-';

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
    constructor() {
      this.catalog = { models: [], commands: [] };
      this.settings = { mode: '', model: '', effort: '' };
      this.reset();
    }

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
    // `images` are this viewer's own previews of what was attached; the session's copy carries none.
    sent(text, images) {
      const item = this.add({ kind: 'user', text, images: images || [], pending: true });
      this.pending.push(item);
      return item;
    }

    setCatalog(frame) {
      this.catalog = {
        models: Array.isArray(frame.models) ? frame.models : [],
        commands: Array.isArray(frame.commands) ? frame.commands : [],
      };
    }

    setSettings(frame) {
      this.settings = { mode: frame.mode || '', model: frame.model || '', effort: frame.effort || '' };
      if (this.settings.model) this.model = this.settings.model;
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
      if (line.type === 'control_response') {
        // A setting the CLI refused to change: said once, here, to whoever is watching.
        const r = line.response || {};
        if (r.subtype === 'error' && String(r.request_id || '').startsWith(SET_PREFIX)) this.add({ kind: 'note', tone: 'error', text: String(r.error || 'The setting was not changed') });
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
      const imageCount = Array.isArray(content) ? content.filter(b => b && b.type === 'image').length : 0;
      if (!text && !imageCount) return;
      const lead = text.trim();
      if (lead.startsWith(INTERRUPTED)) { this.settleDrafts(); this.add({ kind: 'note', tone: 'plain', text: 'Interrupted' }); return; }
      if (line.compactSummary) { this.add({ kind: 'note', tone: 'plain', text: 'Earlier conversation was summarized', detail: text }); return; }
      // What a local command printed ("Set model to …") comes back as if it had been typed: it is a note. Other markup is for the model alone.
      const printed = /^<local-command-stdout>([\s\S]*?)<\/local-command-stdout>\s*$/.exec(lead);
      if (printed) { if (printed[1].trim()) this.add({ kind: 'note', tone: 'plain', text: printed[1].trim() }); return; }
      if (lead.startsWith('<')) return;
      if (!line.isReplay && !line.history) {
        // Said by the CLI on the person's behalf (a finished background task).
        this.add({ kind: 'note', tone: 'plain', text: text.length > 300 ? text.slice(0, 300) + '…' : text });
        return;
      }
      if (!line.history) this.busy = true;
      const i = this.pending.findIndex(p => p.text === text && (p.images || []).length === imageCount);
      if (i >= 0) {
        const item = this.pending.splice(i, 1)[0];
        item.pending = false;
        item.confirmed = true;
        this.touch(item);
        return;
      }
      this.add({ kind: 'user', text, imageCount, history: !!line.history });
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
      // A local command (/context, /effort) is never echoed, so its bubble has nothing to be matched with: the
      // end of the turn settles it. It stays matchable for one more turn, in case its echo was only late.
      this.pending = this.pending.filter(p => !p.settled);
      for (const p of this.pending) { p.pending = false; p.settled = true; this.touch(p); }
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
    // `content` is the text, or text and image blocks when something was attached.
    user: content => JSON.stringify({ type: 'user', message: { role: 'user', content }, parent_tool_use_id: null, session_id: '' }),
    content: (text, images) => !images || !images.length ? text
      : (text ? [{ type: 'text', text }] : []).concat(images.map(i => ({ type: 'image', source: { type: 'base64', media_type: i.mediaType, data: i.data } }))),
    // A change to the running session: set_model {model}, set_permission_mode {mode}, apply_flag_settings {settings:{effortLevel}}.
    set: (subtype, fields) => JSON.stringify({ type: 'control_request', request_id: SET_PREFIX + Date.now().toString(36) + Math.random().toString(36).slice(2, 8), request: Object.assign({ subtype }, fields) }),
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

  // What follows a leading "/" while the message is still that one word; null when the message is not a command being typed.
  function commandQuery(message) {
    const m = /^\/(\S*)$/.exec(message);
    return m ? m[1] : null;
  }

  // The commands whose name holds `query`: the one it names exactly, then those that start with it, then the rest, each in the CLI's order.
  function matchCommands(commands, query) {
    const q = query.toLowerCase();
    const rank = c => { const n = c.name.toLowerCase(); return n === q ? 0 : n.startsWith(q) ? 1 : 2; };
    return commands.filter(c => c.name.toLowerCase().includes(q))
      .map((c, i) => ({ c, i }))
      .sort((a, b) => rank(a.c) - rank(b.c) || a.i - b.i)
      .map(x => x.c);
  }

  // Which entry of the model list is the one in force. The session reports the full model id; the list is keyed by
  // alias, and two aliases can name the same model, so the one last picked here wins, then a named alias over "default".
  function modelInForce(models, model, picked) {
    const same = models.filter(m => m.value === model || m.resolved === model);
    return same.find(m => m.value === picked) || same.find(m => m.value === model) || same.find(m => m.value !== 'default') || same[0] || null;
  }

  const MODES = [['default', 'Ask permissions'], ['acceptEdits', 'Accept edits'], ['plan', 'Plan mode'], ['bypassPermissions', 'Bypass permissions']];
  const EFFORTS = ['low', 'medium', 'high', 'xhigh', 'max'];

  const api = { ChatModel, LineReader, Wire, toolBrief, textOf, commandQuery, matchCommands, modelInForce };
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
    const working = h('div', 'working', 'Working…');
    working.hidden = true;
    column.append(empty, working);
    scroll.appendChild(column);

    // The composer is one card: what is attached, the message, and under it the session's settings and the send button.
    const compose = h('div', 'chat-compose');
    const card = h('div', 'composer');
    const menu = h('div', 'slash-menu');
    menu.setAttribute('role', 'listbox');
    menu.setAttribute('aria-label', 'Commands');
    menu.hidden = true;
    const shelf = h('div', 'attachments');
    shelf.hidden = true;
    const input = document.createElement('textarea');
    input.rows = 1;
    input.placeholder = 'Message Claude';
    input.spellcheck = false;
    const row = h('div', 'composer-row');
    const pick = label => { const s = document.createElement('select'); s.className = 'pick'; s.title = label; s.setAttribute('aria-label', label); s.hidden = true; return s; };
    const modelPick = pick('Model'), effortPick = pick('Effort'), modePick = pick('Permission mode');
    const stateText = h('span', 'composer-state');
    const action = h('button', 'chat-send');
    action.type = 'button';
    row.append(modelPick, effortPick, modePick, stateText, action);
    card.append(menu, shelf, input, row);
    compose.appendChild(card);
    el.append(scroll, compose);

    const ARROW = '<svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M8 13V3.5M3.5 8 8 3.5 12.5 8" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    const SQUARE = '<svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><rect x="4" y="4" width="8" height="8" rx="1.5" fill="currentColor"/></svg>';
    let shots = [], nextShot = 1, preparing = false, hostTakesImages = false, pickedModel = '', drawnCatalog = null, menuItems = [], menuActive = 0, menuClosedFor = null;

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
          column.insertBefore(fresh, before && before.parentNode === column ? before : working);
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
      working.hidden = state !== 'busy';
      stateText.textContent = preparing ? 'Preparing image…'
        : state === 'waiting' ? 'Answer required'
        : state === 'ended' ? (model.ended === '' ? 'Session ended' : `Session ended (exit ${model.ended})`)
        : state === 'offline' ? 'Reconnecting…'
        : '';
      const nothing = !input.value.trim() && !shots.length;
      const stop = state === 'busy' && nothing;
      if (action.classList.contains('stop') !== stop || !action.firstChild) {
        action.innerHTML = stop ? SQUARE : ARROW;
        action.classList.toggle('stop', stop);
        action.setAttribute('aria-label', stop ? 'Stop' : 'Send');
        action.title = stop ? 'Stop (Esc)' : 'Send (Enter)';
      }
      action.disabled = state === 'ended' || state === 'offline' || preparing || (!stop && nothing);
      input.disabled = state === 'ended';
      paintPickers(state);
    }

    function options(select, pairs, value) {
      const want = pairs.map(p => p.join('\u0000')).join('\u0001');
      if (select.dataset.options !== want) {
        select.dataset.options = want;
        select.textContent = '';
        for (const [v, label, title] of pairs) {
          const o = document.createElement('option');
          o.value = v;
          o.textContent = label;
          if (title) o.title = title;
          select.appendChild(o);
        }
      }
      if (document.activeElement !== select) select.value = value;
    }

    // The session's model, effort and permission mode, as the CLI lists and reports them. Nothing shows until it has:
    // a host from before these were sent has no list, and a picker that cannot say what is in force is worse than none.
    function paintPickers(state) {
      const { models } = model.catalog;
      const s = model.settings;
      const live = models.length > 0 && state !== 'ended';
      const inForce = live ? modelInForce(models, s.model, pickedModel) : null;
      modelPick.hidden = !live || !s.model;
      effortPick.hidden = !live || !s.effort;
      modePick.hidden = !live || !s.mode;
      for (const p of [modelPick, effortPick, modePick]) p.disabled = state === 'offline';
      if (!live) return;
      const modelPairs = models.map(m => [m.value, m.name || m.value, m.description || '']);
      if (!inForce && s.model) modelPairs.push([s.model, s.model, '']);
      options(modelPick, modelPairs, inForce ? inForce.value : s.model);
      const levels = inForce && inForce.efforts && inForce.efforts.length ? inForce.efforts.slice() : EFFORTS.slice();
      if (s.effort && !levels.includes(s.effort)) levels.push(s.effort);
      options(effortPick, levels.map(l => [l, l, 'Effort']), s.effort);
      const modePairs = MODES.map(m => m.slice());
      if (s.mode && !MODES.some(m => m[0] === s.mode)) modePairs.push([s.mode, s.mode]);
      options(modePick, modePairs, s.mode);
    }

    function change(select, line) {
      if (!sendLine(line)) paintStatus();
      input.focus();
    }
    modelPick.addEventListener('change', () => { pickedModel = modelPick.value; change(modelPick, Wire.set('set_model', { model: modelPick.value })); });
    effortPick.addEventListener('change', () => change(effortPick, Wire.set('apply_flag_settings', { settings: { effortLevel: effortPick.value } })));
    modePick.addEventListener('change', () => change(modePick, Wire.set('set_permission_mode', { mode: modePick.value })));
    // Losing focus without a choice puts back what is in force; a choice is put back by the session's own answer.
    for (const p of [modelPick, effortPick, modePick]) p.addEventListener('blur', () => schedule());

    // Builds the element for an item, or updates the one it has and returns it.
    function render(item, node) {
      switch (item.kind) {
        case 'user': {
          node = node || h('div', 'msg user');
          node.classList.toggle('pending', !!item.pending);
          node.textContent = '';
          if (item.text) node.appendChild(h('div', 'user-text', item.text));
          if (item.images && item.images.length) {
            const strip = h('div', 'user-images');
            for (const url of item.images) { const img = document.createElement('img'); img.src = url; img.alt = 'Attached image'; strip.appendChild(img); }
            node.appendChild(strip);
          } else if (item.imageCount) {
            node.appendChild(h('div', 'user-attached', item.imageCount === 1 ? '1 image attached' : `${item.imageCount} images attached`));
          }
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
      input.style.height = Math.min(input.scrollHeight, Math.max(120, el.clientHeight * 0.4)) + 'px';
    }

    async function submit() {
      const text = input.value.trim();
      const taken = shots.filter(s => !s.error);
      if ((!text && !taken.length) || preparing) return;
      let images = [];
      if (taken.length) {
        preparing = true;
        paintStatus();
        try { images = await Promise.all(taken.map(s => s.ready)); }
        catch { preparing = false; paintShelf(); paintStatus(); return; }
        preparing = false;
      }
      if (!sendLine(Wire.user(Wire.content(text, images)))) { paintStatus(); return; }
      model.sent(text, taken.map(s => s.url));
      shots = [];
      input.value = '';
      paintShelf();
      paintMenu();
      grow();
      schedule();
      scroll.scrollTop = scroll.scrollHeight;
    }

    function interrupt() { if (model.state === 'busy' || model.state === 'waiting') sendLine(Wire.interrupt()); }

    // ---- attached images ----

    // The image as the session gets it: as pasted when it is a kind and size the API takes, else redrawn smaller.
    async function prepare(file) {
      const MAX_SIDE = 2000, MAX_BYTES = 3.5 * 1024 * 1024;
      let blob = file;
      let type = file.type === 'image/jpg' ? 'image/jpeg' : file.type;
      const bitmap = await createImageBitmap(file);
      const scale = Math.min(1, MAX_SIDE / Math.max(bitmap.width, bitmap.height));
      if (scale < 1 || file.size > MAX_BYTES || !['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(type)) {
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(bitmap.width * scale));
        canvas.height = Math.max(1, Math.round(bitmap.height * scale));
        canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        blob = await new Promise(done => canvas.toBlob(done, 'image/png'));
        type = 'image/png';
        if (blob.size > MAX_BYTES) { blob = await new Promise(done => canvas.toBlob(done, 'image/jpeg', 0.85)); type = 'image/jpeg'; }
      }
      bitmap.close();
      const data = await new Promise((done, fail) => {
        const r = new FileReader();
        r.onload = () => done(String(r.result).split(',')[1] || '');
        r.onerror = () => fail(r.error);
        r.readAsDataURL(blob);
      });
      return { mediaType: type, data };
    }

    function attach(files) {
      for (const file of files) {
        const shot = { id: nextShot++, url: URL.createObjectURL(file), error: '' };
        shot.ready = prepare(file);
        shot.ready.catch(() => { shot.error = 'This image could not be read'; paintShelf(); paintStatus(); });
        shots.push(shot);
      }
      paintShelf();
      paintStatus();
    }

    function paintShelf() {
      shelf.textContent = '';
      shelf.hidden = shots.length === 0;
      for (const shot of shots) {
        const chip = h('div', 'shot' + (shot.error ? ' failed' : ''));
        const img = document.createElement('img');
        img.src = shot.url;
        img.alt = 'Image to send';
        const remove = h('button', 'shot-remove', '×');
        remove.type = 'button';
        remove.setAttribute('aria-label', 'Remove image');
        remove.title = 'Remove image';
        remove.addEventListener('click', () => { shots = shots.filter(s => s !== shot); URL.revokeObjectURL(shot.url); paintShelf(); paintStatus(); input.focus(); });
        chip.append(img, remove);
        if (shot.error) chip.appendChild(h('span', 'shot-error', shot.error));
        shelf.appendChild(chip);
      }
    }

    // A paste that carries text is text, even when the clipboard also holds a picture of it (a copy from a document does that).
    input.addEventListener('paste', ev => {
      const data = ev.clipboardData;
      if (!data || Array.from(data.types).includes('text/plain')) return;
      const files = Array.from(data.files).filter(f => f.type.startsWith('image/'));
      if (!files.length) return;
      ev.preventDefault();
      // A host from before images were handled would keep the whole image in its scrollback and lose the conversation to it.
      if (!hostTakesImages) { model.add({ kind: 'note', tone: 'plain', text: 'Restart this session to attach images: it was started by an older version.' }); schedule(); return; }
      attach(files);
    });

    // ---- the command menu ----

    function paintMenu() {
      const query = commandQuery(input.value);
      menuItems = query === null || menuClosedFor === input.value ? [] : matchCommands(model.catalog.commands, query);
      if (menuClosedFor !== null && menuClosedFor !== input.value) menuClosedFor = null;
      if (menuActive >= menuItems.length) menuActive = 0;
      menu.hidden = menuItems.length === 0;
      menu.textContent = '';
      menuItems.forEach((command, i) => {
        const option = h('button', 'slash-option' + (i === menuActive ? ' active' : ''));
        option.type = 'button';
        option.tabIndex = -1;
        option.setAttribute('role', 'option');
        option.setAttribute('aria-selected', String(i === menuActive));
        const lead = h('span', 'slash-lead');
        lead.appendChild(h('span', 'slash-name', '/' + command.name));
        if (command.hint) lead.appendChild(h('span', 'slash-hint', command.hint));
        option.appendChild(lead);
        option.appendChild(h('span', 'slash-description', command.description || ''));
        if (command.description) option.title = command.description;
        // The message box keeps the focus: the menu is steered from it.
        option.addEventListener('mousedown', ev => ev.preventDefault());
        option.addEventListener('click', () => choose(command, true));
        menu.appendChild(option);
      });
      const active = menu.children[menuActive];
      if (active) active.scrollIntoView({ block: 'nearest' });
    }

    // Tab completes the name; Enter runs a command that takes nothing more, and completes one that does.
    function choose(command, run) {
      if (run && !command.hint) { input.value = '/' + command.name; submit(); return; }
      input.value = '/' + command.name + ' ';
      input.setSelectionRange(input.value.length, input.value.length);
      grow();
      paintMenu();
      paintStatus();
      input.focus();
    }

    function menuKey(ev) {
      if (menu.hidden || ev.isComposing) return false;
      if (ev.key === 'ArrowDown') menuActive = (menuActive + 1) % menuItems.length;
      else if (ev.key === 'ArrowUp') menuActive = (menuActive - 1 + menuItems.length) % menuItems.length;
      else if (ev.key === 'Tab' && !ev.shiftKey) { choose(menuItems[menuActive], false); ev.preventDefault(); return true; }
      else if (ev.key === 'Enter' && !ev.shiftKey) { choose(menuItems[menuActive], true); ev.preventDefault(); return true; }
      else if (ev.key === 'Escape') menuClosedFor = input.value;
      else return false;
      ev.preventDefault();
      paintMenu();
      return true;
    }

    input.addEventListener('input', () => { menuActive = 0; grow(); paintMenu(); paintStatus(); });
    input.addEventListener('keydown', ev => {
      if (menuKey(ev)) return;
      if (ev.key === 'Enter' && !ev.shiftKey && !ev.ctrlKey && !ev.altKey && !ev.isComposing) { ev.preventDefault(); submit(); }
      else if (ev.key === 'Escape') { ev.preventDefault(); interrupt(); }
    });
    input.addEventListener('focus', () => { if (opts.onFocus) opts.onFocus(); });
    input.addEventListener('blur', () => { if (!menu.hidden) { menuClosedFor = input.value; paintMenu(); } });
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
          else if (msg.type === 'catalog') { model.setCatalog(msg); paintMenu(); }
          else if (msg.type === 'settings') { hostTakesImages = true; model.setSettings(msg); }
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
        for (const shot of shots) URL.revokeObjectURL(shot.url);
        for (const item of model.items) for (const url of item.images || []) URL.revokeObjectURL(url);
      },
    };
  }

  root.ChatPane = Object.assign({ create }, api);
})(typeof window !== 'undefined' ? window : globalThis);
