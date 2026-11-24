// A chat pane: one Claude session spoken to in stream-json through its host, rendered as a
// conversation instead of a terminal. ChatModel turns the host's lines into items and knows nothing
// of the page; ChatPane draws them and owns the socket.
(function (root) {
  'use strict';

  const INTERRUPTED = '[Request interrupted by user';
  // The host revision (ChatStream.Revision) from which a viewer that attaches is given what is being said, and a turn
  // keeps what it left half said. Before it, a viewer that was watching can show more than one that was not.
  const HOST_REPLAYS = 7;
  const SOLO_TOOLS = new Set(['AskUserQuestion', 'ExitPlanMode', 'Agent', 'Task', 'Advisor']);
  // Tools the service runs for the model, by the name the pane shows: the advisor is a second model asked for a
  // review, and reads as a step of its own. Any other keeps the name it came with.
  const SERVER_TOOLS = { advisor: 'Advisor' };
  // Calls that are meant to be still running when the turn that made them ends.
  const OUTLIVES_TURN = new Set(['Schedule']);

  // What a tool the service ran came back with. It arrives in the model's own message, not as a reply to it. The
  // advisor's advice is the model's to read: the pane is mostly handed a sealed copy, and says that it was given.
  function serverResult(block) {
    const c = block.content;
    if (c && typeof c === 'object' && !Array.isArray(c)) {
      const kind = String(c.type || '');
      if (kind.endsWith('_error')) return { text: (block.type === 'advisor_tool_result' ? 'Advisor unavailable' : 'Failed') + (c.error_code ? ' (' + c.error_code + ')' : ''), error: true };
      if (typeof c.text === 'string') return { text: c.text, error: false };
      if (kind === 'advisor_redacted_result') return { text: 'Reviewed the conversation. Its advice went to the model and is not shown here.', error: false };
    }
    return { text: resultText(c), error: false };
  }
  const SET_PREFIX = 'sd-set-';
  // How long a conversation has to have been quiet for the time to be said where it picks up again.
  const PAUSE_MS = 60 * 60000;
  // How long a conversation has to have been quiet for the age of its last message to be said under it. Short: it
  // is the one time someone looking at a chat can always see, and "a minute ago" already says the chat is done.
  const QUIET_MS = 60000;

  // When a line was said, in ms, or null when it does not say (an agent whose replay carries no times).
  function stampOf(line) {
    const t = typeof line.timestamp === 'string' ? Date.parse(line.timestamp) : typeof line.timestamp === 'number' ? line.timestamp : NaN;
    return Number.isFinite(t) ? t : null;
  }

  function sameDay(a, b) {
    const x = new Date(a), y = new Date(b);
    return x.getFullYear() === y.getFullYear() && x.getMonth() === y.getMonth() && x.getDate() === y.getDate();
  }

  // Whether the time is worth saying before something said at `at`, when the last thing before it was said at
  // `prev`: at the start, after an hour's quiet, and when the day has changed.
  function pauseBefore(prev, at) {
    return prev === null || at - prev >= PAUSE_MS || !sameDay(prev, at);
  }

  // A time as someone reads it now: the clock alone names today, then yesterday, a weekday within the week, a date
  // within the year, and the year after that. `inSentence` is the same after other words: today goes unsaid.
  // It is worked out from `now` every time it is shown, since a pane stays open for days.
  // The formats are made once: the times on show are worded again each time a pane is come back to.
  const DATED = { weekday: 'long', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' };
  const FORMATS = {};
  const format = (name, options) => FORMATS[name] || (FORMATS[name] = new Intl.DateTimeFormat(undefined, options));

  function timeLabel(at, now, inSentence) {
    const when = new Date(at);
    const clock = format('clock', { hour: 'numeric', minute: '2-digit' }).format(when);
    const midnight = t => { const d = new Date(t); return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime(); };
    const days = Math.round((midnight(now) - midnight(at)) / 86400000);
    if (days <= 0) return inSentence ? clock : 'Today ' + clock;
    if (days === 1) return (inSentence ? 'yesterday ' : 'Yesterday ') + clock;
    if (days < 7) return format('weekday', { weekday: 'long' }).format(when) + ' ' + clock;
    if (when.getFullYear() !== new Date(now).getFullYear()) return format('year', Object.assign({ year: 'numeric' }, DATED)).format(when);
    return format('date', DATED).format(when);
  }

  // How long since, in whole words, for the line that says when a conversation was last heard from.
  function since(at, now) {
    const count = (n, unit) => `${n} ${unit}${n === 1 ? '' : 's'} ago`;
    const minutes = Math.max(0, Math.floor((now - at) / 60000));
    if (minutes < 60) return count(Math.max(1, minutes), 'minute');
    if (minutes < 24 * 60) return count(Math.floor(minutes / 60), 'hour');
    const days = Math.floor(minutes / (24 * 60));
    if (days < 60) return count(days, 'day');
    if (days < 730) return count(Math.floor(days / 30), 'month');
    return count(Math.floor(days / 365), 'year');
  }

  function fullTime(at) {
    return format('full', { dateStyle: 'full', timeStyle: 'short' }).format(new Date(at));
  }
  const DRAFT_PREFIX = 'sd.chat.draft:';
  // How wide the conversation column is. The person sets it by dragging the column's edge; it is one width for every
  // chat on the page and is kept, like a window's size.
  const WIDE_KEY = 'sd.chat.wide', WIDE_MIN = 480, GRIP_INSET = 6;
  const widePanes = new Set();
  // Every open pane's way of wording its times again, on one slow timer for them all.
  const clocks = new Set();
  if (typeof window !== 'undefined') setInterval(() => { for (const retime of clocks) retime(true); }, 60000);
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
    // `now` is the viewer's clock, for what is said from here before the session's own line of it comes back.
    // Without one, times come from the session's lines alone.
    constructor(now) {
      this.now = typeof now === 'function' ? now : null;
      this.catalog = { models: [], commands: [], modes: MODES, agent: 'Claude', reload: false };
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
      // When the conversation was last heard from, and the time waiting to be said ahead of whatever comes next
      // (the conversation's start, or where it picks up after a pause).
      this.lastAt = null;
      this.markAt = null;
    }

    // Something was said at `at`. Times that go backwards (a queued message, results of calls made together) are
    // not a pause and do not move the clock.
    clock(at) {
      if (at === null || (this.lastAt !== null && at <= this.lastAt)) return;
      if (pauseBefore(this.lastAt, at)) this.markAt = at;
      this.lastAt = at;
    }

    add(item) {
      if (this.markAt !== null) {
        const mark = { kind: 'time', at: this.markAt };
        this.markAt = null;
        this.items.push(mark);
        this.dirty.add(mark);
      }
      this.items.push(item);
      this.dirty.add(item);
      return item;
    }

    // A tool call is drawn inside its run, and a subagent's call inside the call that started the subagent:
    // what gets repainted is the row that holds it.
    // `rev` counts the changes to a call (its own and those under it), so a run can tell which of its calls to draw again.
    touch(item) {
      let root = item;
      while (root.parent) root = root.parent;
      root.rev = (root.rev || 0) + 1;
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
      // What is shown began with its time, as any start does. With the conversation before it in place that time
      // stays only if there was a pause there; and a pause the page ended on is said where the shown part begins.
      const top = this.items[0];
      if (top && top.kind === 'time') {
        if (older.lastAt !== null && older.markAt === null && !pauseBefore(older.lastAt, top.at)) this.remove(top);
      } else if (older.markAt !== null && top) {
        older.items.push({ kind: 'time', at: older.markAt });
      }
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
      const at = this.now ? this.now() : null;
      this.clock(at);
      const item = this.add({ kind: 'user', text, images: images || [], pending: true, at });
      this.pending.push(item);
      return item;
    }

    setCatalog(frame) {
      // The modes and the name are the agent's: Claude's are the ones known here, and another agent's host says its own.
      this.catalog = {
        models: Array.isArray(frame.models) ? frame.models : [],
        commands: Array.isArray(frame.commands) ? frame.commands : [],
        modes: Array.isArray(frame.modes) && frame.modes.every(m => Array.isArray(m) && m.length >= 2) ? frame.modes : MODES,
        agent: typeof frame.agent === 'string' && frame.agent ? frame.agent : 'Claude',
        // Whether this host can have the agent read its commands and skills from disk again (`/skills reload`).
        reload: frame.reload === true,
      };
    }

    setSettings(frame) {
      this.settings = { mode: frame.mode || '', model: frame.model || '', effort: frame.effort || '', resume: frame.resume || '' };
      if (this.settings.model) this.model = this.settings.model;
    }

    // A line kept in the host's scrollback: the conversation itself.
    durable(line) {
      if (!line || typeof line !== 'object') return;
      // (A message kept only because its turn was cut off is in the scrollback alone: no host can find the page before it.)
      if (this.oldest === null && (line.type === 'user' || line.type === 'assistant') && !line.parent_tool_use_id && !line.partial)
        this.oldest = { at: typeof line.at === 'number' ? line.at : null, uuid: typeof line.uuid === 'string' ? line.uuid : '' };
      if ((line.type === 'user' || line.type === 'assistant') && !line.parent_tool_use_id) this.clock(stampOf(line));
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
          // A reply that starts to arrive is being said now: its own line, with its time, comes when it is whole.
          this.clock(this.now ? this.now() : null);
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
        item.at = stampOf(line) ?? item.at;
        this.touch(item);
        return;
      }
      this.add({ kind: 'user', text, imageCount, history: !!line.history, at: stampOf(line) });
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
          } else if (text.trim()) this.add({ kind, text });
        } else if (typeof b.type === 'string' && b.type.endsWith('_tool_result') && b.tool_use_id) {
          const ran = this.tools.get(b.tool_use_id);
          if (ran) { ran.result = serverResult(b); this.touch(ran); }
        } else if (b.type === 'tool_use' || b.type === 'server_tool_use') {
          const named = b.type === 'server_tool_use' ? SERVER_TOOLS[b.name] || b.name : b.name;
          const tool = { kind: 'tool', id: b.id, name: named || 'tool', input: b.input || {}, result: null, request: null };
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
      // White space alone is not a message: an agent can begin one with a blank line and say the rest as another.
      if (!draft.text.trim()) this.remove(draft);
    }

    // The socket was attached again. A host that says what is being said to whoever attaches (HOST_REPLAYS) says
    // it whole, so what this viewer had of it is dropped: it may have missed part while it was away. From an older
    // host nothing would replace it, and it stays.
    attached(hostRevision) {
      if (hostRevision < HOST_REPLAYS) return;
      for (const d of this.drafts) this.remove(d);
      this.drafts = [];
      this.current = null;
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
      // The host can say which calls the turn leaves running (a command an agent sent to the background).
      const running = new Set(Array.isArray(line.running) ? line.running : []);
      for (const tool of this.tools.values()) {
        // A subagent left running in the background outlives the turn, and so do its calls. So does a timer
        // (Antigravity's Schedule): the turn ends with it still to fire, and it reports when it has.
        if (tool.result || tool.stopped || (tool.parent && taskRunning(tool.parent)) || OUTLIVES_TURN.has(tool.name) || running.has(tool.id)) continue;
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
        // A subagent that has reported can be set going again (Antigravity's are messaged and answer): it is at work once more.
        if (line.subtype === 'task_started') { task.description = String(line.description || ''); task.background = !!line.is_backgrounded; task.status = 'running'; }
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
      // Nothing outlives the session: not a call cut off in the middle of a turn, not a timer still to fire, not a
      // subagent left in the background.
      for (const tool of this.tools.values()) {
        if (tool.stopped || (tool.result && !taskRunning(tool))) continue;
        tool.stopped = true;
        this.touch(tool);
      }
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

  // What the pane itself answers to, beside what the CLI offers. On pipes the CLI refuses /resume (its picker is
  // drawn in a terminal), so a host that can move to another conversation gets the command from here.
  const RESUME = { name: 'resume', description: 'Resume a conversation from this folder', hint: '' };
  // Naming the session is the host's to do (revision 5). Typed to an agent it is a prompt like any other: a turn
  // spent on a name. Before revision 5 the text goes on as typed, which the Claude CLI at least takes as its own command.
  const RENAME = { name: 'rename', description: 'Name this session', hint: '<name>' };
  // `bare`: what follows the name is optional, so Enter on it in the menu runs it as it stands (Tab completes it to add more).
  const SKILLS = { name: 'skills', description: 'List skills and commands, or reload them', hint: '[reload]', bare: true };
  function offeredCommands(commands, hostRevision, reload) {
    if (hostRevision < 4) return commands;
    const own = (hostRevision >= 5 ? [RESUME, RENAME] : [RESUME]).concat(reload ? [SKILLS] : []);
    return own.concat(commands.filter(c => !own.some(o => o.name === c.name)));
  }

  // What `/skills` asks for: 'list' (said alone), 'reload', or 'usage' for anything else said after it; null when the
  // message is not that command. The two forms are the ones the agent's own terminal has.
  function skillsAsked(message) {
    const m = /^\/skills(?:\s+([\s\S]*))?$/.exec(message.trim());
    if (!m) return null;
    const rest = (m[1] || '').trim().toLowerCase();
    return rest === '' || rest === 'list' ? 'list' : rest === 'reload' ? 'reload' : 'usage';
  }

  // The commands and skills an agent offers, as one line. The protocol says a name and a description for each and
  // not which is a skill, so they are not told apart here.
  function skillsNote(agent, commands) {
    const names = commands.map(c => '/' + c.name);
    return names.length ? `${agent} offers ${names.length} commands and skills: ${names.join(', ')}. /skills reload reads them again from disk.`
      : `${agent} has said no commands or skills. /skills reload reads them from disk.`;
  }

  // What reading them again found, as one line. It is the list that is compared: a skill's body is read when the
  // skill is used, so an edit to one is in force either way, and "the same" must not read as "your edit was ignored".
  function reloadNote(msg) {
    const parts = [['Added', msg.added], ['Removed', msg.removed], ['Updated', msg.changed]]
      .filter(([, names]) => Array.isArray(names) && names.length)
      .map(([what, names]) => `${what}: ${names.map(n => '/' + n).join(', ')}.`);
    return parts.length ? 'Skills reloaded. ' + parts.join(' ') : `Skills reloaded: the same ${Number(msg.count) || 0} commands and skills.`;
  }

  // An agent that serves several providers lists each model as "provider/Name" under the id "provider/model"
  // (OpenCode). The provider is a heading in the list, so the choice shows as the model alone.
  function modelLabel(value, name) {
    const id = String(value || ''), text = String(name || id);
    const cut = id.indexOf('/');
    const provider = cut > 0 ? id.slice(0, cut) : '';
    return provider && text.startsWith(provider + '/') && text.length > provider.length + 1
      ? { group: provider, label: text.slice(provider.length + 1) } : { group: '', label: text };
  }

  // The name `/rename …` asks for, on one line ('' when none was typed); null when the message is anything else.
  function renameAsked(message) {
    const m = /^\/rename(?:\s+([\s\S]*))?$/.exec(message.trim());
    return m ? (m[1] || '').replace(/\s+/g, ' ').trim() : null;
  }

  // The conversations whose name holds every word typed, in the order given (newest first).
  function filterSessions(sessions, typed) {
    const words = typed.toLowerCase().split(/\s+/).filter(Boolean);
    if (!words.length) return sessions;
    return sessions.filter(s => { const t = (s.title || '').toLowerCase(); return words.every(w => t.includes(w)); });
  }

  // How long ago, as a person would say it at a glance.
  function ago(at, now) {
    const minutes = Math.max(0, Math.floor((now - at) / 60000));
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    if (minutes < 24 * 60) return `${Math.floor(minutes / 60)}h ago`;
    const days = Math.floor(minutes / (24 * 60));
    if (days < 30) return `${days}d ago`;
    return new Date(at).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
  }

  // Whether a snapshot means "drop what is shown": the scrollback no longer continues from the last byte seen,
  // or the host has moved to another conversation (its epoch changed) and may be answering from that same byte.
  // What Up and Down bring back into the message box, as a terminal's history does: the conversation's earlier
  // messages, then what was sent from here (commands too, which the conversation does not keep), oldest first.
  class Recall {
    constructor() { this.list = []; this.at = -1; this.draft = ''; }
    seed(texts) { this.list = []; for (const t of texts) this.push(t); }
    push(text) {
      const t = String(text || '').trim();
      if (t && this.list[this.list.length - 1] !== t) this.list.push(t);
      this.at = -1;
    }
    // The message before the one shown, or null when there is none; what was being typed is kept for the way back.
    older(current) {
      if (!this.list.length || this.at === 0) return null;
      if (this.at === -1) { this.draft = current; this.at = this.list.length; }
      return this.list[--this.at];
    }
    // The message after the one shown; past the newest, what was being typed. Null when nothing was recalled.
    newer() {
      if (this.at === -1) return null;
      if (++this.at < this.list.length) return this.list[this.at];
      this.at = -1;
      return this.draft;
    }
  }

  function startsOver(seq, epoch, snapshot) {
    return snapshot.start !== seq || (snapshot.epoch || 0) !== epoch;
  }

  // What a line of a unified diff is: added, removed, or neither (context, a hunk header, a file header).
  function diffKind(line) {
    if (line.startsWith('+++') || line.startsWith('---')) return 'same';
    return line.startsWith('+') ? 'add' : line.startsWith('-') ? 'del' : 'same';
  }

  // The width a drag asks for, as one the column can take: no narrower than reads well, no wider than the pane
  // (a pane narrower than that minimum is simply filled).
  function clampWide(px, available) {
    return Math.round(Math.max(WIDE_MIN, Math.min(px, Math.max(WIDE_MIN, available))));
  }

  // The width last chosen, or 0 for the stylesheet's own.
  function storedWide() {
    try { const n = Number(localStorage.getItem(WIDE_KEY)); return n >= WIDE_MIN ? n : 0; } catch { return 0; }
  }

  // Every chat on the page takes the width as it is dragged; it is written down when the drag ends.
  function setWide(px, keep) {
    for (const pane of widePanes) pane(px);
    if (!keep) return;
    try { if (px) localStorage.setItem(WIDE_KEY, String(px)); else localStorage.removeItem(WIDE_KEY); } catch { }
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
    return task.last + (task.uses > 1 ? `, ${task.uses} tool uses` : '');
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

  // Math as models write it (LaTeX), found in running text: `$…$`, `\(…\)` in a line, `$$…$$` and `\[…\]` set apart.
  // `before` is the character ahead of `src`. A dollar sign is far more often money or a shell variable than math,
  // so a `$…$` pair counts only when it is shaped like math: it opens after something that is not a letter or a
  // digit and straight onto its first character, closes straight after its last and before something that is not a
  // letter or a digit, and stays on one line ("$5 and $10", "$HOME/$USER" and "US$5" are all left alone).
  // Returns { raw, tex, display } or null. Whether the TeX is any good is for the renderer to say.
  const MATH_START = /\$|\\[(\[]/;
  function mathAt(src, before) {
    let m;
    if ((m = /^\$\$([\s\S]+?)\$\$/.exec(src)) || (m = /^\\\[([\s\S]+?)\\\]/.exec(src)))
      return m[1].trim() && !/\n\s*\n/.test(m[1]) ? { raw: m[0], tex: m[1].trim(), display: true } : null;
    if ((m = /^\\\(([\s\S]+?)\\\)/.exec(src)))
      return m[1].trim() && !/\n\s*\n/.test(m[1]) ? { raw: m[0], tex: m[1].trim(), display: false } : null;
    if (src[0] !== '$' || /[A-Za-z0-9$\\]/.test(before || '') || src.length < 3 || /[\s$]/.test(src[1])) return null;
    let end = 1;
    while ((end = src.indexOf('$', end)) > 0 && src[end - 1] === '\\') end++;
    if (end < 2) return null;
    const tex = src.slice(1, end);
    if (tex.includes('\n') || /\s/.test(tex[tex.length - 1]) || /[A-Za-z0-9]/.test(src[end + 1] || '')) return null;
    return { raw: src.slice(0, end + 1), tex, display: false };
  }

  // What a table cell holds, for how wide its column has to be to be read: 'long' when it has one unbroken thing too
  // long for a narrow column (a path, which may be broken anywhere), 'prose' when it is a sentence, '' when it is short.
  function cellKind(text) {
    const words = String(text).trim().split(/\s+/).filter(Boolean);
    if (words.some(w => w.length > 28)) return 'long';
    return words.join(' ').length > 28 ? 'prose' : '';
  }

  // What the line under a chat at work says once nothing has come for a while.
  function workingText(at, now) { return `Working… last activity ${since(at, now)}, ${timeLabel(at, now, true)}`; }

  // Script assigning '' to a box the keyboard is typing into makes iOS WebKit call into the keyboard, where the app has hung (iOS 26.6): an edit does not.
  // A box that lost the focus in this turn and gets it back afterwards counts as typed into: focus it before, never after.
  function emptyBox(input, doc) {
    if (!input.value) return;
    if (doc.activeElement === input) {
      input.select();
      try { doc.execCommand('delete'); } catch { }
    }
    if (input.value) input.value = '';
  }

  const api = { HOST_REPLAYS, emptyBox, workingText, cellKind, mathAt, ChatModel, timeLabel, since, pauseBefore, PAUSE_MS, LineReader, Wire, toolBrief, textOf, commandQuery, matchCommands, modelInForce, shortModel, lineDiff, runOverview, alwaysOffer, planExits, planText, liveStep, taskRunning, asks, offeredCommands, renameAsked, skillsAsked, skillsNote, reloadNote, modelLabel, Recall, filterSessions, ago, startsOver, clampWide, diffKind };
  if (typeof module !== 'undefined' && module.exports) { module.exports = api; return; }

  // ================= the page side =================

  const SAFE_TAGS = new Set(['P', 'BR', 'HR', 'EM', 'STRONG', 'DEL', 'CODE', 'PRE', 'BLOCKQUOTE', 'UL', 'OL', 'LI', 'A', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6',
    'TABLE', 'THEAD', 'TBODY', 'TR', 'TH', 'TD', 'INPUT', 'SPAN', 'DIV']);

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  // ---- math ----
  // The markdown pass only marks where math is (two private-use characters around its number), so that what it
  // makes still goes through the scrub as plain text; the formulas are drawn into those places afterwards.
  const MATH_OPEN = '\uE000', MATH_CLOSE = '\uE001';
  let maths = [];
  function mathMark(token) { maths.push(token); return MATH_OPEN + (maths.length - 1) + MATH_CLOSE; }

  // What KaTeX is let put on the page: MathML and nothing else.
  const MATH_TAGS = new Set(['math', 'semantics', 'annotation', 'mrow', 'mi', 'mo', 'mn', 'ms', 'mtext', 'mspace', 'msup', 'msub', 'msubsup', 'mfrac', 'msqrt', 'mroot',
    'mover', 'munder', 'munderover', 'mmultiscripts', 'mprescripts', 'mtable', 'mtr', 'mtd', 'mstyle', 'menclose', 'mpadded', 'mphantom', 'merror']);
  const MATH_ATTRS = new Set(['display', 'encoding', 'mathvariant', 'stretchy', 'fence', 'separator', 'accent', 'accentunder', 'displaystyle', 'scriptlevel', 'mathcolor',
    'mathbackground', 'columnalign', 'rowalign', 'columnspacing', 'rowspacing', 'columnlines', 'rowlines', 'notation', 'width', 'height', 'depth', 'lspace', 'rspace',
    'minsize', 'maxsize', 'linethickness', 'movablelimits', 'largeop', 'symmetric', 'form', 'voffset']);

  // The formula as an element, or null when it is not TeX that can be drawn (the text then stays as it was typed).
  // KaTeX's MathML needs no fonts or styles of its own: the browser draws it.
  function mathNode(m) {
    if (!root.katex) return null;
    try {
      const tpl = document.createElement('template');
      tpl.innerHTML = root.katex.renderToString(m.tex, { output: 'mathml', displayMode: !!m.display, throwOnError: true, trust: false, strict: 'ignore', maxExpand: 500 });
      const math = tpl.content.querySelector('math');
      if (!math) return null;
      for (const el of [math, ...math.querySelectorAll('*')]) {
        if (!MATH_TAGS.has(el.localName)) return null;
        for (const attr of Array.from(el.attributes)) if (!MATH_ATTRS.has(attr.name.toLowerCase())) el.removeAttribute(attr.name);
      }
      return math;
    } catch { return null; }
  }

  function placeMath(node, found) {
    const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
    const marked = [];
    for (let t; (t = walker.nextNode());) if (t.data.includes(MATH_OPEN)) marked.push(t);
    for (const t of marked) {
      const parts = t.data.split(new RegExp(MATH_OPEN + '(\\d+)' + MATH_CLOSE));
      const made = document.createDocumentFragment();
      parts.forEach((part, i) => {
        if (i % 2 === 0) { if (part) made.appendChild(document.createTextNode(part)); return; }
        const m = found[Number(part)];
        if (m) made.appendChild(mathNode(m) || document.createTextNode(m.raw.replace(/\s+$/, '')));
      });
      t.replaceWith(made);
    }
  }

  let markedReady = false;
  // `files`: links to files on the session's computer are kept as links (the deck opens them; a phone cannot).
  function markdown(text, files) {
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
        // Math is taken out before the markdown rules see it: `_`, `*` and `\\` mean other things inside a formula.
        // Code is tokenized first where it starts first, so a `$` in a code span or block is never math.
        extensions: [
          {
            name: 'mathBlock', level: 'block',
            start(src) { const m = /(^|\n)(\$\$|\\\[)/.exec(src); return m ? m.index + m[1].length : undefined; },
            tokenizer(src) {
              const m = mathAt(src, '');
              const tail = m && m.display ? /^[ \t]*(\n|$)/.exec(src.slice(m.raw.length)) : null;
              if (tail) return { type: 'mathBlock', raw: m.raw + tail[0], tex: m.tex, display: true };
            },
            renderer(token) { return '<p>' + mathMark(token) + '</p>\n'; },
          },
          {
            name: 'math', level: 'inline',
            start(src) { const i = src.search(MATH_START); return i < 0 ? undefined : i; },
            tokenizer(src, tokens) {
              const last = tokens && tokens.length ? tokens[tokens.length - 1] : null;
              const m = mathAt(src, last && typeof last.raw === 'string' ? last.raw.slice(-1) : '');
              if (m) return { type: 'math', raw: m.raw, tex: m.tex, display: m.display };
            },
            renderer(token) { return mathMark(token); },
          },
        ],
      });
    }
    let html;
    maths = [];
    try { html = root.marked.parse(text.replace(/[\uE000\uE001]/g, '')); } catch { return '<p>' + escapeHtml(text) + '</p>'; }
    const tpl = document.createElement('template');
    tpl.innerHTML = html;
    scrub(tpl.content, !!files);
    if (maths.length) placeMath(tpl.content, maths);
    maths = [];
    for (const cell of tpl.content.querySelectorAll('td, th')) {
      const kind = cellKind(cell.textContent || '');
      if (kind) cell.className = kind;
    }
    return tpl.innerHTML;
  }

  // Whatever the markdown pass produced, only plain formatting survives it: no scripts, handlers, styles or remote loads.
  // A link to a file on the computer the session runs on (`file:///C:/…` or `C:/…`), which the deck can open there.
  const FILE_LINK = /^(file:\/\/\/[A-Za-z]:|[A-Za-z]:[\\/])/i;

  function scrub(node, files) {
    for (const el of Array.from(node.querySelectorAll('*'))) {
      if (!SAFE_TAGS.has(el.tagName)) { el.replaceWith(document.createTextNode(el.textContent || '')); continue; }
      for (const attr of Array.from(el.attributes)) {
        const name = attr.name.toLowerCase();
        if (el.tagName === 'A' && name === 'href' && (/^(https?:|mailto:)/i.test(attr.value.trim()) || (files && FILE_LINK.test(attr.value.trim())))) continue;
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
    const now = opts.now || (() => Date.now());
    const model = new ChatModel(now);
    const nodes = new Map();
    let ws = null, seq = 0, reader = new LineReader(false), closed = false, retries = 0, connected = false, frame = 0;
    // The deck times this pane's long work for its performance log; anywhere else it just runs.
    const timed = opts.timed || ((_what, _id, fn) => fn());
    // Earlier conversation: whether this host pages it, whether a page was already put in, and the request out now.
    let hostPages = false, paged = false, asked = '', nextAsk = 1, draftKey = '';
    // The host's conversation counter and what it can do; the list of conversations while one is being picked;
    // and the conversation asked for until the host has moved to it or said it will not.
    let epoch = 0, hostRevision = 0, choosing = null, resuming = '';

    el.classList.add('chat');
    const scroll = h('div', 'chat-scroll');
    const column = h('div', 'chat-column');
    const empty = h('div', 'chat-empty', 'Type a message to start.');
    const working = h('div', 'working', 'Working…');
    working.hidden = true;
    const earlier = h('button', 'load-earlier', 'Load earlier messages');
    earlier.type = 'button';
    earlier.hidden = true;
    // When the conversation was last heard from, under its last message: someone coming back to it reads that
    // without scrolling. Shown once it has been quiet for a minute and nothing is running.
    const tail = h('div', 'time-tail');
    tail.hidden = true;
    // A session started before an update keeps running on the host it was started with.
    const behind = h('div', 'host-behind', 'This chat started before the last Session Deck update. Until it restarts, a reply can show on one screen and be missing on the other. To restart it now, right-click it in the desktop\'s session list. A restart ends any timer or subagent it still has running.');
    behind.hidden = true;
    column.append(earlier, empty, working, tail, behind);
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

    // Every place the page itself puts words in the message box, or takes them out, goes through here.
    let emptying = false;
    function setBox(text) {
      if (text || !opts.touch) { input.value = text; return; }
      emptying = true;
      try { emptyBox(input, document); } finally { emptying = false; }
    }

    function nearBottom() { return scroll.scrollHeight - scroll.scrollTop - scroll.clientHeight < 60; }

    // On a phone a tap on the conversation puts the keyboard away, as it does in any chat: the app shows no
    // bar over the keyboard to do it with. A tap on something that answers to taps is left to it.
    if (opts.touch) scroll.addEventListener('click', ev => {
      if (ev.target.closest('a, button, summary, select, input, textarea, label')) return;
      if (String(getSelection() || '')) return;
      if (document.activeElement === input) input.blur();
    });
    // On a desktop a click anywhere in the chat that is not on a control, and not the end of selecting text,
    // puts the cursor in the message box, so what is typed next goes there.
    if (!opts.touch) el.addEventListener('click', ev => {
      if (ev.target.closest('a, button, summary, select, input, textarea, label, .slash-menu, .chat-grip')) return;
      if (String(getSelection() || '')) return;
      if (!input.disabled) input.focus({ preventScroll: true });
    });

    // A keyboard coming up (or a window made shorter) shrinks the conversation under a reader who was at the
    // latest message: they stay there, rather than being left looking at whatever is now at the top.
    let atLatest = true, heightSeen = scroll.clientHeight;
    // A scroll reported while the area is changing height (a phone's keyboard coming up) is the browser's doing,
    // not the reader's: it must not be read as their having left the latest message, or they are left behind.
    scroll.addEventListener('scroll', () => { if (scroll.clientHeight === heightSeen) atLatest = nearBottom(); });
    // A table too wide for the pane scrolls sideways (chat.css). The edge it is cut at fades, so it reads as going on
    // and not as all there is. Every table is read before any is written to, so it is one layout however many there are.
    function edges(tables) {
      const seen = [];
      for (const t of tables) seen.push([t, t.scrollWidth - t.clientWidth - t.scrollLeft > 1, t.scrollLeft > 1]);
      for (const [t, more, back] of seen) { t.classList.toggle('more', more); t.classList.toggle('back', back); }
    }
    let widthSeen = -1;
    const resized = new ResizeObserver(() => {
      heightSeen = scroll.clientHeight;
      if (atLatest) scroll.scrollTop = scroll.scrollHeight;
      placeGrips();
      // Width only: it is what decides whether a table fits (and a pane that was hidden has none until it is shown).
      if (scroll.clientWidth !== widthSeen) { widthSeen = scroll.clientWidth; edges(column.querySelectorAll('.md table')); }
    });
    resized.observe(scroll);
    column.addEventListener('scroll', e => { if (e.target instanceof Element && e.target.tagName === 'TABLE') edges([e.target]); }, { capture: true, passive: true });

    // ---- the column's width ----

    // A thin line at each edge of the column, shown while the pointer is over the pane. Dragging either moves both,
    // since the column stays centred; a double click goes back to the stylesheet's width. A phone has no edges to drag.
    const grips = opts.touch ? [] : ['left', 'right'].map(side => {
      const grip = h('div', 'chat-grip ' + side);
      grip.title = 'Drag to change the width of the conversation. Double-click to reset.';
      el.appendChild(grip);
      return grip;
    });

    function placeGrips() {
      if (!grips.length) return;
      // Kept a little inside the scrolling area: a column as wide as its pane would otherwise put them under the
      // scrollbar and the edge of the next pane, where they cannot be taken hold of.
      const pane = el.getBoundingClientRect(), col = column.getBoundingClientRect(), area = scroll.getBoundingClientRect();
      const least = area.left - pane.left + GRIP_INSET, most = area.left - pane.left + scroll.clientWidth - GRIP_INSET;
      grips[0].style.left = Math.max(least, col.left - pane.left) + 'px';
      grips[1].style.left = Math.min(most, col.right - pane.left) + 'px';
    }

    function applyWide(px) {
      if (px) el.style.setProperty('--chat-wide', px + 'px'); else el.style.removeProperty('--chat-wide');
      // A width someone chose is the width they want to read at: the stylesheet's own limit on a line's length steps aside.
      el.classList.toggle('widened', !!px);
      placeGrips();
      if (atLatest) scroll.scrollTop = scroll.scrollHeight;
    }

    for (const grip of grips) {
      grip.addEventListener('pointerdown', ev => {
        if (ev.button !== 0) return;
        ev.preventDefault();
        grip.setPointerCapture(ev.pointerId);
        el.classList.add('sizing');
        // The column is centred in the scrolling area, so its width is twice the pointer's distance from that centre.
        const centre = scroll.getBoundingClientRect().left + scroll.clientWidth / 2;
        let wide = 0;
        const move = e => { wide = clampWide(2 * Math.abs(e.clientX - centre), scroll.clientWidth); setWide(wide, false); };
        const end = () => {
          grip.removeEventListener('pointermove', move);
          grip.removeEventListener('pointerup', end);
          grip.removeEventListener('pointercancel', end);
          el.classList.remove('sizing');
          if (wide) setWide(wide, true);
        };
        grip.addEventListener('pointermove', move);
        grip.addEventListener('pointerup', end);
        grip.addEventListener('pointercancel', end);
      });
      grip.addEventListener('dblclick', () => setWide(0, true));
    }
    if (grips.length) {
      widePanes.add(applyWide);
      applyWide(storedWide());
    }

    // Every pane shares one thread, so a chat behind another tab is drawn at most this often: drawn each frame, it
    // holds up the pane being typed in. It is drawn at once when it is come back to.
    const BEHIND_MS = 2000;
    const away = opts.behind || (() => false);
    let later = 0;
    function draw() {
      if (!frame) frame = requestAnimationFrame(() => { frame = 0; timed('chat-paint', id, paint); });
    }
    function schedule() {
      if (frame || later) return;
      if (away()) later = setTimeout(() => { later = 0; draw(); }, BEHIND_MS);
      else draw();
    }
    // Drawn behind another tab, a pane measures nothing (see paint): what that left undone is done when it is shown.
    let unsettled = false;
    const shownAgain = opts.behind ? new MutationObserver(() => {
      if (away() || !(later || unsettled)) return;
      if (later) { clearTimeout(later); later = 0; }
      draw();
    }) : null;
    if (shownAgain) shownAgain.observe(el, { attributes: true, attributeFilter: ['class'] });

    function paint() {
      // Asking where anything is makes the browser lay the whole page out there and then, every tab of it. A pane
      // nobody can see asks nothing: whether its reader was at the latest message is remembered, not measured.
      const hidden = away();
      const stick = hidden || unsettled ? atLatest : nearBottom();
      if (model.cleared) {
        model.cleared = false;
        for (const n of nodes.values()) n.remove();
        nodes.clear();
      }
      const drawn = [];
      for (const item of model.dirty) {
        let node = nodes.get(item);
        if (item.removed) { if (node) { node.remove(); nodes.delete(item); } continue; }
        const fresh = render(item, node);
        drawn.push(fresh);
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
      const tables = hidden ? [] : unsettled ? Array.from(column.querySelectorAll('.md table'))
        : drawn.flatMap(n => n.isConnected && n.querySelectorAll ? Array.from(n.querySelectorAll('.md table')) : []);
      if (tables.length) edges(tables);
      empty.style.display = model.items.length ? 'none' : '';
      earlier.hidden = !(hostPages && model.earlier && model.historyAsk());
      earlier.disabled = !!asked || !connected;
      earlier.textContent = asked ? 'Loading…' : 'Load earlier messages';
      paintStatus();
      paintTail();
      unsettled = hidden;
      if (stick && !hidden) scroll.scrollTop = scroll.scrollHeight;
    }

    function markText(item) { return timeLabel(item.at, now()); }

    // Whether the line came or went, which changes how tall the conversation is: the caller keeps a reader at the
    // latest message then. New words on a line already there change nothing's place.
    function paintTail() {
      const at = model.lastAt, t = now();
      const quiet = at !== null && model.items.length > 0 && t - at >= QUIET_MS;
      // At work with nothing new for a while (a command that takes minutes): how long, on the line that says it is
      // working, so a chat that is busy can be told from one that is stuck.
      const busy = el.dataset.state === 'busy';
      const still = busy && quiet ? workingText(at, t) : 'Working…';
      if (working.textContent !== still) { working.textContent = still; working.title = busy && quiet ? fullTime(at) : ''; }
      const show = quiet && !busy;
      const text = show ? `Last message ${since(at, t)}, ${timeLabel(at, t, true)}` : '';
      const moved = tail.hidden === show;
      if (moved || (show && tail.textContent !== text)) { tail.hidden = !show; tail.textContent = text; tail.title = show ? fullTime(at) : ''; }
      return moved;
    }

    // Times are worded from now ("Yesterday", "3 days ago"), and a pane stays open for days: they are read again
    // when the pane is come back to and on a slow timer, and written only where the words changed.
    // On the timer only a pane that can be seen is worded again. The words of every pane change together (each
    // minute after a turn, and all of them at midnight), and a deck's worth of hidden panes laid out again in one
    // frame is a stall for the one being read. A hidden pane is worded when it is come back to.
    function retime(onTimer) {
      if (closed && model.ended === null) return;
      if (onTimer && typeof el.checkVisibility === 'function' && !el.checkVisibility({ visibilityProperty: true })) return;
      const moved = paintTail();
      for (const item of model.items) {
        if (item.kind !== 'time') continue;
        const node = nodes.get(item), text = markText(item);
        if (node && node.textContent !== text) node.textContent = text;
      }
      if (moved && atLatest) scroll.scrollTop = scroll.scrollHeight;
    }
    clocks.add(retime);

    function paintStatus() {
      const state = !connected && model.ended === null ? 'offline' : model.state;
      el.dataset.state = state;
      working.hidden = state !== 'busy';
      stateText.textContent = preparing ? 'Preparing image…'
        : resuming ? 'Resuming…'
        : reloading ? 'Reloading skills…'
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
      action.disabled = state === 'ended' || state === 'offline' || preparing || !!resuming || !!choosing || (!stop && nothing);
      input.disabled = state === 'ended';
      input.placeholder = !choosing ? 'Message ' + model.catalog.agent : opts.touch ? 'Type to filter' : 'Type to filter, Enter to open, Esc to close';
      paintPickers(state);
    }

    function options(select, pairs, value) {
      const want = pairs.map(p => p.join('\u0000')).join('\u0001');
      if (select.dataset.options !== want) {
        select.dataset.options = want;
        select.textContent = '';
        let under = select, heading = '';
        for (const [v, label, title, group] of pairs) {
          if ((group || '') !== heading) {
            heading = group || '';
            under = select;
            if (heading) { under = document.createElement('optgroup'); under.label = heading; select.appendChild(under); }
          }
          const o = document.createElement('option');
          o.value = v;
          o.textContent = label;
          if (title) o.title = title;
          under.appendChild(o);
        }
      }
      if (document.activeElement !== select) select.value = value;
      fit(select);
    }

    // A select is as wide as its longest option unless told otherwise; these are as wide as the choice they show.
    const ruler = h('span', 'pick-ruler');
    el.appendChild(ruler);
    // Measuring makes the browser lay the page out there and then, and this is asked on every drawing and every key
    // typed: it is done only when the words shown, or the look they are set in, have changed.
    let looks = 0;
    function fit(select) {
      const shown = select.selectedOptions[0];
      const text = shown ? shown.textContent : '';
      const fitted = looks + '\u0000' + text;
      if (select.dataset.fitted === fitted) return;
      ruler.textContent = text;
      // Its padding and edge differ between a desktop and a phone: read, not assumed.
      const box = getComputedStyle(select);
      const around = ['paddingLeft', 'paddingRight', 'borderLeftWidth', 'borderRightWidth'].reduce((sum, side) => sum + (parseFloat(box[side]) || 0), 0);
      const wide = ruler.getBoundingClientRect().width;
      select.style.width = Math.ceil(wide) + Math.ceil(around) + 'px';
      // Words that measured nothing were not laid out (a pane not on the page yet): they are measured again.
      if (wide > 0 || !text) select.dataset.fitted = fitted;
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
      const modelPairs = models.map(m => {
        const { group, label } = modelLabel(m.value, m.name);
        return [m.value, narrow ? shortModel(label) : label, m.description || '', group];
      });
      if (!inForce && s.model) modelPairs.push([s.model, s.model, '']);
      options(modelPick, modelPairs, inForce ? inForce.value : s.model);
      const levels = inForce && inForce.efforts && inForce.efforts.length ? inForce.efforts.slice() : EFFORTS.slice();
      if (s.effort && !levels.includes(s.effort)) levels.push(s.effort);
      options(effortPick, levels.map(l => [l, l, 'Effort']), s.effort);
      const modes = model.catalog.modes;
      const modePairs = modes.map(m => [m[0], narrow ? m[2] || m[1] : m[1]]);
      if (s.mode && !modes.some(m => m[0] === s.mode)) modePairs.push([s.mode, s.mode]);
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
        case 'time': {
          node = node || h('div', 'time-mark');
          node.textContent = markText(item);
          node.title = fullTime(item.at);
          return node;
        }
        case 'user': {
          node = node || h('div', 'msg user');
          node.classList.toggle('pending', !!item.pending);
          node.textContent = '';
          node.title = item.at ? fullTime(item.at) : '';
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
          node.innerHTML = markdown(item.text, !opts.touch);
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
        // Where a link to a file goes is not on show anywhere else (the deck has no status bar).
        if (FILE_LINK.test(a.getAttribute('href'))) a.title = a.getAttribute('href');
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
      // A run already drawn is kept, and only the calls that changed are drawn again. Each call is a <details>, and
      // making hundreds of them afresh for every call and result held the page for tens of milliseconds each time.
      const kept = node && node.classList.contains('tool-run') ? node : null;
      const fresh = kept || h('details', 'tool-run');
      if (overview.asking) fresh.open = true;
      if (fresh.dataset.state !== overview.state) fresh.dataset.state = overview.state;
      const summary = kept ? kept.firstElementChild : h('summary');
      summary.textContent = '';
      const latest = overview.going;
      summary.append(h('span', 'tool-name', overview.words),
        h('span', 'tool-brief', overview.running ? `${latest.name} ${toolBrief(latest.name, latest.input)}` : overview.breakdown));
      const word = overview.asking ? 'answer required' : overview.running ? 'running' : overview.failed ? `${overview.failed} failed` : '';
      if (word) summary.appendChild(h('span', 'tool-state', word));
      const body = kept ? kept.lastElementChild : h('div', 'run-body');
      if (!kept) fresh.append(summary, body);
      run.tools.forEach((tool, i) => {
        const now = body.children[i], same = !!now && now.dataset.tool === tool.id;
        if (same && now.drawnRev === tool.rev) return;
        const drawn = renderTool(tool, open);
        drawn.drawnRev = tool.rev;
        if (same) now.replaceWith(drawn);
        else body.insertBefore(drawn, now || null);
      });
      while (body.children.length > run.tools.length) body.lastElementChild.remove();
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
      // A timer that has been set is not doing anything: it is waiting to fire.
      const word = { asking: 'answer required', failed: req && req.decision === 'deny' ? 'declined' : 'failed', stopped: 'stopped', running: OUTLIVES_TURN.has(item.name) ? 'waiting' : 'running' }[state];
      summary.append(h('span', 'tool-name', item.name), h('span', 'tool-brief', toolBrief(item.name, item.input)));
      if (word) summary.appendChild(h('span', 'tool-state', word));
      const step = liveStep(item);
      if (step) summary.appendChild(h('span', 'tool-live', step));
      fresh.appendChild(summary);
      const body = h('div', 'tool-body');
      if (item.name === 'AskUserQuestion') body.appendChild(questionCard(item));
      else {
        const detail = toolDetail(item);
        if (detail) body.appendChild(detail);
        if (asking) body.appendChild(permissionCard(req));
      }
      if (item.children && item.children.length) body.appendChild(subActivity(item.children, open));
      if (item.result) {
        // A line that only says it worked is a line, not a block of output.
        const text = item.result.text.trim();
        if (!item.result.error && text.length <= 160 && !text.includes('\n')) { if (text) body.appendChild(h('div', 'tool-verdict', text)); }
        else body.appendChild(h('pre', 'tool-result' + (item.result.error ? ' error' : ''), clip(item.result.text, 12000)));
      }
      if (!body.firstChild) fresh.classList.add('bare');
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
      // A change that arrives as a diff already (Codex): each line says by its first character what it is.
      if (typeof input.diff === 'string' && input.diff) {
        const box = h('div', 'diff');
        const lines = input.diff.split('\n');
        for (const line of lines.slice(0, 400)) box.appendChild(h('div', 'diff-line ' + diffKind(line), line));
        if (lines.length > 400) box.appendChild(h('div', 'diff-line same', `… ${lines.length - 400} more lines`));
        return box;
      }
      if (typeof input.content === 'string' && (input.file_path || input.path)) return h('pre', 'tool-input', clip(input.content, 6000));
      // A command the line above already shows whole is not shown again under it.
      if (typeof input.command === 'string') return toolBrief(item.name, input) === input.command.trim() ? null : h('pre', 'tool-input', clip(input.command, 6000));
      // A subagent's job is prose written for it.
      if (typeof input.prompt === 'string' && input.prompt) return h('div', 'tool-prose', clip(input.prompt, 6000));
      // A call that was given nothing (the advisor reads the conversation itself) has nothing to show here.
      if (Object.keys(input).length === 0) return null;
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
        plan.innerHTML = markdown(text, !opts.touch);
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
      if (always && always.detail) {
        const rule = h('div', 'ask-rule', always.label + ' covers ');
        rule.appendChild(h('code', '', clip(always.detail, 600)));
        card.appendChild(rule);
      }
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

    // What is typed and not sent is kept by conversation, so a session restarted under it (an update does that
    // to one left alone) comes back with the words still in the box.
    function keepDraft() {
      if (!draftKey) return;
      try { if (input.value) localStorage.setItem(draftKey, input.value); else localStorage.removeItem(draftKey); } catch { }
    }

    function takeDraft(session) {
      if (!session || draftKey === DRAFT_PREFIX + session) return;
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

    let recall = new Recall(), recallSeeded = false;
    function seedRecall() {
      if (recallSeeded) return;
      recallSeeded = true;
      recall.seed(model.items.filter(i => i.kind === 'user' && !i.pending && typeof i.text === 'string').map(i => i.text));
    }
    function showRecalled(text) {
      if (text === null) return false;
      setBox(text);
      input.setSelectionRange(text.length, text.length);
      menuClosedFor = input.value;
      keepDraft();
      paintMenu();
      grow();
      paintStatus();
      return true;
    }
    // Up on the first line of the box brings back the message before; Down on its last line, the one after.
    function recallKey(ev) {
      if ((ev.key !== 'ArrowUp' && ev.key !== 'ArrowDown') || ev.shiftKey || ev.altKey || ev.ctrlKey || ev.metaKey || ev.isComposing) return false;
      const start = input.selectionStart, end = input.selectionEnd;
      if (start !== end) return false;
      if (ev.key === 'ArrowUp') {
        if (input.value.slice(0, start).includes('\n')) return false;
        seedRecall();
        if (!showRecalled(recall.older(input.value))) return false;
      } else {
        if (input.value.slice(end).includes('\n')) return false;
        if (!showRecalled(recall.newer())) return false;
      }
      ev.preventDefault();
      return true;
    }

    async function submit() {
      const text = input.value.trim();
      const taken = shots.filter(s => !s.error);
      if ((!text && !taken.length) || preparing || resuming) return;
      if (text) { seedRecall(); recall.push(text); }
      if (text === '/' + RESUME.name && !taken.length && hostRevision >= 4) { openPicker(); return; }
      const naming = taken.length || hostRevision < 5 ? null : renameAsked(text);
      if (naming !== null) { rename(naming); return; }
      // Only where the host can do it: anywhere else the words go to the agent, which may have a /skills of its own.
      const wanted = taken.length || !model.catalog.reload ? null : skillsAsked(text);
      if (wanted !== null) { skills(wanted); return; }
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
      setBox('');
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

    // ---- a name for the session ----

    // Asked of the host, never sent as a message: the host names the session where its agent keeps names.
    function rename(name) {
      if (!name) {
        model.add({ kind: 'note', tone: 'plain', text: 'Give the session a name: /rename <name>' });
        schedule();
        return;
      }
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      ws.send(JSON.stringify({ type: 'rename', request: 'n' + nextAsk++, name }));
      setBox('');
      keepDraft();
      paintMenu();
      grow();
    }

    // ---- the agent's skills ----

    // The reload asked of the host and not yet answered, with the timer that gives up on it.
    let reloading = null;

    function endReload() {
      if (reloading) clearTimeout(reloading.timer);
      reloading = null;
    }

    function skills(what) {
      const agent = model.catalog.agent;
      if (what === 'reload') {
        if (!ws || ws.readyState !== WebSocket.OPEN || reloading) return;
        const request = 'c' + nextAsk++;
        // The host answers when the agent has said its commands again. One that never does is not waited on for ever.
        const timer = setTimeout(() => {
          if (!reloading || reloading.request !== request) return;
          reloading = null;
          model.add({ kind: 'note', tone: 'error', text: `${agent} did not answer the reload.` });
          schedule();
        }, 15000);
        reloading = { request, timer };
        ws.send(JSON.stringify({ type: 'commands', request }));
      } else {
        model.add({ kind: 'note', tone: 'plain', text: what === 'list' ? skillsNote(agent, model.catalog.commands)
          : 'Usage: /skills lists the skills and commands, /skills reload reads them again from disk.' });
      }
      setBox('');
      keepDraft();
      paintMenu();
      grow();
      schedule();
    }

    function gotCommands(msg) {
      if (!reloading || msg.request !== reloading.request) return;
      endReload();
      model.add(msg.ok ? { kind: 'note', tone: 'plain', text: reloadNote(msg) }
        : { kind: 'note', tone: 'error', text: String(msg.error || 'The skills were not reloaded.') });
    }

    function gotRenamed(msg) {
      model.add(msg.ok ? { kind: 'note', tone: 'plain', text: 'Renamed to “' + String(msg.name || '') + '”' }
        : { kind: 'note', tone: 'error', text: String(msg.error || 'The session was not renamed.') });
    }

    // ---- another conversation ----

    // The list is the host's: it reads the folder the session runs in. The message box is the filter while it is open.
    function openPicker() {
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      choosing = { request: 'p' + nextAsk++, sessions: null, active: 0 };
      ws.send(JSON.stringify({ type: 'sessions', request: choosing.request }));
      input.focus();
      setBox('');
      keepDraft();
      grow();
      paintMenu();
      paintStatus();
    }

    function closePicker() {
      if (!choosing) return;
      choosing = null;
      setBox('');
      grow();
      paintMenu();
      paintStatus();
    }

    function gotSessions(msg) {
      if (!choosing || msg.request !== choosing.request) return;
      choosing.sessions = Array.isArray(msg.sessions) ? msg.sessions : [];
      paintMenu();
    }

    function resume(session) {
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      ws.send(JSON.stringify({ type: 'resume', request: 'r' + nextAsk++, session: session.id }));
      resuming = session.id;
      closePicker();
    }

    // Not carried out: the host says why. A pick that did not come up is also a line in the conversation, which says it there.
    function notResumed(msg) {
      resuming = '';
      if (msg.request && msg.error) model.add({ kind: 'note', tone: 'error', text: String(msg.error) });
      paintStatus();
    }

    function paintPicker() {
      const shown = choosing.sessions ? filterSessions(choosing.sessions, input.value) : [];
      if (choosing.active >= shown.length) choosing.active = 0;
      menu.hidden = false;
      menu.textContent = '';
      menu.classList.add('resume');
      const head = h('div', 'resume-head');
      head.appendChild(h('span', 'resume-heading', 'Resume a conversation'));
      const close = h('button', 'resume-close', 'Close');
      close.type = 'button';
      close.addEventListener('mousedown', ev => ev.preventDefault());
      close.addEventListener('click', () => { input.focus(); closePicker(); });
      head.appendChild(close);
      menu.appendChild(head);
      if (!shown.length) {
        menu.appendChild(h('div', 'resume-empty', !choosing.sessions ? 'Looking for conversations…'
          : choosing.sessions.length ? 'Nothing matches.' : 'No other conversations in this folder.'));
        return;
      }
      const now = Date.now();
      shown.forEach((session, i) => {
        const row = h('button', 'slash-option resume-row' + (i === choosing.active ? ' active' : ''));
        row.type = 'button';
        row.tabIndex = -1;
        row.setAttribute('role', 'option');
        row.setAttribute('aria-selected', String(i === choosing.active));
        row.dataset.session = session.id;
        row.appendChild(h('span', 'resume-title', session.title || 'Untitled conversation'));
        const meta = h('span', 'resume-meta');
        if (session.live) meta.appendChild(h('span', 'resume-live', 'open in another session'));
        meta.appendChild(h('span', 'resume-when', ago(session.at, now)));
        row.appendChild(meta);
        row.title = session.title || '';
        row.addEventListener('mousedown', ev => ev.preventDefault());
        row.addEventListener('click', () => resume(session));
        menu.appendChild(row);
      });
      const active = menu.querySelector('.resume-row.active');
      if (active) active.scrollIntoView({ block: 'nearest' });
    }

    function pickerKey(ev) {
      if (!choosing || ev.isComposing) return false;
      const shown = choosing.sessions ? filterSessions(choosing.sessions, input.value) : [];
      if (ev.key === 'ArrowDown' && shown.length) choosing.active = (choosing.active + 1) % shown.length;
      else if (ev.key === 'ArrowUp' && shown.length) choosing.active = (choosing.active - 1 + shown.length) % shown.length;
      else if (ev.key === 'Enter' && !ev.shiftKey) { ev.preventDefault(); if (shown[choosing.active]) resume(shown[choosing.active]); return true; }
      else if (ev.key === 'Escape') { ev.preventDefault(); closePicker(); return true; }
      else return false;
      ev.preventDefault();
      paintMenu();
      return true;
    }

    function paintMenu() {
      if (choosing) { paintPicker(); return; }
      menu.classList.remove('resume');
      const query = commandQuery(input.value);
      menuItems = query === null || menuClosedFor === input.value ? [] : matchCommands(offeredCommands(model.catalog.commands, hostRevision, model.catalog.reload), query);
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
      if (run && (!command.hint || command.bare)) { input.value = '/' + command.name; submit(); return; }
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

    input.addEventListener('input', () => {
      if (emptying) return;
      menuActive = 0;
      grow();
      // While a conversation is being picked the box is the filter, not a message being written.
      if (choosing) choosing.active = 0; else keepDraft();
      paintMenu();
      paintStatus();
    });
    input.addEventListener('keydown', ev => {
      if (pickerKey(ev)) return;
      if (menuKey(ev)) return;
      if (recallKey(ev)) return;
      // On a touch keyboard Return is a new line, as in any message box there; the button sends.
      if (ev.key === 'Enter' && !opts.touch && !ev.shiftKey && !ev.ctrlKey && !ev.altKey && !ev.isComposing) { ev.preventDefault(); submit(); }
      else if (ev.key === 'Escape') { ev.preventDefault(); interrupt(); }
    });
    input.addEventListener('focus', () => { if (opts.onFocus) opts.onFocus(); });
    // A tap on one of the menu's rows can take the focus for a moment on a touch screen: the menu closes only if the focus stays away.
    input.addEventListener('blur', () => setTimeout(() => {
      if (document.activeElement !== input && !menu.hidden && !choosing) { menuClosedFor = input.value; paintMenu(); }
    }, 250));
    // The message box keeps the focus through a press on the button, so the keyboard is never told it was left.
    action.addEventListener('mousedown', ev => ev.preventDefault());
    action.addEventListener('click', () => { input.focus(); if (action.classList.contains('stop')) interrupt(); else submit(); });
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
      sock.onmessage = ev => timed('chat-data', id, () => {
        if (typeof ev.data === 'string') {
          let msg;
          try { msg = JSON.parse(ev.data); } catch { return; }
          if (msg.type === 'snapshot') {
            // A host that can move between conversations says whether what follows begins mid-line; before that, a
            // scrollback that did not start at its first byte always might.
            if (startsOver(seq, epoch, msg)) { recall = new Recall(); recallSeeded = false; model.reset(); reader = new LineReader(msg.cut !== undefined ? !!msg.cut : msg.start > 0); paged = false; }
            if ((msg.epoch || 0) !== epoch) resuming = '';
            epoch = msg.epoch || 0;
            hostRevision = msg.chat || 0;
            model.attached(hostRevision);
            behind.hidden = hostRevision >= HOST_REPLAYS;
            seq = msg.start;
            // `start` says nothing about what came before: a viewer that reattaches asks for what follows the
            // last byte it has, and is answered from there. The host says whether its transcript goes back further.
            hostPages = msg.chat >= 3;
            if (!paged) model.earlier = !!msg.earlier;
            takeDraft(msg.session);
            if (msg.title) post({ type: 'title', id, title: msg.title });
          } else if (msg.type === 'live') model.live(msg.line);
          else if (msg.type === 'history') { gotEarlier(msg); return; }
          else if (msg.type === 'sessions') { gotSessions(msg); return; }
          else if (msg.type === 'resumed') notResumed(msg);
          else if (msg.type === 'renamed') gotRenamed(msg);
          else if (msg.type === 'commands') gotCommands(msg);
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
      });
      sock.onclose = () => {
        if (ws !== sock) return;
        ws = null;
        connected = false;
        asked = '';
        resuming = '';
        endReload();
        closePicker();
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
      focus() { retime(false); input.focus(); },
      // Type and send in one step, for a shell around the page that has its own way of taking words.
      send(text) { setBox(String(text || '')); grow(); return submit(); },
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
        // The colours are the stylesheet's, in a dark and a light set; the shell says which, and how see-through its panes are.
        el.dataset.scheme = look.onDark ? 'dark' : 'light';
        el.style.setProperty('--chat-alpha', String(look.alpha ?? 1));
        el.style.colorScheme = look.onDark ? 'dark' : 'light';
        looks++;
        schedule();
      },
      dispose() {
        closed = true;
        if (frame) cancelAnimationFrame(frame);
        if (later) clearTimeout(later);
        if (shownAgain) shownAgain.disconnect();
        resized.disconnect();
        widePanes.delete(applyWide);
        clocks.delete(retime);
        endReload();
        try { if (ws) ws.close(); } catch { }
        for (const shot of shots) URL.revokeObjectURL(shot.url);
        for (const item of model.items) for (const url of item.images || []) URL.revokeObjectURL(url);
      },
    };
  }

  root.ChatPane = Object.assign({ create }, api);
})(typeof window !== 'undefined' ? window : globalThis);
