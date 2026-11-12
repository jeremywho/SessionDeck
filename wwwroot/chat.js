// A chat pane: one Claude session spoken to in stream-json through its host, rendered as a
// conversation instead of a terminal. ChatModel turns the host's lines into items and knows nothing
// of the page; ChatPane draws them and owns the socket.
(function (root) {
  'use strict';

  const INTERRUPTED = '[Request interrupted by user';
  const SOLO_TOOLS = new Set(['AskUserQuestion', 'ExitPlanMode', 'Agent', 'Task']);
  const SET_PREFIX = 'sd-set-';
  const DRAFT_PREFIX = 'sd.chat.draft:';
  // A subagent can make hundreds of calls; the latest of them are drawn.
  const SUB_SHOWN = 200;

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
      this.settings = { mode: '', model: '', effort: '', resume: '' };
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
      // The oldest message shown, by its place in the transcript or (for one that arrived live) its uuid; and
      // whether the host has said there is conversation before it.
      this.oldest = null;
      this.earlier = false;
      // Results whose call is not shown: it is in conversation not loaded yet, and gets its result when that is.
      this.orphans = new Map();
    }

    add(item) {
      this.items.push(item);
      this.dirty.add(item);
      return item;
    }

    // A tool call is drawn inside its run, and a subagent's call inside the call that started the subagent:
    // what gets repainted is the row that holds it.
    touch(item) {
      let root = item;
      while (root.parent) root = root.parent;
      this.dirty.add(root.run || root);
    }

    // What to ask the host for to get the conversation before the oldest message shown; null when nothing is shown.
    // The place is sent with the uuid when both are known: the host checks one against the other, since a place
    // names a message only in the file it was read from.
    historyAsk() {
      if (!this.oldest) return null;
      const { at, uuid } = this.oldest;
      if (at !== null) return uuid ? { before: at, uuid } : { before: at };
      return uuid ? { uuid } : null;
    }

    // A page of earlier conversation, put ahead of what is shown. `next` is where the page before it ends, or null
    // when this is the start. A call left without its result at the page's edge is shown as not having finished.
    prepend(lines, next) {
      const older = new ChatModel();
      for (const line of lines) older.durable(line);
      for (const [id, tool] of older.tools) {
        if (!tool.result) {
          const late = this.orphans.get(id);
          if (late) { tool.result = late; this.orphans.delete(id); } else tool.stopped = true;
        }
        if (!this.tools.has(id)) this.tools.set(id, tool);
      }
      for (const [id, result] of older.orphans) this.orphans.set(id, result);
      // A run of calls cut in two by where the page ends is one run again.
      const seam = older.items[older.items.length - 1], first = this.items[0];
      if (seam && first && seam.kind === 'tools' && first.kind === 'tools') {
        for (const tool of seam.tools) tool.run = first;
        first.tools = seam.tools.concat(first.tools);
        older.items.pop();
        this.dirty.add(first);
      }
      this.items = older.items.concat(this.items);
      for (const item of older.items) this.dirty.add(item);
      if (older.oldest) this.oldest = older.oldest;
      this.earlier = typeof next === 'number';
      return older.items.length;
    }

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
      this.settings = { mode: frame.mode || '', model: frame.model || '', effort: frame.effort || '', resume: frame.resume || '' };
      if (this.settings.model) this.model = this.settings.model;
    }

    // A line kept in the host's scrollback: the conversation itself.
    durable(line) {
      if (!line || typeof line !== 'object') return;
      if (this.oldest === null && (line.type === 'user' || line.type === 'assistant') && !line.parent_tool_use_id)
        this.oldest = { at: typeof line.at === 'number' ? line.at : null, uuid: typeof line.uuid === 'string' ? line.uuid : '' };
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
      const content = line.message ? line.message.content : '';
      if (Array.isArray(content)) {
        let handled = false;
        for (const b of content) {
          if (!b || b.type !== 'tool_result') continue;
          handled = true;
          const tool = this.tools.get(b.tool_use_id);
          if (!tool) {
            if (!line.parent_tool_use_id) this.orphans.set(b.tool_use_id, { text: resultText(b.content), error: !!b.is_error });
            continue;
          }
          tool.result = { text: resultText(b.content), error: !!b.is_error };
          this.touch(tool);
        }
        if (handled) return;
      }
      // What is left of a subagent's side is the job it was handed, which its call already shows.
      if (line.parent_tool_use_id) return;
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

    // What a subagent does while it runs, kept under the call that started it.
    sub(line) {
      const parent = this.tools.get(line.parent_tool_use_id);
      if (!parent) return;
      const content = line.message && Array.isArray(line.message.content) ? line.message.content : [];
      const children = parent.children || (parent.children = []);
      for (const b of content) {
        if (!b) continue;
        if (b.type === 'tool_use') {
          const child = { kind: 'tool', id: b.id, name: b.name || 'tool', input: b.input || {}, result: null, request: null, parent };
          children.push(child);
          this.tools.set(b.id, child);
        } else if (b.type === 'text' && b.text) children.push({ kind: 'said', text: b.text, parent });
      }
      this.touch(parent);
    }

    assistant(line) {
      if (line.parent_tool_use_id) { this.sub(line); return; }
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
          const tool = { kind: 'tool', id: b.id, name: b.name || 'tool', input: b.input || {}, result: null, request: null };
          // A call read back from the transcript was made by a process that is gone: with no result there, it never finished.
          if (line.history) tool.stopped = true;
          this.tools.set(b.id, tool);
          // A question and a plan are things to answer and stand alone, and so does a subagent, whose line says
          // what it is doing now. Every other call joins the run of calls it is part of, so a turn that reads
          // twenty files is one line to look past, not twenty.
          const last = this.items[this.items.length - 1];
          if (SOLO_TOOLS.has(tool.name)) this.add(tool);
          else if (last && last.kind === 'tools') { last.tools.push(tool); tool.run = last; this.touch(last); }
          else { tool.run = this.add({ kind: 'tools', tools: [tool] }); }
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
      tool.request = { id: line.request_id, input: req.input || {}, description: req.description || '', open: true, decision: '', answers: null,
        suggestions: Array.isArray(req.permission_suggestions) ? req.permission_suggestions : [], granted: [] };
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
      req.granted = Array.isArray(inner.updatedPermissions) ? inner.updatedPermissions : [];
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
      for (const tool of this.tools.values()) {
        // A subagent left running in the background outlives the turn, and so do its calls.
        if (tool.result || tool.stopped || (tool.parent && taskRunning(tool.parent))) continue;
        tool.stopped = true;
        this.touch(tool);
      }
      if (line.is_error && line.subtype !== 'error_during_execution') {
        const text = typeof line.result === 'string' && line.result ? line.result : String(line.subtype || 'error').replace(/_/g, ' ');
        this.add({ kind: 'note', tone: 'error', text });
      }
    }

    system(line) {
      if (line.subtype === 'task_started' || line.subtype === 'task_progress' || line.subtype === 'task_notification') {
        // A subagent (or a command left running) reports as a task of the call that started it.
        const tool = this.tools.get(line.tool_use_id);
        if (!tool) return;
        const task = tool.task || (tool.task = { description: '', last: '', uses: 0, status: 'running', background: false });
        if (line.subtype === 'task_started') { task.description = String(line.description || ''); task.background = !!line.is_backgrounded; }
        else if (line.subtype === 'task_progress') {
          if (line.description) task.last = String(line.description);
          if (line.usage && typeof line.usage.tool_uses === 'number') task.uses = line.usage.tool_uses;
        } else task.status = String(line.status || 'completed');
        this.touch(tool);
        return;
      }
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
    // `permissions` go with the answer: a mode to continue in, or the rule that makes this the last time it is asked.
    allow: (request, answers, permissions) => JSON.stringify({
      type: 'control_response',
      response: { subtype: 'success', request_id: request.id, response: Object.assign(
        { behavior: 'allow', updatedInput: answers ? Object.assign({}, request.input, { answers }) : request.input },
        permissions && permissions.length ? { updatedPermissions: permissions } : {}) },
    }),
    deny: (request, message) => JSON.stringify({
      type: 'control_response',
      response: { subtype: 'success', request_id: request.id, response: { behavior: 'deny', message: message || 'The user declined this.' } },
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

  // Each permission mode with its label, and the one-word label a phone has room for.
  const MODES = [['default', 'Ask permissions', 'Ask'], ['acceptEdits', 'Accept edits', 'Edits'], ['plan', 'Plan mode', 'Plan'], ['bypassPermissions', 'Bypass permissions', 'Bypass']];

  // "Claude Fable 5.2" -> "Fable 5.2", "Default (recommended)" -> "Default".
  function shortModel(name) {
    return name.replace(/^Claude\s+/, '').replace(/\s*\(.*\)\s*$/, '') || name;
  }
  const EFFORTS = ['low', 'medium', 'high', 'xhigh', 'max'];

  // What an edit took out and put in, line by line: [{ op: ' ' | '-' | '+', text }]. Lines the two share stay in
  // place between the changes. Past a size where comparing every pair of lines would stall the page, the old text
  // is shown going and the new coming, which is still true.
  function lineDiff(before, after) {
    const a = String(before).split('\n'), b = String(after).split('\n');
    let head = 0;
    while (head < a.length && head < b.length && a[head] === b[head]) head++;
    let tail = 0;
    while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail++;
    const x = a.slice(head, a.length - tail), y = b.slice(head, b.length - tail);
    const middle = [];
    if (x.length * y.length > 400000) {
      for (const t of x) middle.push({ op: '-', text: t });
      for (const t of y) middle.push({ op: '+', text: t });
    } else {
      // Longest common subsequence, filled from the end so the walk forward takes removals before additions.
      const width = y.length + 1;
      const table = new Uint32Array((x.length + 1) * width);
      for (let i = x.length - 1; i >= 0; i--)
        for (let j = y.length - 1; j >= 0; j--)
          table[i * width + j] = x[i] === y[j] ? table[(i + 1) * width + j + 1] + 1 : Math.max(table[(i + 1) * width + j], table[i * width + j + 1]);
      let i = 0, j = 0;
      while (i < x.length && j < y.length) {
        if (x[i] === y[j]) { middle.push({ op: ' ', text: x[i] }); i++; j++; }
        else if (table[(i + 1) * width + j] >= table[i * width + j + 1]) middle.push({ op: '-', text: x[i++] });
        else middle.push({ op: '+', text: y[j++] });
      }
      while (i < x.length) middle.push({ op: '-', text: x[i++] });
      while (j < y.length) middle.push({ op: '+', text: y[j++] });
    }
    const same = t => ({ op: ' ', text: t });
    return a.slice(0, head).map(same).concat(middle, a.slice(a.length - tail).map(same));
  }

  // A run of tool calls in a line: how many, of which tools, and how it stands.
  function runOverview(tools) {
    const counts = new Map();
    for (const t of tools) counts.set(t.name, (counts.get(t.name) || 0) + 1);
    const failed = tools.filter(t => t.result && t.result.error).length;
    const asking = tools.some(asks);
    const running = tools.some(t => taskRunning(t) || (!t.result && !t.stopped && !(t.request && t.request.decision === 'deny')));
    // The call to name while the run is at work: the latest one still going.
    const going = tools.filter(t => taskRunning(t) || (!t.result && !t.stopped)).pop() || tools[tools.length - 1];
    return {
      going,
      words: `${tools.length} tool calls`,
      breakdown: Array.from(counts, ([name, n]) => n > 1 ? `${name} ${n}` : name).join(', '),
      failed, asking, running,
      state: asking ? 'asking' : running ? 'running' : failed ? 'failed' : 'done',
    };
  }

  // What the CLI offers to remember with an allow, in words: { label, detail }. Null when it offers nothing, or
  // something this cannot describe: a button whose effect is not stated is not offered.
  function alwaysOffer(suggestions) {
    if (!Array.isArray(suggestions) || !suggestions.length) return null;
    const where = { session: 'this session', localSettings: 'in this folder', projectSettings: 'in this project', userSettings: 'everywhere' };
    if (suggestions.every(x => x && x.type === 'setMode' && x.mode === 'acceptEdits' && x.destination === 'session'))
      return { label: 'Allow all edits this session', detail: '' };
    if (suggestions.every(x => x && x.type === 'addRules' && x.behavior === 'allow' && where[x.destination] && Array.isArray(x.rules) && x.rules.length)) {
      const places = Array.from(new Set(suggestions.map(x => where[x.destination])));
      if (places.length !== 1) return null;
      const rules = suggestions.flatMap(x => x.rules).map(r => r.ruleContent ? `${r.toolName}: ${r.ruleContent}` : String(r.toolName));
      return { label: `Always allow ${places[0]}`, detail: rules.join('\n') };
    }
    return null;
  }

  // What the subagent a call started is doing now and how far along it is; '' when it is not at work or has
  // not said. It is its own line under the call, so a narrow screen that cuts the call's line short still shows it.
  function liveStep(tool) {
    const task = tool.task;
    if (!taskRunning(tool) || !task.last) return '';
    return task.last + (task.uses > 1 ? ` (${task.uses} tool uses)` : '');
  }

  // Whether the task a call started is still at work. One that runs in the foreground is over when its call has
  // its result or was stopped; one sent to the background has its result at once and goes on until it reports.
  function taskRunning(tool) {
    const task = tool.task;
    if (!task || task.status !== 'running' || tool.stopped) return false;
    return task.background || !tool.result;
  }

  // Whether the call, or one a subagent of it made, waits on the person.
  function asks(tool) {
    return !!(tool.request && tool.request.open) || (tool.children || []).some(c => c.kind === 'tool' && asks(c));
  }

  // The text of a plan. The call itself can carry none: the plan then comes with the request for approval, and for
  // a conversation read back from the transcript, where no request is kept, in what the approval said back.
  function planText(tool) {
    const asked = tool.request && tool.request.input && tool.request.input.plan;
    if (typeof asked === 'string' && asked) return asked;
    if (tool.input && typeof tool.input.plan === 'string' && tool.input.plan) return tool.input.plan;
    const said = tool.result && !tool.result.error ? tool.result.text : '';
    const at = said.indexOf('## Approved Plan:');
    return at < 0 ? '' : said.slice(at + '## Approved Plan:'.length).trim();
  }

  // The modes a plan can be approved into: where the session was before it planned, then the two that any session
  // may take. Bypass is offered only as the way back to it, since a session not started with it cannot enter it.
  function planExits(resume) {
    const modes = [];
    for (const m of [resume, 'acceptEdits', 'default']) if (m && m !== 'plan' && !modes.includes(m)) modes.push(m);
    return modes;
  }

  const api = { ChatModel, LineReader, Wire, toolBrief, textOf, commandQuery, matchCommands, modelInForce, shortModel, lineDiff, runOverview, alwaysOffer, planExits, planText, liveStep, taskRunning, asks };
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
    // Earlier conversation: whether this host pages it, whether a page was already put in, and the request out now.
    let hostPages = false, paged = false, asked = '', nextAsk = 1, draftKey = '';

    el.classList.add('chat');
    const scroll = h('div', 'chat-scroll');
    const column = h('div', 'chat-column');
    const empty = h('div', 'chat-empty', 'Type a message to start.');
    const working = h('div', 'working', 'Working…');
    working.hidden = true;
    const earlier = h('button', 'load-earlier', 'Load earlier messages');
    earlier.type = 'button';
    earlier.hidden = true;
    column.append(earlier, empty, working);
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
    // Pasting is how an image gets in on a desktop; a phone has no such gesture worth relying on, so there is a button too.
    const picker = document.createElement('input');
    picker.type = 'file';
    picker.accept = 'image/png,image/jpeg,image/gif,image/webp';
    picker.multiple = true;
    picker.hidden = true;
    const addImage = h('button', 'chat-attach');
    addImage.type = 'button';
    addImage.setAttribute('aria-label', 'Attach image');
    addImage.title = 'Attach image';
    addImage.innerHTML = '<svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M8 3v10M3 8h10" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>';
    addImage.hidden = true;
    row.append(addImage, picker, modelPick, effortPick, modePick, stateText, action);
    card.append(menu, shelf, input, row);
    compose.appendChild(card);
    el.append(scroll, compose);

    const ARROW = '<svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M8 13V3.5M3.5 8 8 3.5 12.5 8" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    const SQUARE = '<svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><rect x="4" y="4" width="8" height="8" rx="1.5" fill="currentColor"/></svg>';
    let shots = [], nextShot = 1, preparing = false, hostTakesImages = false, pickedModel = '', drawnCatalog = null, menuItems = [], menuActive = 0, menuClosedFor = null;

    const sendLine = line => { if (ws && ws.readyState === WebSocket.OPEN) { ws.send(JSON.stringify({ type: 'input', data: line + '\n' })); return true; } return false; };

    function nearBottom() { return scroll.scrollHeight - scroll.scrollTop - scroll.clientHeight < 60; }

    // A keyboard coming up (or a window made shorter) shrinks the conversation under a reader who was at the
    // latest message: they stay there, rather than being left looking at whatever is now at the top.
    let atLatest = true;
    scroll.addEventListener('scroll', () => { atLatest = nearBottom(); });
    const resized = new ResizeObserver(() => { if (atLatest) scroll.scrollTop = scroll.scrollHeight; });
    resized.observe(scroll);

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
          // Ahead of the next item that is on the page: a page of earlier conversation arrives as many at once.
          let before = working;
          for (let i = model.items.indexOf(item) + 1; i < model.items.length; i++) {
            const n = nodes.get(model.items[i]);
            if (n && n.parentNode === column) { before = n; break; }
          }
          column.insertBefore(fresh, before);
        } else if (fresh !== node) {
          node.replaceWith(fresh);
          nodes.set(item, fresh);
        }
      }
      model.dirty.clear();
      empty.style.display = model.items.length ? 'none' : '';
      earlier.hidden = !(hostPages && model.earlier && model.historyAsk());
      earlier.disabled = !!asked || !connected;
      earlier.textContent = asked ? 'Loading…' : 'Load earlier messages';
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
      fit(select);
    }

    // A select is as wide as its longest option unless told otherwise; these are as wide as the choice they show.
    const ruler = h('span', 'pick-ruler');
    el.appendChild(ruler);
    function fit(select) {
      const shown = select.selectedOptions[0];
      ruler.textContent = shown ? shown.textContent : '';
      select.style.width = Math.ceil(ruler.getBoundingClientRect().width) + 26 + 'px';
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
      addImage.hidden = !hostTakesImages || state === 'ended';
      for (const p of [modelPick, effortPick, modePick]) p.disabled = state === 'offline';
      if (!live) return;
      // Three full labels do not fit across a phone: there the model drops its "Claude" and the mode is one word.
      const narrow = !!opts.touch && el.clientWidth < 600;
      const modelPairs = models.map(m => [m.value, narrow ? shortModel(m.name || m.value) : m.name || m.value, m.description || '']);
      if (!inForce && s.model) modelPairs.push([s.model, s.model, '']);
      options(modelPick, modelPairs, inForce ? inForce.value : s.model);
      const levels = inForce && inForce.efforts && inForce.efforts.length ? inForce.efforts.slice() : EFFORTS.slice();
      if (s.effort && !levels.includes(s.effort)) levels.push(s.effort);
      options(effortPick, levels.map(l => [l, l, 'Effort']), s.effort);
      const modePairs = MODES.map(m => [m[0], narrow ? m[2] : m[1]]);
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
    for (const p of [modelPick, effortPick, modePick]) {
      p.addEventListener('blur', () => schedule());
      p.addEventListener('change', () => fit(p));
    }

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
          decorate(node, !item.draft);
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
        case 'tools': return renderRun(item, node);
        case 'tool': return renderTool(item, openIn(node));
      }
      return node || h('div');
    }

    // Links open outside, code blocks get a copy button, and a finished block in a named language is coloured.
    // highlight.js escapes what it is given and returns only spans, so its output goes in after the scrub.
    function decorate(node, colour) {
      if (colour && root.hljs) {
        for (const code of node.querySelectorAll('pre > code[class^="language-"]')) {
          const language = code.className.slice('language-'.length);
          if (!root.hljs.getLanguage(language)) continue;
          try { code.innerHTML = root.hljs.highlight(code.textContent || '', { language, ignoreIllegals: true }).value; } catch { }
        }
      }
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

    // A run of calls is one line that opens to its calls; a run of one is just that call.
    // The calls a reader has opened in what is drawn now, so that drawing it again leaves them open.
    function openIn(node) {
      const open = new Set();
      if (!node) return open;
      for (const d of node.querySelectorAll('details[data-tool]')) if (d.open) open.add(d.dataset.tool);
      if (node.dataset.tool && node.open) open.add(node.dataset.tool);
      return open;
    }

    function renderRun(run, node) {
      const open = openIn(node);
      if (run.tools.length === 1) return renderTool(run.tools[0], open);
      const overview = runOverview(run.tools);
      const fresh = h('details', 'tool-run');
      fresh.open = (!!node && node.classList.contains('tool-run') && node.open) || overview.asking;
      fresh.dataset.state = overview.state;
      const summary = h('summary');
      const latest = overview.going;
      summary.append(h('span', 'tool-name', overview.words),
        h('span', 'tool-brief', overview.running ? `${latest.name} ${toolBrief(latest.name, latest.input)}` : overview.breakdown));
      const word = overview.asking ? 'answer required' : overview.running ? 'running' : overview.failed ? `${overview.failed} failed` : '';
      if (word) summary.appendChild(h('span', 'tool-state', word));
      fresh.appendChild(summary);
      const body = h('div', 'run-body');
      for (const tool of run.tools) body.appendChild(renderTool(tool, open));
      fresh.appendChild(body);
      return fresh;
    }

    // `open` names the calls that were open before this drawing.
    function renderTool(item, open) {
      if (item.name === 'ExitPlanMode') return renderPlan(item);
      const fresh = h('details', 'tool');
      fresh.dataset.tool = item.id;
      const req = item.request;
      const asking = req && req.open;
      // A question from inside a subagent is under the subagent's call, which opens to show it.
      const within = !asking && asks(item);
      fresh.open = open.has(item.id) || !!asking || within;
      // A subagent's call that never got its result is over once the subagent is.
      const over = item.stopped || (item.parent && item.parent.task && !taskRunning(item.parent));
      const state = asking || within ? 'asking' : taskRunning(item) ? 'running'
        : item.result ? (item.result.error ? 'failed' : 'done') : req && req.decision === 'deny' ? 'failed' : over ? 'stopped' : 'running';
      fresh.dataset.state = state;
      const summary = h('summary');
      const word = { asking: 'answer required', failed: req && req.decision === 'deny' ? 'declined' : 'failed', stopped: 'stopped', running: 'running' }[state];
      summary.append(h('span', 'tool-name', item.name), h('span', 'tool-brief', toolBrief(item.name, item.input)));
      if (word) summary.appendChild(h('span', 'tool-state', word));
      const step = liveStep(item);
      if (step) summary.appendChild(h('span', 'tool-live', step));
      fresh.appendChild(summary);
      const body = h('div', 'tool-body');
      if (item.name === 'AskUserQuestion') body.appendChild(questionCard(item));
      else {
        body.appendChild(toolDetail(item));
        if (asking) body.appendChild(permissionCard(req));
      }
      if (item.children && item.children.length) body.appendChild(subActivity(item.children, open));
      if (item.result) body.appendChild(h('pre', 'tool-result' + (item.result.error ? ' error' : ''), clip(item.result.text, 12000)));
      fresh.appendChild(body);
      return fresh;
    }

    // What a subagent did, in order: its calls, each opening like any other, and what it said between them.
    function subActivity(children, open) {
      const box = h('div', 'sub');
      const shown = children.slice(-SUB_SHOWN);
      if (shown.length < children.length) box.appendChild(h('div', 'sub-said', `… ${children.length - shown.length} earlier steps`));
      for (const child of shown) {
        if (child.kind === 'tool') box.appendChild(renderTool(child, open));
        else box.appendChild(h('div', 'sub-said', clip(child.text, 600)));
      }
      return box;
    }

    // What the call was asked to do, in the shape it is easiest to read: an edit as what went and what came,
    // a new file as its text, a command as the command; anything else as the input it was given.
    function toolDetail(item) {
      const input = item.input || {};
      if (typeof input.old_string === 'string' && typeof input.new_string === 'string') {
        const box = h('div', 'diff');
        const lines = lineDiff(input.old_string, input.new_string);
        for (const line of lines.slice(0, 400)) box.appendChild(h('div', 'diff-line ' + (line.op === '-' ? 'del' : line.op === '+' ? 'add' : 'same'), line.op + ' ' + line.text));
        if (lines.length > 400) box.appendChild(h('div', 'diff-line same', `… ${lines.length - 400} more lines`));
        return box;
      }
      if (typeof input.content === 'string' && (input.file_path || input.path)) return h('pre', 'tool-input', clip(input.content, 6000));
      if (typeof input.command === 'string') return h('pre', 'tool-input', clip(input.command, 6000));
      // A subagent's job is prose written for it.
      if (typeof input.prompt === 'string' && input.prompt) return h('pre', 'tool-input', clip(input.prompt, 6000));
      return h('pre', 'tool-input', clip(JSON.stringify(input, null, 2), 6000));
    }

    // A plan is something to read and answer, so it is shown whole: the plan, then how to go on.
    function renderPlan(item) {
      const box = h('div', 'plan');
      box.dataset.tool = item.id;
      const req = item.request;
      const open = req && req.open;
      const head = h('div', 'plan-head');
      head.appendChild(h('span', 'plan-title', 'Plan'));
      // Read back from a transcript there is no request, only what came of it.
      const verdict = open ? 'approval required'
        : req ? (req.decision === 'allow' ? 'approved' : req.decision === 'deny' ? 'not approved' : 'not answered')
        : item.result ? (item.result.error ? 'not approved' : 'approved') : '';
      if (verdict) head.appendChild(h('span', 'tool-state', verdict));
      box.dataset.state = open ? 'asking' : 'done';
      box.appendChild(head);
      const plan = h('div', 'md plan-body');
      const text = planText(item);
      if (text) {
        plan.innerHTML = markdown(text);
        decorate(plan, true);
        box.appendChild(plan);
      }
      if (!open) return box;

      const actions = h('div', 'ask');
      const label = h('label', 'plan-mode', 'After approval');
      const pick = document.createElement('select');
      pick.className = 'plan-pick';
      for (const mode of planExits(model.settings.resume)) {
        const o = document.createElement('option');
        o.value = mode;
        o.textContent = (MODES.find(m => m[0] === mode) || [mode, mode])[1];
        pick.appendChild(o);
      }
      label.appendChild(pick);
      actions.appendChild(label);
      const row = h('div', 'ask-row');
      const approve = h('button', 'primary', 'Approve plan');
      const keep = h('button', '', 'Keep planning');
      approve.type = keep.type = 'button';
      const sent = ok => { if (ok) approve.disabled = keep.disabled = pick.disabled = true; };
      approve.addEventListener('click', () => sent(sendLine(Wire.allow(req, null, [{ type: 'setMode', mode: pick.value, destination: 'session' }]))));
      keep.addEventListener('click', () => sent(sendLine(Wire.deny(req, 'The user wants to keep planning. Ask what to change, or revise the plan.'))));
      row.append(approve, keep);
      actions.appendChild(row);
      box.appendChild(actions);
      return box;
    }

    function permissionCard(req) {
      const card = h('div', 'ask');
      card.appendChild(h('div', 'ask-title', 'Permission required'));
      if (req.description) card.appendChild(h('div', 'ask-text', req.description));
      const always = alwaysOffer(req.suggestions);
      const row = h('div', 'ask-row' + (always ? ' three' : ''));
      const allow = h('button', 'primary', 'Allow');
      const deny = h('button', '', 'Deny');
      const buttons = [allow, deny];
      allow.type = deny.type = 'button';
      const sent = ok => { if (ok) for (const b of buttons) b.disabled = true; };
      allow.addEventListener('click', () => sent(sendLine(Wire.allow(req))));
      deny.addEventListener('click', () => sent(sendLine(Wire.deny(req))));
      row.appendChild(allow);
      if (always) {
        const remember = h('button', '', always.label);
        remember.type = 'button';
        buttons.push(remember);
        remember.addEventListener('click', () => sent(sendLine(Wire.allow(req, null, req.suggestions))));
        row.appendChild(remember);
      }
      row.appendChild(deny);
      card.appendChild(row);
      if (always && always.detail) card.appendChild(h('pre', 'ask-rule', clip(always.detail, 600)));
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

    // What is typed and not sent is kept by conversation, so a session restarted under it (an update does that
    // to one left alone) comes back with the words still in the box.
    function keepDraft() {
      if (!draftKey) return;
      try { if (input.value) localStorage.setItem(draftKey, input.value); else localStorage.removeItem(draftKey); } catch { }
    }

    function takeDraft(session) {
      if (draftKey || !session) return;
      draftKey = DRAFT_PREFIX + session;
      let kept = '';
      try { kept = localStorage.getItem(draftKey) || ''; } catch { }
      if (kept && !input.value) { input.value = kept; grow(); paintStatus(); }
      else if (input.value) keepDraft();
    }

    // ---- earlier conversation ----

    function askEarlier() {
      const ask = model.historyAsk();
      if (asked || !ask || !ws || ws.readyState !== WebSocket.OPEN) return;
      asked = 'h' + nextAsk++;
      ws.send(JSON.stringify(Object.assign({ type: 'history', request: asked }, ask)));
      schedule();
    }

    // The page goes in above what is being read, which stays where it is on the screen.
    function gotEarlier(msg) {
      if (!asked || msg.request !== asked) return;
      asked = '';
      paged = true;
      const height = scroll.scrollHeight, top = scroll.scrollTop;
      model.prepend(Array.isArray(msg.lines) ? msg.lines : [], msg.next);
      paint();
      scroll.scrollTop = top + (scroll.scrollHeight - height);
    }

    earlier.addEventListener('click', askEarlier);

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
      keepDraft();
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
    addImage.addEventListener('click', () => picker.click());
    picker.addEventListener('change', () => {
      const files = Array.from(picker.files || []).filter(f => f.type.startsWith('image/'));
      picker.value = '';
      if (files.length) attach(files);
      if (!opts.touch) input.focus();
    });

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

    input.addEventListener('input', () => { menuActive = 0; grow(); paintMenu(); paintStatus(); keepDraft(); });
    input.addEventListener('keydown', ev => {
      if (menuKey(ev)) return;
      // On a touch keyboard Return is a new line, as in any message box there; the button sends.
      if (ev.key === 'Enter' && !opts.touch && !ev.shiftKey && !ev.ctrlKey && !ev.altKey && !ev.isComposing) { ev.preventDefault(); submit(); }
      else if (ev.key === 'Escape') { ev.preventDefault(); interrupt(); }
    });
    input.addEventListener('focus', () => { if (opts.onFocus) opts.onFocus(); });
    // A tap on one of the menu's rows can take the focus for a moment on a touch screen: the menu closes only if the focus stays away.
    input.addEventListener('blur', () => setTimeout(() => {
      if (document.activeElement !== input && !menu.hidden) { menuClosedFor = input.value; paintMenu(); }
    }, 250));
    action.addEventListener('click', () => { if (action.classList.contains('stop')) interrupt(); else submit(); input.focus(); });
    el.addEventListener('keydown', ev => {
      // F13 to F16 move through the session list, as they do from a terminal pane.
      if (/^F1[3-6]$/.test(ev.key)) { post({ type: 'listKey', key: ev.key }); ev.preventDefault(); }
    });
    scroll.addEventListener('mousedown', () => { if (opts.onFocus) opts.onFocus(); });

    // ---- the socket ----

    function connect() {
      if (closed) return;
      // The deck attaches to the host itself; a phone attaches through the gateway, which names its own address.
      const sock = new WebSocket(opts.socketUrl ? opts.socketUrl(seq) : `ws://127.0.0.1:${port}/attach?token=${encodeURIComponent(token)}&after=${seq}`);
      sock.binaryType = 'arraybuffer';
      ws = sock;
      let openedAt = 0;
      sock.onopen = () => { openedAt = Date.now(); connected = true; post({ type: 'connected', id }); schedule(); };
      sock.onmessage = ev => {
        if (typeof ev.data === 'string') {
          let msg;
          try { msg = JSON.parse(ev.data); } catch { return; }
          if (msg.type === 'snapshot') {
            if (msg.start !== seq) { model.reset(); reader = new LineReader(msg.start > 0); paged = false; }
            seq = msg.start;
            // `start` says nothing about what came before: a viewer that reattaches asks for what follows the
            // last byte it has, and is answered from there. The host says whether its transcript goes back further.
            hostPages = msg.chat >= 3;
            if (!paged) model.earlier = !!msg.earlier;
            takeDraft(msg.session);
            if (msg.title) post({ type: 'title', id, title: msg.title });
          } else if (msg.type === 'live') model.live(msg.line);
          else if (msg.type === 'history') { gotEarlier(msg); return; }
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
        asked = '';
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
    // The deck names the conversation as it opens the pane; a host too old to name it in its snapshot is then
    // one whose restart still finds the draft.
    takeDraft(opts.session);
    paintStatus();
    if (!closed) connect();

    return {
      model,
      focus() { input.focus(); },
      // Type and send in one step, for a shell around the page that has its own way of taking words.
      send(text) { input.value = String(text || ''); grow(); return submit(); },
      get state() { return el.dataset.state; },
      paste(text) {
        if (!text) return;
        const at = input.selectionStart ?? input.value.length;
        input.setRangeText(text, at, input.selectionEnd ?? at, 'end');
        grow();
        paintStatus();
        keepDraft();
      },
      kill() { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'kill' })); },
      applyLook(look) {
        el.style.setProperty('--chat-mono', `"${look.font}", "Cascadia Code", Consolas, monospace`);
        el.style.setProperty('--chat-size', Math.round(look.size * 96 / 72) + 'px');
        for (const [name, value] of Object.entries(look.ink || {})) el.style.setProperty(name, value);
        el.style.colorScheme = look.onDark ? 'dark' : 'light';
        schedule();
      },
      dispose() {
        closed = true;
        if (frame) cancelAnimationFrame(frame);
        resized.disconnect();
        try { if (ws) ws.close(); } catch { }
        for (const shot of shots) URL.revokeObjectURL(shot.url);
        for (const item of model.items) for (const url of item.images || []) URL.revokeObjectURL(url);
      },
    };
  }

  root.ChatPane = Object.assign({ create }, api);
})(typeof window !== 'undefined' ? window : globalThis);
