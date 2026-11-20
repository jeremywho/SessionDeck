// node --test Tests/js
// The lines here are shaped like the ones Claude Code 2.1.322 wrote in stream-json mode, cut down to the fields the pane reads.
const test = require('node:test');
const assert = require('node:assert/strict');
const { ChatModel, LineReader, Wire, toolBrief, commandQuery, matchCommands, modelInForce, shortModel, lineDiff, runOverview, alwaysOffer, planExits, planText, renameAsked, modelLabel, Recall, liveStep, taskRunning, asks: asksPerson, offeredCommands, filterSessions, ago, startsOver, clampWide, diffKind } = require('../../wwwroot/chat.js');

const init = { type: 'system', subtype: 'init', session_id: 's1', model: 'claude-haiku-5-5' };
const echo = text => ({ type: 'user', isReplay: true, message: { role: 'user', content: text }, parent_tool_use_id: null });
const said = text => ({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text }] }, parent_tool_use_id: null });
const thought = text => ({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'thinking', thinking: text }] }, parent_tool_use_id: null });
const used = (id, name, input) => ({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'tool_use', id, name, input }] }, parent_tool_use_id: null });
const returned = (id, content, isError = false) => ({ type: 'user', message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: id, content, is_error: isError }] }, parent_tool_use_id: null });
const done = (extra = {}) => Object.assign({ type: 'result', subtype: 'success', is_error: false, result: 'ok' }, extra);
const asks = (requestId, toolUseId, name, input) => ({ type: 'control_request', request_id: requestId, request: { subtype: 'can_use_tool', tool_name: name, input, tool_use_id: toolUseId, description: 'wants to ' + name } });
const stream = event => ({ type: 'stream_event', event, parent_tool_use_id: null });
const blockStart = (index, type) => stream({ type: 'content_block_start', index, content_block: { type } });
const textDelta = (index, text) => stream({ type: 'content_block_delta', index, delta: { type: 'text_delta', text } });
const kinds = m => m.items.map(i => i.kind);
// Tool calls sit inside the run of calls they belong to; this is every call in the conversation, in order.
const calls = m => m.items.flatMap(i => i.kind === 'tools' ? i.tools : i.kind === 'tool' ? [i] : []);

test('a finished turn reads as the message, the tool with its result, and the reply', () => {
  const m = new ChatModel();
  for (const line of [echo('make the file'), used('t1', 'Write', { file_path: 'a.txt', content: 'x' }), returned('t1', 'File created'), said('Done.'), done()]) m.durable(line);
  assert.deepEqual(kinds(m), ['user', 'tools', 'assistant']);
  assert.equal(m.items[0].text, 'make the file');
  assert.deepEqual(calls(m)[0].result, { text: 'File created', error: false });
  assert.equal(m.items[2].text, 'Done.');
  assert.equal(m.state, 'idle');
});

test('a tool result given as blocks is read as text, and a failed one is marked', () => {
  const m = new ChatModel();
  m.durable(used('t1', 'Bash', { command: 'false' }));
  m.durable(returned('t1', [{ type: 'text', text: 'exit 1' }, { type: 'text', text: 'boom' }], true));
  assert.deepEqual(calls(m)[0].result, { text: 'exit 1\nboom', error: true });
});

test('streamed text becomes the finished message in place, never a second copy', () => {
  const m = new ChatModel();
  m.live(init);
  m.durable(echo('hi'));
  m.live(stream({ type: 'message_start' }));
  m.live(blockStart(0, 'text'));
  m.live(textDelta(0, 'Hel'));
  m.live(textDelta(0, 'lo'));
  assert.deepEqual(kinds(m), ['user', 'assistant']);
  assert.equal(m.items[1].text, 'Hello');
  assert.equal(m.items[1].draft, true);
  const draft = m.items[1];
  m.durable(said('Hello there'));
  assert.deepEqual(kinds(m), ['user', 'assistant']);
  assert.equal(m.items[1], draft);
  assert.equal(draft.text, 'Hello there');
  assert.equal(draft.draft, false);
});

test('finished messages that arrive after several blocks have streamed land on their own drafts, in order', () => {
  const m = new ChatModel();
  m.live(stream({ type: 'message_start' }));
  m.live(blockStart(0, 'text'));
  m.live(textDelta(0, 'first'));
  m.live(blockStart(1, 'tool_use'));
  m.live(blockStart(2, 'text'));
  m.live(textDelta(2, 'second'));
  m.durable(said('first.'));
  m.durable(used('t1', 'Read', { file_path: 'x' }));
  m.durable(said('second.'));
  assert.deepEqual(m.items.filter(i => i.kind === 'assistant').map(i => i.text), ['first.', 'second.']);
  assert.equal(m.items.filter(i => i.draft).length, 0);
});

test('a thinking block with nothing to show leaves no row behind', () => {
  const m = new ChatModel();
  m.live(blockStart(0, 'thinking'));
  m.durable(thought(''));
  assert.deepEqual(kinds(m), []);
  m.durable(thought('weighing it'));
  assert.deepEqual(kinds(m), ['thinking']);
});

test('a message typed here shows at once and is matched with the session copy, not doubled', () => {
  const m = new ChatModel();
  const mine = m.sent('do the thing');
  assert.equal(mine.pending, true);
  m.durable(echo('do the thing'));
  assert.deepEqual(kinds(m), ['user']);
  assert.equal(mine.pending, false);
  m.durable(echo('typed in another window'));
  assert.deepEqual(m.items.map(i => i.text), ['do the thing', 'typed in another window']);
});

test('a permission request waits for an answer and the answer closes it', () => {
  const m = new ChatModel();
  m.durable(echo('write it'));
  m.durable(used('t1', 'Write', { file_path: 'a.txt' }));
  m.durable(asks('r1', 't1', 'Write', { file_path: 'a.txt' }));
  assert.equal(m.state, 'waiting');
  const tool = calls(m)[0];
  assert.equal(tool.request.open, true);
  assert.equal(tool.request.description, 'wants to Write');
  m.durable(JSON.parse(Wire.allow(tool.request)));
  assert.equal(tool.request.open, false);
  assert.equal(tool.request.decision, 'allow');
  assert.equal(m.state, 'busy');
});

test('a declined request and a withdrawn one both stop waiting', () => {
  const m = new ChatModel();
  m.durable(echo('go'));
  m.durable(used('t1', 'Bash', { command: 'rm x' }));
  m.durable(asks('r1', 't1', 'Bash', { command: 'rm x' }));
  m.durable(JSON.parse(Wire.deny(calls(m)[0].request)));
  assert.equal(calls(m)[0].request.decision, 'deny');
  m.durable(used('t2', 'Bash', { command: 'ls' }));
  m.durable(asks('r2', 't2', 'Bash', { command: 'ls' }));
  assert.equal(m.state, 'waiting');
  m.durable({ type: 'control_cancel_request', request_id: 'r2' });
  assert.equal(calls(m)[1].request.decision, 'cancelled');
  assert.equal(m.state, 'busy');
});

test('a question carries the answer that was given', () => {
  const m = new ChatModel();
  const input = { questions: [{ question: 'Tea or coffee?', header: 'Drink', multiSelect: false, options: [{ label: 'Tea', description: 'Tea' }, { label: 'Coffee', description: 'Coffee' }] }] };
  m.durable(used('t1', 'AskUserQuestion', input));
  m.durable(asks('r1', 't1', 'AskUserQuestion', input));
  const req = m.items[0].request;
  const sent = JSON.parse(Wire.allow(req, { 'Tea or coffee?': 'Coffee' }));
  assert.deepEqual(sent.response.response.updatedInput.answers, { 'Tea or coffee?': 'Coffee' });
  assert.deepEqual(sent.response.response.updatedInput.questions, input.questions);
  m.durable(sent);
  assert.deepEqual(req.answers, { 'Tea or coffee?': 'Coffee' });
  assert.equal(toolBrief('AskUserQuestion', input), 'Tea or coffee?');
});

test('an interrupt keeps what was said so far and marks where it stopped', () => {
  const m = new ChatModel();
  m.durable(echo('count'));
  m.live(blockStart(0, 'text'));
  m.live(textDelta(0, '1\n2\n3'));
  m.durable({ type: 'user', message: { role: 'user', content: [{ type: 'text', text: '[Request interrupted by user]' }] } });
  m.durable(done({ subtype: 'error_during_execution', is_error: true }));
  assert.deepEqual(kinds(m), ['user', 'assistant', 'note']);
  assert.equal(m.items[1].text, '1\n2\n3');
  assert.equal(m.items[1].draft, false);
  assert.equal(m.items[2].text, 'Interrupted');
  assert.equal(m.state, 'idle');
});

test('earlier conversation read from the transcript does not look like a running turn', () => {
  const m = new ChatModel();
  m.durable(Object.assign(echo('old question'), { isReplay: undefined, history: true }));
  m.durable(Object.assign(said('old answer'), { history: true }));
  assert.deepEqual(kinds(m), ['user', 'assistant']);
  assert.equal(m.state, 'idle');
  m.durable(echo('new question'));
  assert.equal(m.state, 'busy');
});

test('a failed turn says why, a subagent stays out of the conversation, and the host can add a line', () => {
  const m = new ChatModel();
  m.durable(Object.assign(said('inside a subagent'), { parent_tool_use_id: 't9' }));
  m.durable(done({ subtype: 'error_max_turns', is_error: true, result: '' }));
  m.durable({ type: 'host', subtype: 'stderr', text: 'not logged in' });
  assert.deepEqual(m.items.map(i => [i.kind, i.tone, i.text]), [['note', 'error', 'error max turns'], ['note', 'error', 'not logged in']]);
});

test('text the CLI says for the person is a note, and markup meant for the model is not shown', () => {
  const m = new ChatModel();
  m.durable({ type: 'user', message: { role: 'user', content: 'Background task finished' } });
  m.durable({ type: 'user', message: { role: 'user', content: '<task-notification>x</task-notification>' } });
  assert.deepEqual(m.items.map(i => [i.kind, i.text]), [['note', 'Background task finished']]);
});

test('the session ending closes anything still open', () => {
  const m = new ChatModel();
  m.durable(used('t1', 'Write', {}));
  m.durable(asks('r1', 't1', 'Write', {}));
  m.exited(1);
  assert.equal(m.state, 'ended');
  assert.equal(calls(m)[0].request.open, false);
});

test('lines split anywhere in the byte stream, even inside a character, come out whole', () => {
  const bytes = new TextEncoder().encode(JSON.stringify({ type: 'assistant', text: 'héllo → wörld' }) + '\n' + JSON.stringify({ n: 2 }) + '\nnot json\n' + JSON.stringify({ n: 3 }));
  const r = new LineReader(false);
  const out = [];
  for (let i = 0; i < bytes.length; i += 7) out.push(...r.push(bytes.slice(i, i + 7)));
  assert.deepEqual(out, [{ type: 'assistant', text: 'héllo → wörld' }, { n: 2 }]);
  assert.deepEqual(r.push(new TextEncoder().encode('\n')), [{ n: 3 }]);
});

test('scrollback that starts mid-line drops the fragment', () => {
  const r = new LineReader(true);
  assert.deepEqual(r.push(new TextEncoder().encode('lf of a line"}\n{"n":1}\n')), [{ n: 1 }]);
});

test('what a viewer sends is what the CLI reads', () => {
  assert.deepEqual(JSON.parse(Wire.user('hello')), { type: 'user', message: { role: 'user', content: 'hello' }, parent_tool_use_id: null, session_id: '' });
  const stop = JSON.parse(Wire.interrupt());
  assert.equal(stop.type, 'control_request');
  assert.equal(stop.request.subtype, 'interrupt');
  assert.match(stop.request_id, /^interrupt-/);
  const deny = JSON.parse(Wire.deny({ id: 'r1', input: {} }));
  assert.equal(deny.response.request_id, 'r1');
  assert.equal(deny.response.response.behavior, 'deny');
});

test('a message with images goes as text and image blocks, and a setting change is named so the host can tell it', () => {
  const image = { mediaType: 'image/png', data: 'QUJD' };
  assert.equal(Wire.content('just words', []), 'just words');
  assert.deepEqual(Wire.content('look', [image]), [{ type: 'text', text: 'look' }, { type: 'image', source: { type: 'base64', media_type: 'image/png', data: 'QUJD' } }]);
  assert.deepEqual(Wire.content('', [image]).map(b => b.type), ['image']);
  const set = JSON.parse(Wire.set('set_model', { model: 'sonnet' }));
  assert.match(set.request_id, /^sd-set-/);
  assert.deepEqual(set.request, { subtype: 'set_model', model: 'sonnet' });
  assert.deepEqual(JSON.parse(Wire.set('apply_flag_settings', { settings: { effortLevel: 'low' } })).request, { subtype: 'apply_flag_settings', settings: { effortLevel: 'low' } });
  assert.notEqual(JSON.parse(Wire.set('set_permission_mode', { mode: 'plan' })).request_id, set.request_id);
});

test('a message sent with images keeps its previews and is matched with the session copy by words and count', () => {
  const m = new ChatModel();
  const mine = m.sent('what is this', ['blob:one', 'blob:two']);
  // Same words, one image: someone else's message, not the echo of this one.
  m.durable({ type: 'user', isReplay: true, message: { role: 'user', content: [{ type: 'text', text: 'what is this' }, { type: 'image', source: { type: 'base64', data: '', omitted: 9 } }] } });
  assert.equal(mine.pending, true);
  assert.deepEqual(m.items.map(i => [i.kind, i.text, i.imageCount || 0]), [['user', 'what is this', 0], ['user', 'what is this', 1]]);
  m.durable({ type: 'user', isReplay: true, message: { role: 'user', content: [{ type: 'text', text: 'what is this' }, { type: 'image', source: {} }, { type: 'image', source: {} }] } });
  assert.equal(mine.pending, false);
  assert.deepEqual(mine.images, ['blob:one', 'blob:two']);
  assert.equal(m.items.length, 2);

  // Images with no words are still a message.
  m.durable({ type: 'user', history: true, message: { role: 'user', content: [{ type: 'image', source: {} }] } });
  assert.deepEqual([m.items[2].text, m.items[2].imageCount], ['', 1]);
});

test('a command that is never echoed settles when its turn ends, and a late echo still finds it', () => {
  const m = new ChatModel();
  const typed = m.sent('/context');
  m.live(init);
  m.durable(said('## Context Usage'));
  m.durable(done());
  assert.equal(typed.pending, false);
  assert.deepEqual(kinds(m), ['user', 'assistant']);

  const late = m.sent('sent as the turn was ending');
  m.durable(done());
  assert.equal(late.pending, false);
  m.durable(echo('sent as the turn was ending'));
  assert.equal(m.items.filter(i => i.kind === 'user').length, 2);
  m.durable(done());
  m.durable(echo('sent as the turn was ending'));
  assert.equal(m.items.filter(i => i.kind === 'user').length, 3);
});

test('what a local command printed is a note, and markup meant for the model is never a bubble', () => {
  const m = new ChatModel();
  m.durable(echo('<local-command-stdout>Set model to `sonnet (claude-sonnet-5-5)`</local-command-stdout>'));
  m.durable(Object.assign(echo('<local-command-caveat>Caveat: generated by the user</local-command-caveat>'), { isReplay: undefined, history: true }));
  m.durable(Object.assign(echo('<command-name>/model</command-name>'), { isReplay: undefined, history: true }));
  m.durable(echo('<local-command-stdout></local-command-stdout>'));
  assert.deepEqual(m.items.map(i => [i.kind, i.text]), [['note', 'Set model to `sonnet (claude-sonnet-5-5)`']]);
  assert.equal(m.state, 'idle');
});

test('the session says what it offers and what is in force, and a refused change is said once', () => {
  const m = new ChatModel();
  m.setCatalog({ type: 'catalog', models: [{ value: 'haiku', resolved: 'claude-haiku-5-5', name: 'Haiku', efforts: ['low'] }], commands: [{ name: 'compact', description: 'Summarize', hint: '' }] });
  m.setSettings({ type: 'settings', mode: 'plan', model: 'claude-haiku-5-5', effort: 'low', resume: 'bypassPermissions' });
  assert.deepEqual(m.settings, { mode: 'plan', model: 'claude-haiku-5-5', effort: 'low', resume: 'bypassPermissions' });
  // A host from before plan approval says nothing of where the session was: that reads as unknown, not as an error.
  m.setSettings({ type: 'settings', mode: 'plan', model: 'claude-haiku-5-5', effort: 'low' });
  assert.equal(m.settings.resume, '');
  assert.equal(m.model, 'claude-haiku-5-5');
  m.reset();
  assert.equal(m.catalog.commands.length, 1);
  assert.equal(m.settings.mode, 'plan');

  m.live({ type: 'control_response', response: { subtype: 'error', request_id: 'sd-set-abc', error: 'Unknown model: nope' } });
  m.live({ type: 'control_response', response: { subtype: 'success', request_id: 'sd-set-abd', response: { mode: 'plan' } } });
  m.live({ type: 'control_response', response: { subtype: 'error', request_id: 'interrupt-1', error: 'nothing to interrupt' } });
  assert.deepEqual(m.items.map(i => [i.kind, i.tone, i.text]), [['note', 'error', 'Unknown model: nope']]);
});

test('typing a slash lists the commands, the exact name first, then those that start with it', () => {
  assert.equal(commandQuery('/'), '');
  assert.equal(commandQuery('/comp'), 'comp');
  assert.equal(commandQuery('/compact now'), null);
  assert.equal(commandQuery('see /compact'), null);
  assert.equal(commandQuery(' /compact'), null);
  assert.equal(commandQuery(''), null);

  const commands = ['recompact', 'compact', 'model', 'compact-all', 'Context'].map(name => ({ name, description: '', hint: '' }));
  assert.deepEqual(matchCommands(commands, '').map(c => c.name), ['recompact', 'compact', 'model', 'compact-all', 'Context']);
  assert.deepEqual(matchCommands(commands, 'compact').map(c => c.name), ['compact', 'compact-all', 'recompact']);
  assert.deepEqual(matchCommands(commands, 'CO').map(c => c.name), ['compact', 'compact-all', 'Context', 'recompact']);
  assert.deepEqual(matchCommands(commands, 'zzz'), []);
});

test('the model in force is found among aliases: the one picked here, else a named one over the default', () => {
  const models = [
    { value: 'default', resolved: 'claude-opus-5-5' }, { value: 'opus', resolved: 'claude-opus-5-5' },
    { value: 'claude-fable-5-2', resolved: 'claude-fable-5-2' }, { value: 'haiku', resolved: 'claude-haiku-5-5' },
  ];
  assert.equal(modelInForce(models, 'claude-opus-5-5', '').value, 'opus');
  assert.equal(modelInForce(models, 'claude-opus-5-5', 'default').value, 'default');
  assert.equal(modelInForce(models, 'claude-opus-5-5', 'haiku').value, 'opus');
  assert.equal(modelInForce(models, 'claude-fable-5-2', '').value, 'claude-fable-5-2');
  assert.equal(modelInForce(models, 'claude-haiku-5-5', '').value, 'haiku');
  assert.equal(modelInForce(models, 'something-else', ''), null);
  assert.equal(modelInForce([{ value: 'default', resolved: 'x' }], 'x', '').value, 'default');
});

test('a phone shows a model by the part of its name that tells it apart', () => {
  assert.equal(shortModel('Claude Fable 5.2'), 'Fable 5.2');
  assert.equal(shortModel('Default (recommended)'), 'Default');
  assert.equal(shortModel('Haiku'), 'Haiku');
  assert.equal(shortModel('Claude'), 'Claude');
  assert.equal(shortModel('claude-opus-5-5'), 'claude-opus-5-5');
});

test('calls made one after another are one run, and words, a question or a plan end it', () => {
  const m = new ChatModel();
  m.durable(echo('look around'));
  m.durable(used('t1', 'Read', { file_path: 'a' }));
  m.durable(used('t2', 'Read', { file_path: 'b' }));
  m.durable(returned('t1', 'A'));
  m.durable(used('t3', 'Grep', { pattern: 'x' }));
  assert.deepEqual(kinds(m), ['user', 'tools']);
  assert.deepEqual(m.items[1].tools.map(t => t.id), ['t1', 't2', 't3']);

  // A result repaints the run the call is drawn in, since the call has no row of its own.
  m.dirty.clear();
  m.durable(returned('t3', 'hit'));
  assert.deepEqual(Array.from(m.dirty).map(i => i.kind), ['tools']);

  m.durable(said('Found it.'));
  m.durable(used('t4', 'Edit', { file_path: 'a', old_string: 'x', new_string: 'y' }));
  m.durable(used('q1', 'AskUserQuestion', { questions: [] }));
  m.durable(used('t5', 'Bash', { command: 'ls' }));
  m.durable(used('p1', 'ExitPlanMode', { plan: '# Plan' }));
  m.durable(used('t6', 'Bash', { command: 'pwd' }));
  assert.deepEqual(kinds(m), ['user', 'tools', 'assistant', 'tools', 'tool', 'tools', 'tool', 'tools']);
  assert.deepEqual(m.items.filter(i => i.kind === 'tools').map(r => r.tools.length), [3, 1, 1, 1]);
  assert.deepEqual(calls(m).map(t => t.id), ['t1', 't2', 't3', 't4', 'q1', 't5', 'p1', 't6']);
});

test('a run says how many calls it holds, of what, and how it stands', () => {
  const tool = (name, extra) => Object.assign({ name, input: {}, result: null, request: null }, extra);
  const done = { result: { text: '', error: false } };
  const three = [tool('Read', done), tool('Read', done), tool('Grep', done)];
  const { going, ...overview } = runOverview(three);
  assert.deepEqual(overview, { words: '3 tool calls', breakdown: 'Read 2, Grep', failed: 0, asking: false, running: false, state: 'done' });
  assert.equal(going, three[2]);
  // While a run is at work the call it names is the latest still going, not the latest made.
  const slow = tool('Agent'), quick = tool('Read', done);
  assert.equal(runOverview([tool('Read', done), slow, quick]).going, slow);
  assert.equal(runOverview([tool('Read', done), tool('Bash', { result: { text: 'no', error: true } })]).state, 'failed');
  assert.equal(runOverview([tool('Read', done), tool('Bash')]).state, 'running');
  assert.equal(runOverview([tool('Read', done), tool('Bash', { stopped: true })]).state, 'done');
  assert.equal(runOverview([tool('Bash', { request: { open: false, decision: 'deny' } }), tool('Read', done)]).state, 'done');
  // Waiting on the person outranks everything else that is true of the run.
  assert.equal(runOverview([tool('Bash', { result: { text: 'no', error: true } }), tool('Write', { request: { open: true } })]).state, 'asking');
});

test('an edit reads as the lines that went and the lines that came, around the ones that stayed', () => {
  const show = d => d.map(l => l.op + l.text);
  assert.deepEqual(show(lineDiff('a\nb\nc', 'a\nB\nc')), [' a', '-b', '+B', ' c']);
  assert.deepEqual(show(lineDiff('hello', 'goodbye')), ['-hello', '+goodbye']);
  assert.deepEqual(show(lineDiff('a\nb', 'a\nb')), [' a', ' b']);
  assert.deepEqual(show(lineDiff('a\nb\nc\nd', 'a\nd')), [' a', '-b', '-c', ' d']);
  assert.deepEqual(show(lineDiff('a\nd', 'a\nb\nc\nd')), [' a', '+b', '+c', ' d']);
  assert.deepEqual(show(lineDiff('one\ntwo\nthree\nfour', 'zero\ntwo\nthree\nfive')), ['-one', '+zero', ' two', ' three', '-four', '+five']);
  assert.deepEqual(show(lineDiff('', 'new')), ['-', '+new']);

  // Every line of both texts is accounted for, in order, however the two differ.
  const before = Array.from({ length: 60 }, (_, i) => 'line ' + (i % 7)).join('\n');
  const after = Array.from({ length: 55 }, (_, i) => 'line ' + (i % 5)).join('\n');
  const d = lineDiff(before, after);
  assert.equal(d.filter(l => l.op !== '+').map(l => l.text).join('\n'), before);
  assert.equal(d.filter(l => l.op !== '-').map(l => l.text).join('\n'), after);

  // Past the size where comparing every pair of lines would stall the page, it is still a true account.
  const big = Array.from({ length: 700 }, (_, i) => 'x' + i).join('\n');
  const other = Array.from({ length: 700 }, (_, i) => 'y' + i).join('\n');
  const coarse = lineDiff(big, other);
  assert.equal(coarse.filter(l => l.op === '-').length, 700);
  assert.equal(coarse.filter(l => l.op === '+').length, 700);
});

test('a request keeps what the CLI offers to remember, and the offer is put in words or not made', () => {
  const m = new ChatModel();
  m.durable(used('t1', 'Write', { file_path: 'a.txt' }));
  const edits = [{ type: 'setMode', mode: 'acceptEdits', destination: 'session' }];
  m.durable({ type: 'control_request', request_id: 'r1', request: { subtype: 'can_use_tool', tool_name: 'Write', input: { file_path: 'a.txt' }, tool_use_id: 't1', permission_suggestions: edits } });
  const req = calls(m)[0].request;
  assert.deepEqual(req.suggestions, edits);
  assert.deepEqual(alwaysOffer(req.suggestions), { label: 'Allow all edits this session', detail: '' });

  const sent = JSON.parse(Wire.allow(req, null, req.suggestions));
  assert.deepEqual(sent.response.response, { behavior: 'allow', updatedInput: { file_path: 'a.txt' }, updatedPermissions: edits });
  assert.equal('updatedPermissions' in JSON.parse(Wire.allow(req)).response.response, false);
  m.durable(sent);
  assert.deepEqual(req.granted, edits);

  const rule = [{ type: 'addRules', rules: [{ toolName: 'PowerShell', ruleContent: 'Get-Date' }, { toolName: 'Read' }], behavior: 'allow', destination: 'localSettings' }];
  assert.deepEqual(alwaysOffer(rule), { label: 'Always allow in this folder', detail: 'PowerShell: Get-Date\nRead' });
  assert.equal(alwaysOffer([Object.assign({}, rule[0], { destination: 'session' })]).label, 'Always allow this session');
  assert.equal(alwaysOffer([Object.assign({}, rule[0], { destination: 'userSettings' })]).label, 'Always allow everywhere');
  // Nothing offered, a rule that denies, a place it cannot name, or two places at once: no button.
  assert.equal(alwaysOffer([]), null);
  assert.equal(alwaysOffer(undefined), null);
  assert.equal(alwaysOffer([Object.assign({}, rule[0], { behavior: 'deny' })]), null);
  assert.equal(alwaysOffer([Object.assign({}, rule[0], { destination: 'somewhereNew' })]), null);
  assert.equal(alwaysOffer([rule[0], Object.assign({}, rule[0], { destination: 'session' })]), null);
  assert.equal(alwaysOffer([{ type: 'setMode', mode: 'bypassPermissions', destination: 'session' }]), null);
});

test('a plan is approved into the mode the session was in before it planned, or one any session may take', () => {
  assert.deepEqual(planExits('bypassPermissions'), ['bypassPermissions', 'acceptEdits', 'default']);
  assert.deepEqual(planExits('acceptEdits'), ['acceptEdits', 'default']);
  assert.deepEqual(planExits('default'), ['default', 'acceptEdits']);
  assert.deepEqual(planExits(''), ['acceptEdits', 'default']);
  assert.deepEqual(planExits('plan'), ['acceptEdits', 'default']);

  const m = new ChatModel();
  m.durable(used('p1', 'ExitPlanMode', { plan: '# Plan' }));
  m.durable(asks('r1', 'p1', 'ExitPlanMode', { plan: '# Plan' }));
  const req = calls(m)[0].request;
  const approved = JSON.parse(Wire.allow(req, null, [{ type: 'setMode', mode: 'acceptEdits', destination: 'session' }]));
  assert.deepEqual(approved.response.response.updatedInput, { plan: '# Plan' });
  assert.deepEqual(approved.response.response.updatedPermissions, [{ type: 'setMode', mode: 'acceptEdits', destination: 'session' }]);
  const kept = JSON.parse(Wire.deny(req, 'keep planning'));
  assert.deepEqual(kept.response.response, { behavior: 'deny', message: 'keep planning' });
  assert.equal(JSON.parse(Wire.deny(req)).response.response.message, 'The user declined this.');
});

test('the plan is read from the request for approval, since the call itself can carry none', () => {
  const m = new ChatModel();
  // As Claude Code 2.1.322 sends it when plan mode was switched on mid-session: an empty call, the plan in the request.
  m.durable(used('p1', 'ExitPlanMode', {}));
  assert.equal(planText(calls(m)[0]), '');
  m.durable(asks('r1', 'p1', 'ExitPlanMode', { plan: '# The plan', planFilePath: 'C:/x/plan.md' }));
  assert.equal(planText(calls(m)[0]), '# The plan');

  // Read back from the transcript there is no request: the approval's own words carry the plan.
  const back = new ChatModel();
  back.durable(Object.assign(used('p1', 'ExitPlanMode', {}), { history: true }));
  const approval = ['User has approved your plan. You can now start coding.', '', '## Approved Plan:', '# The plan', '- one', ''].join('\n');
  back.durable(Object.assign(returned('p1', approval), { history: true }));
  assert.equal(planText(calls(back)[0]), ['# The plan', '- one'].join('\n'));

  assert.equal(planText({ input: { plan: '# In the call' }, request: null, result: null }), '# In the call');
  assert.equal(planText({ input: {}, request: null, result: { text: 'The user wants to keep planning.', error: true } }), '');
  assert.equal(planText({ input: {}, request: null, result: { text: 'no marker here', error: false } }), '');
});

// The lines a subagent's work arrives as, in the order Claude Code 2.1.322 wrote them: the call, the task it
// became, the subagent's own calls and their results under the call's id, progress, the end, the hand-back.
const under = (parent, line) => Object.assign({}, line, { parent_tool_use_id: parent });
const task = (subtype, toolUseId, extra) => Object.assign({ type: 'system', subtype, tool_use_id: toolUseId, task_id: 'k1' }, extra);

test('what a subagent does is kept under the call that started it, and the call says where it has got to', () => {
  const m = new ChatModel();
  m.durable(echo('count the files'));
  m.durable(used('a1', 'Agent', { description: 'Count files', prompt: 'Count the files in src.' }));
  m.durable(task('task_started', 'a1', { description: 'Count files', is_backgrounded: false }));
  m.durable(under('a1', { type: 'user', message: { role: 'user', content: [{ type: 'text', text: 'Count the files in src.' }] } }));
  const agent = calls(m)[0];
  assert.equal(liveStep(agent), '');
  assert.equal(taskRunning(agent), true);

  m.dirty.clear();
  m.durable(under('a1', used('c1', 'Glob', { pattern: 'src/**' })));
  m.durable(task('task_progress', 'a1', { description: 'Finding src/**', last_tool_name: 'Glob', usage: { tool_uses: 1 } }));
  // The job it was handed is not a message, and nothing of the subagent's is an item of the conversation.
  assert.deepEqual(kinds(m), ['user', 'tool']);
  assert.deepEqual(calls(m).map(t => t.id), ['a1']);
  assert.deepEqual(agent.children.map(c => [c.kind, c.id, c.name]), [['tool', 'c1', 'Glob']]);
  assert.equal(agent.children[0].parent, agent);
  // What is drawn again is the row holding the call, however deep the change was.
  assert.deepEqual(Array.from(m.dirty), [m.items[1]]);
  assert.equal(liveStep(agent), 'Finding src/**');

  m.dirty.clear();
  m.durable(under('a1', returned('c1', 'a.js b.js')));
  assert.deepEqual(agent.children[0].result, { text: 'a.js b.js', error: false });
  assert.deepEqual(Array.from(m.dirty), [m.items[1]]);
  m.durable(under('a1', { type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text: 'Two files.' }, { type: 'tool_use', id: 'c2', name: 'Read', input: { file_path: 'a.js' } }] } }));
  m.durable(task('task_progress', 'a1', { description: 'Reading a.js', usage: { tool_uses: 2 } }));
  assert.deepEqual(agent.children.map(c => c.kind === 'tool' ? c.id : c.text), ['c1', 'Two files.', 'c2']);
  assert.equal(liveStep(agent), 'Reading a.js, 2 tool uses');
  assert.equal(runOverview([agent]).running, true);

  m.durable(task('task_notification', 'a1', { status: 'completed', summary: 'done' }));
  assert.equal(taskRunning(agent), false);
  m.durable(returned('a1', 'There are 2 files.'));
  assert.equal(liveStep(agent), '');
  assert.deepEqual(agent.result, { text: 'There are 2 files.', error: false });
  // Lines about a call this never saw (the scrollback began after it) are let go.
  m.durable(under('gone', used('c9', 'Read', {})));
  m.durable(task('task_progress', 'gone', { description: 'x' }));
  assert.deepEqual(kinds(m), ['user', 'tool']);
  // Each subagent is a line of its own, so two at work at once each say where they are; other calls still gather.
  m.durable(used('a2', 'Agent', { description: 'Another', prompt: 'p' }));
  m.durable(used('r1', 'Read', { file_path: 'a' }));
  m.durable(used('r2', 'Read', { file_path: 'b' }));
  assert.deepEqual(kinds(m), ['user', 'tool', 'tool', 'tools']);
});

test('a question from inside a subagent is a question of the call and of its run', () => {
  const m = new ChatModel();
  m.durable(used('r1', 'Read', { file_path: 'a' }));
  m.durable(used('a1', 'Agent', { description: 'Tidy up', prompt: 'p' }));
  m.durable(task('task_started', 'a1', { description: 'Tidy up' }));
  m.durable(under('a1', used('c1', 'Bash', { command: 'del x' })));
  const agent = calls(m)[1];
  assert.equal(asksPerson(agent), false);
  m.dirty.clear();
  m.durable(asks('q1', 'c1', 'Bash', { command: 'del x' }));
  assert.equal(m.state, 'waiting');
  assert.equal(agent.children[0].request.open, true);
  assert.equal(asksPerson(agent), true);
  assert.equal(runOverview([calls(m)[0], agent]).state, 'asking');
  assert.deepEqual(Array.from(m.dirty), [m.items[1]]);
  m.durable({ type: 'control_response', response: { subtype: 'success', request_id: 'q1', response: { behavior: 'allow', updatedInput: {} } } });
  assert.equal(asksPerson(agent), false);
});

test('a subagent sent to the background is at work after its call returned and after the turn ended', () => {
  const m = new ChatModel();
  m.durable(used('a1', 'Agent', { description: 'Long job', prompt: 'p' }));
  m.durable(task('task_started', 'a1', { description: 'Long job', is_backgrounded: true }));
  m.durable(returned('a1', 'Started in the background.'));
  m.durable(under('a1', used('c1', 'Bash', { command: 'sleep 60' })));
  m.durable(task('task_progress', 'a1', { description: 'Running sleep 60', usage: { tool_uses: 1 } }));
  m.durable(done());
  const agent = calls(m)[0];
  assert.equal(taskRunning(agent), true);
  assert.equal(agent.children[0].stopped, undefined);
  assert.equal(liveStep(agent), 'Running sleep 60');
  assert.equal(runOverview([agent]).running, true);
  m.durable(task('task_notification', 'a1', { status: 'completed' }));
  assert.equal(taskRunning(agent), false);
  assert.equal(runOverview([agent]).running, false);

  // One in the foreground ends with the turn, its unfinished calls with it.
  const f = new ChatModel();
  f.durable(used('a2', 'Agent', { description: 'Short job', prompt: 'p' }));
  f.durable(task('task_started', 'a2', { description: 'Short job', is_backgrounded: false }));
  f.durable(under('a2', used('c2', 'Bash', { command: 'x' })));
  f.durable(done());
  assert.equal(taskRunning(calls(f)[0]), false);
  assert.equal(calls(f)[0].children[0].stopped, true);
});

// Lines as the host reads them back from a transcript: marked as history, each with its place in the file.
const old = (at, line) => Object.assign({}, line, { history: true, at, uuid: 'u' + at });

test('the oldest message shown says what to ask the host for: its place in the transcript, else its uuid', () => {
  const m = new ChatModel();
  assert.equal(m.historyAsk(), null);
  m.durable({ type: 'host', subtype: 'note', text: 'not a message' });
  assert.equal(m.historyAsk(), null);
  m.durable(old(4200, echo('from the transcript')));
  m.durable(old(4800, said('answer')));
  assert.deepEqual(m.historyAsk(), { before: 4200, uuid: 'u4200' });
  const placed = new ChatModel();
  placed.durable(Object.assign(echo('a place and no uuid'), { history: true, at: 77 }));
  assert.deepEqual(placed.historyAsk(), { before: 77 });

  // A scrollback that begins with a line that arrived live knows that line only by its uuid.
  const live = new ChatModel();
  live.durable(under('a1', Object.assign(used('c1', 'Read', {}), { uuid: 'sub' })));
  live.durable(Object.assign(said('live answer'), { uuid: 'abc-1' }));
  assert.deepEqual(live.historyAsk(), { uuid: 'abc-1' });
  const bare = new ChatModel();
  bare.durable(said('no uuid at all'));
  assert.equal(bare.historyAsk(), null);
  m.reset();
  assert.equal(m.historyAsk(), null);
});

test('a page of earlier conversation goes in ahead of what is shown, and a call meets the result that came before it', () => {
  const m = new ChatModel();
  // The scrollback starts with the result of a call made before it.
  m.durable(old(900, returned('t2', 'the file')));
  m.durable(old(950, said('It says hello.')));
  m.durable(done());
  m.earlier = true;
  m.dirty.clear();
  const shown = m.items.slice();

  const added = m.prepend([old(500, echo('read both')), old(600, used('t1', 'Read', { file_path: 'gone' })), old(700, used('t2', 'Read', { file_path: 'a' }))], 480);
  assert.equal(added, 2);
  assert.deepEqual(kinds(m), ['user', 'tools', 'assistant']);
  assert.deepEqual(m.items.slice(2), shown);
  assert.deepEqual(Array.from(m.dirty), m.items.slice(0, 2));
  assert.deepEqual(calls(m).map(t => [t.id, t.result && t.result.text, !t.result && !!t.stopped]), [['t1', null, true], ['t2', 'the file', false]]);
  assert.deepEqual(m.historyAsk(), { before: 500, uuid: 'u500' });
  assert.equal(m.earlier, true);

  // The first page there is: nothing before it, and a result of its own with no call is kept for no one.
  assert.equal(m.prepend([old(100, echo('the very first'))], null), 1);
  assert.deepEqual(m.items.slice(0, 2).map(i => i.text), ['the very first', 'read both']);
  assert.deepEqual(m.historyAsk(), { before: 100, uuid: 'u100' });
  assert.equal(m.earlier, false);

  // A page with nothing in it changes nothing but that.
  m.earlier = true;
  assert.equal(m.prepend([], null), 0);
  assert.deepEqual(m.historyAsk(), { before: 100, uuid: 'u100' });
  assert.equal(m.earlier, false);
  // A call loaded from an earlier page is still the call a late line is about.
  m.durable(returned('t1', 'found after all'));
  assert.equal(calls(m)[0].result.text, 'found after all');
});

test('a call read back from the transcript without its result was cut off, not left running', () => {
  const m = new ChatModel();
  m.durable(old(100, echo('read it')));
  m.durable(old(200, used('t1', 'Read', { file_path: 'a' })));
  m.durable(old(300, returned('t1', 'text')));
  m.durable(old(400, used('t2', 'Bash', { command: 'sleep 600' })));
  assert.deepEqual(calls(m).map(t => [t.id, !!t.result, !!t.stopped]), [['t1', true, true], ['t2', false, true]]);
  const overview = runOverview(m.items[1].tools);
  assert.deepEqual([overview.running, overview.state], [false, 'done']);
  assert.equal(m.state, 'idle');
  // The same call made now is at work until its result comes.
  m.durable(used('t3', 'Bash', { command: 'sleep 1' }));
  assert.equal(runOverview(m.items[1].tools).running, true);
});

test('a run of calls split by where a page ends is one run once the page is in', () => {
  const m = new ChatModel();
  m.durable(old(500, used('t3', 'Read', { file_path: 'c' })));
  m.durable(old(600, returned('t3', 'c')));
  m.durable(old(700, said('All read.')));
  const run = m.items[0];
  m.dirty.clear();
  assert.equal(m.prepend([old(100, echo('read three')), old(200, used('t1', 'Read', { file_path: 'a' })), old(300, returned('t1', 'a')), old(400, used('t2', 'Read', { file_path: 'b' }))], null), 1);
  assert.deepEqual(kinds(m), ['user', 'tools', 'assistant']);
  // The run already on the page is the one kept, so it is drawn again where it stands.
  assert.equal(m.items[1], run);
  assert.deepEqual(run.tools.map(t => [t.id, t.run === run]), [['t1', true], ['t2', true], ['t3', true]]);
  assert.deepEqual(Array.from(m.dirty), [run, m.items[0]]);
  // Words between them keep two runs two.
  const apart = new ChatModel();
  apart.durable(old(500, used('t3', 'Read', {})));
  apart.prepend([old(200, used('t1', 'Read', {})), old(300, said('Now the next.'))], null);
  assert.deepEqual(kinds(apart), ['tools', 'assistant', 'tools']);
});

test('/resume is offered by the pane, first, to a host that can move between conversations', () => {
  const cli = [{ name: 'compact', description: 'Summarize' }, { name: 'context', description: 'Show context' }];
  // The CLI does not list it on pipes; a host from before it could move gets nothing added.
  assert.deepEqual(offeredCommands(cli, 3).map(c => c.name), ['compact', 'context']);
  assert.deepEqual(offeredCommands(cli, 4).map(c => c.name), ['resume', 'compact', 'context']);
  assert.equal(offeredCommands(cli, 4)[0].hint, '');
  // Should the CLI ever list one of its own, there is still one, and it is the pane's.
  const both = offeredCommands(cli.concat([{ name: 'resume', description: 'the CLI one' }]), 4);
  assert.deepEqual(both.map(c => c.name), ['resume', 'compact', 'context']);
  assert.notEqual(both[0].description, 'the CLI one');
  assert.deepEqual(matchCommands(offeredCommands(cli, 4), 'res').map(c => c.name), ['resume']);
});

test('the list of conversations is narrowed by every word typed, in the order it came', () => {
  const list = [{ id: 'a', title: 'Fix the login bug' }, { id: 'b', title: 'Bees and wasps' }, { id: 'c', title: '' }, { id: 'd', title: 'login page copy' }];
  assert.deepEqual(filterSessions(list, '').map(s => s.id), ['a', 'b', 'c', 'd']);
  assert.deepEqual(filterSessions(list, '   ').map(s => s.id), ['a', 'b', 'c', 'd']);
  assert.deepEqual(filterSessions(list, 'LOGIN').map(s => s.id), ['a', 'd']);
  assert.deepEqual(filterSessions(list, 'bug login').map(s => s.id), ['a']);
  assert.deepEqual(filterSessions(list, 'zebra').map(s => s.id), []);
});

test('when a conversation was last touched is said the way a person would', () => {
  const now = Date.UTC(2026, 10, 12, 12, 0, 0), minute = 60000, hour = 60 * minute, day = 24 * hour;
  assert.equal(ago(now - 20000, now), 'just now');
  assert.equal(ago(now + 5000, now), 'just now');
  assert.equal(ago(now - 59 * minute, now), '59m ago');
  assert.equal(ago(now - 60 * minute, now), '1h ago');
  assert.equal(ago(now - 23 * hour - 59 * minute, now), '23h ago');
  assert.equal(ago(now - day, now), '1d ago');
  assert.equal(ago(now - 29 * day, now), '29d ago');
  assert.match(ago(now - 45 * day, now), /2026/);
});

test('a viewer starts over when the scrollback does not go on from its last byte, or the host moved to another conversation', () => {
  // Caught up, same conversation: what follows is appended.
  assert.equal(startsOver(500, 0, { start: 500, epoch: 0 }), false);
  // A host from before conversations could change says no epoch at all.
  assert.equal(startsOver(500, 0, { start: 500 }), false);
  assert.equal(startsOver(500, 0, { start: 900 }), true);
  // The host moved while this viewer was away and answers from the very byte it had reached: still another conversation.
  assert.equal(startsOver(500, 0, { start: 500, epoch: 1 }), true);
  assert.equal(startsOver(500, 1, { start: 500, epoch: 1 }), false);
  assert.equal(startsOver(0, 0, { start: 0, epoch: 3 }), true);
});

test('the column can be dragged no narrower than reads well and no wider than its pane', () => {
  assert.equal(clampWide(900.4, 1400), 900);
  assert.equal(clampWide(200, 1400), 480);
  assert.equal(clampWide(2000, 1400), 1400);
  // A pane narrower than the least width is simply filled: the least width is still what is asked for.
  assert.equal(clampWide(300, 400), 480);
  assert.equal(clampWide(1000, 400), 480);
});

test('an agent other than Claude says its own name and modes, and Claude\'s stay the default', () => {
  const m = new ChatModel();
  assert.equal(m.catalog.agent, 'Claude');
  assert.deepEqual(m.catalog.modes.map(x => x[0]), ['default', 'acceptEdits', 'plan', 'bypassPermissions']);
  m.setCatalog({ models: [{ value: 'gpt-x', name: 'GPT X', efforts: ['low', 'ultra'] }], commands: [], agent: 'Codex', modes: [['default', 'Ask before acting', 'Ask'], ['bypassPermissions', 'Full access', 'Full']] });
  assert.equal(m.catalog.agent, 'Codex');
  assert.deepEqual(m.catalog.modes.map(x => x[1]), ['Ask before acting', 'Full access']);
  // A frame without them (every Claude host) or with something that is not a list of pairs leaves Claude's.
  m.setCatalog({ models: [], commands: [], modes: ['nonsense'] });
  assert.equal(m.catalog.agent, 'Claude');
  assert.equal(m.catalog.modes.length, 4);
});

test('a line of a diff is told by its first character, and a file header is neither added nor removed', () => {
  assert.deepEqual(['+new', '-old', ' same', '@@ -1 +1 @@', '+++ b/file', '--- a/file', ''].map(diffKind), ['add', 'del', 'same', 'same', 'same', 'same', 'same']);
});

test('/rename is the pane\'s to a host that names sessions, and the CLI\'s own before that', () => {
  const cli = [{ name: 'compact', description: 'Summarize' }, { name: 'rename', description: 'the CLI one' }];
  // A host from before revision 5 would drop the request: the text goes on as typed, to the CLI's own command.
  assert.deepEqual(offeredCommands(cli, 4).map(c => c.name + ':' + c.description), ['resume:Resume a conversation from this folder', 'compact:Summarize', 'rename:the CLI one']);
  const now = offeredCommands(cli, 5);
  assert.deepEqual(now.map(c => c.name), ['resume', 'rename', 'compact']);
  assert.deepEqual([now[1].description, now[1].hint], ['Name this session', '<name>']);
  // An agent that lists none (Codex, Antigravity, OpenCode) is offered it all the same.
  assert.deepEqual(offeredCommands([{ name: 'goal', description: 'Set a goal' }], 5).map(c => c.name), ['resume', 'rename', 'goal']);
  assert.deepEqual(matchCommands(offeredCommands(cli, 5), 'ren').map(c => c.name), ['rename']);
});

test('what /rename asks for is the name on one line, and only /rename asks for it', () => {
  assert.equal(renameAsked('/rename ship the thing'), 'ship the thing');
  assert.equal(renameAsked('  /rename   ship \n  the\tthing  '), 'ship the thing');
  assert.equal(renameAsked('/rename'), '');
  assert.equal(renameAsked('/rename   '), '');
  assert.equal(renameAsked('/rename /resume'), '/resume');
  for (const other of ['/renamed it', '/renameship', 'please /rename this', '/resume', 'rename this', '/Rename x', '']) assert.equal(renameAsked(other), null, other);
});

test('a model listed as provider/Name shows as the name under its provider, and any other name as it is', () => {
  assert.deepEqual(modelLabel('swift/swift-1.5', 'swift/Swift 1.5 (ultimac)'), { group: 'swift', label: 'Swift 1.5 (ultimac)' });
  assert.deepEqual(modelLabel('opencode-go/kimi-k3', 'opencode-go/Kimi K3'), { group: 'opencode-go', label: 'Kimi K3' });
  // No provider in the id, or a name that does not repeat it: nothing is cut.
  assert.deepEqual(modelLabel('fable', 'Claude Fable 5.2'), { group: '', label: 'Claude Fable 5.2' });
  assert.deepEqual(modelLabel('gemini-4-carbon', 'Gemini 4 Carbon'), { group: '', label: 'Gemini 4 Carbon' });
  assert.deepEqual(modelLabel('swift/swift-1.5', 'Swift 1.5'), { group: '', label: 'Swift 1.5' });
  assert.deepEqual(modelLabel('swift/swift-1.5', 'swift/'), { group: '', label: 'swift/' });
  assert.deepEqual(modelLabel('/odd', '/odd'), { group: '', label: '/odd' });
  // A model the catalog has no name for is shown by its id, whole.
  assert.deepEqual(modelLabel('opencode/big-pickle', ''), { group: 'opencode', label: 'big-pickle' });
  assert.deepEqual(modelLabel('haiku', undefined), { group: '', label: 'haiku' });
});

test('Up brings back earlier messages newest first, Down returns, and past the newest gives back what was being typed', () => {
  const r = new Recall();
  assert.equal(r.older('typing'), null);
  assert.equal(r.newer(), null);
  r.seed(['first', '  second  ', '', 'second', 'third']);
  assert.deepEqual(r.list, ['first', 'second', 'third']);
  assert.equal(r.older('half typed'), 'third');
  assert.equal(r.older('third'), 'second');
  assert.equal(r.older('second'), 'first');
  assert.equal(r.older('first'), null);
  assert.equal(r.newer(), 'second');
  assert.equal(r.newer(), 'third');
  assert.equal(r.newer(), 'half typed');
  assert.equal(r.newer(), null);
  // Sending ends the recall, and what was sent is the newest; the same message twice in a row is kept once.
  r.older('');
  r.push('/rename ship it');
  r.push('/rename ship it');
  assert.deepEqual(r.list.slice(-2), ['third', '/rename ship it']);
  assert.equal(r.older(''), '/rename ship it');
  r.push('   ');
  assert.equal(r.at, -1);
  assert.equal(r.older(''), '/rename ship it');
});

// Shaped as Claude Code 2.1.325 said them in a chat on Opus 5.5 with an advisor model set (C:/Temp/chatlab/adv1.jsonl).
const ADVISOR_CALL = { type: 'assistant', message: { id: 'msg_1', role: 'assistant', content: [{ type: 'server_tool_use', id: 'srvtoolu_014aFH', name: 'advisor', input: {} }] }, parent_tool_use_id: null };
const advisorResult = content => ({ type: 'assistant', message: { id: 'msg_1', role: 'assistant', content: [{ type: 'advisor_tool_result', tool_use_id: 'srvtoolu_014aFH', content }] }, parent_tool_use_id: null });

test('a consult of the advisor is a call of its own that runs until its result comes in the model\'s message', () => {
  const m = new ChatModel();
  m.durable({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'tool_use', id: 't1', name: 'Read', input: { file_path: 'a.txt' } }] }, parent_tool_use_id: null });
  m.durable(ADVISOR_CALL);
  const advisor = m.items[m.items.length - 1];
  assert.deepEqual([advisor.kind, advisor.name, advisor.result, m.items.length], ['tool', 'Advisor', null, 2]);
  assert.equal(m.items[0].kind, 'tools');

  // What it said is the model's to read: the service hands the pane a sealed copy.
  m.durable(advisorResult({ type: 'advisor_redacted_result', encrypted_content: 'EswDCioIFRgC' }));
  assert.deepEqual(advisor.result, { text: 'Reviewed the conversation. Its advice went to the model and is not shown here.', error: false });
  m.durable({ type: 'assistant', message: { id: 'msg_1', role: 'assistant', content: [{ type: 'text', text: 'advisor said yes' }] }, parent_tool_use_id: null });
  assert.deepEqual(m.items.map(i => i.kind), ['tools', 'tool', 'assistant']);
});

test('advice given in the open is shown, and an advisor that could not be reached says so', () => {
  const open = new ChatModel();
  open.durable(ADVISOR_CALL);
  open.durable(advisorResult({ type: 'advisor_result', text: 'Check the empty case first.' }));
  assert.deepEqual(open.items[0].result, { text: 'Check the empty case first.', error: false });

  const down = new ChatModel();
  down.durable(ADVISOR_CALL);
  down.durable(advisorResult({ type: 'advisor_tool_result_error', error_code: 'overloaded' }));
  assert.deepEqual(down.items[0].result, { text: 'Advisor unavailable (overloaded)', error: true });

  // A result for a call this pane never saw (it attached later) changes nothing.
  const late = new ChatModel();
  late.durable(advisorResult({ type: 'advisor_result', text: 'x' }));
  assert.equal(late.items.length, 0);
});

test('a conversation read back shows a finished consult as done and one cut short as stopped', () => {
  const m = new ChatModel();
  m.durable({ ...ADVISOR_CALL, history: true });
  assert.equal(m.items[0].stopped, true);
  m.durable({ ...advisorResult({ type: 'advisor_redacted_result', encrypted_content: 'x' }), history: true });
  assert.equal(m.items[0].result.error, false);
});

test('another tool the service runs is shown under its own name with what it returned', () => {
  const m = new ChatModel();
  m.durable({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'server_tool_use', id: 's1', name: 'web_search', input: { query: 'windows conpty' } }] }, parent_tool_use_id: null });
  assert.deepEqual([m.items[0].kind, m.items[0].tools[0].name, m.items[0].tools[0].input.query], ['tools', 'web_search', 'windows conpty']);
  m.durable({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'web_search_tool_result', tool_use_id: 's1', content: [{ type: 'text', text: 'three results' }] }] }, parent_tool_use_id: null });
  assert.deepEqual(m.items[0].tools[0].result, { text: 'three results', error: false });
});


// ---- times ----
// Built with local constructors, so a day is a day wherever the tests run.
const { timeLabel, since, pauseBefore, PAUSE_MS } = require('../../wwwroot/chat.js');
const day = (y, mo, d, h = 12, mi = 0, s = 0) => new Date(y, mo - 1, d, h, mi, s).getTime();
const stamped = (line, t) => Object.assign({}, line, { timestamp: new Date(t).toISOString() });
const clockOf = t => new Date(t).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
const marks = m => m.items.filter(i => i.kind === 'time').map(i => i.at);

test('a conversation starts with its time, and says the time again after an hour of quiet', () => {
  const m = new ChatModel();
  const t = [day(2026, 11, 10, 10, 0), day(2026, 11, 10, 10, 5), day(2026, 11, 10, 11, 4), day(2026, 11, 10, 11, 5), day(2026, 11, 10, 12, 5), day(2026, 11, 10, 12, 6)];
  for (const line of [stamped(echo('one'), t[0]), stamped(said('first'), t[1]), stamped(echo('two'), t[2]), stamped(said('second'), t[3]), stamped(echo('three'), t[4]), stamped(said('third'), t[5])]) m.durable(line);
  // 59 minutes after the first reply is not a pause; 60 minutes after the second is.
  assert.deepEqual(kinds(m), ['time', 'user', 'assistant', 'user', 'assistant', 'time', 'user', 'assistant']);
  assert.deepEqual(marks(m), [t[0], t[4]]);
  assert.equal(m.lastAt, t[5]);
  assert.equal(m.items[1].at, t[0]);
  // Each mark is handed to the page to draw, like any other item.
  assert.ok(m.dirty.has(m.items[0]) && m.dirty.has(m.items[5]));
});

test('a new day is said even when little time has passed', () => {
  const m = new ChatModel();
  const late = day(2026, 11, 10, 23, 50), early = day(2026, 11, 11, 0, 10);
  for (const line of [stamped(echo('before midnight'), late), stamped(said('ok'), late + 60000), stamped(echo('after midnight'), early)]) m.durable(line);
  assert.deepEqual(kinds(m), ['time', 'user', 'assistant', 'time', 'user']);
  assert.deepEqual(marks(m), [late, early]);
});

test('lines that carry no time get no marks, and neither does a subagent\'s side', () => {
  const m = new ChatModel();
  for (const line of [echo('hello'), said('hi'), done()]) m.durable(line);
  assert.deepEqual(kinds(m), ['user', 'assistant']);
  assert.equal(m.lastAt, null);
  const t = day(2026, 11, 10, 10, 0);
  for (const line of [stamped(echo('run it'), t), stamped(used('a1', 'Agent', { prompt: 'look' }), t + 1000),
    stamped(Object.assign(said('from the subagent'), { parent_tool_use_id: 'a1' }), t + 3 * PAUSE_MS)]) m.durable(line);
  assert.equal(m.lastAt, t + 1000);
  assert.equal(m.markAt, null);
});

test('times that go backwards are not a pause and do not move the clock', () => {
  const m = new ChatModel();
  const t = day(2026, 11, 10, 10, 0);
  for (const line of [stamped(echo('one'), t), stamped(said('reply'), t + 30 * 60000), stamped(echo('queued earlier'), t + 10 * 60000)]) m.durable(line);
  assert.equal(m.lastAt, t + 30 * 60000);
  // 45 minutes after the latest time seen, though 65 after the one that came out of order: not a pause.
  m.durable(stamped(said('again'), t + 75 * 60000));
  assert.deepEqual(marks(m), [t]);
  assert.equal(m.lastAt, t + 75 * 60000);
});

test('a pause that ends in a tool\'s result is said ahead of the next thing shown', () => {
  const m = new ChatModel();
  const t = day(2026, 11, 10, 10, 0), back = t + 150 * 60000;
  for (const line of [stamped(echo('build it'), t), stamped(used('t1', 'Bash', { command: 'make' }), t + 60000), stamped(returned('t1', 'built'), back)]) m.durable(line);
  // Nothing new is shown yet, so the time waits: a mark never hangs at the end of the conversation.
  assert.deepEqual(kinds(m), ['time', 'user', 'tools']);
  assert.equal(m.markAt, back);
  m.durable(stamped(said('Built.'), back + 30000));
  assert.deepEqual(kinds(m), ['time', 'user', 'tools', 'time', 'assistant']);
  assert.deepEqual(marks(m), [t, back]);
  assert.equal(m.markAt, null);
});

test('a message sent from here is timed by this viewer\'s clock until the session\'s own copy says when', () => {
  let clock = day(2026, 11, 10, 10, 0);
  const m = new ChatModel(() => clock);
  m.sent('hello');
  assert.deepEqual(kinds(m), ['time', 'user']);
  assert.equal(m.items[1].at, clock);
  m.durable(stamped(echo('hello'), clock + 2000));
  assert.deepEqual(kinds(m), ['time', 'user']);
  assert.equal(m.items[1].at, clock + 2000);
  assert.equal(m.items[1].pending, false);
  // Sent again within the hour: no second mark.
  clock += 20 * 60000;
  m.sent('more');
  assert.deepEqual(kinds(m), ['time', 'user', 'user']);
  // A command is never echoed, so the time it was sent at is the one it keeps.
  assert.equal(m.items[2].at, clock);
  // And a copy that comes back saying no time (an agent that keeps none) does not take it away.
  m.durable(echo('more'));
  assert.equal(m.items[2].pending, false);
  assert.equal(m.items[2].at, clock);
});

test('a reply that streams in after a long quiet has its time above it, once', () => {
  let clock = day(2026, 11, 10, 10, 0);
  const m = new ChatModel(() => clock);
  for (const line of [stamped(echo('wake me later'), clock), stamped(said('Will do.'), clock + 5000), done()]) m.durable(line);
  clock += 3 * PAUSE_MS;
  m.live(blockStart(0, 'text'));
  m.live(textDelta(0, 'It is later.'));
  assert.deepEqual(kinds(m), ['time', 'user', 'assistant', 'time', 'assistant']);
  assert.equal(m.items[3].at, clock);
  m.durable(stamped(said('It is later.'), clock + 4000));
  assert.deepEqual(kinds(m), ['time', 'user', 'assistant', 'time', 'assistant']);
  assert.equal(m.items[4].draft, false);
});

test('earlier conversation put ahead takes the top time away when there was no pause there', () => {
  const m = new ChatModel();
  const t = day(2026, 11, 10, 12, 0);
  for (const line of [stamped(echo('later question'), t), stamped(said('later answer'), t + 60000)]) m.durable(line);
  assert.deepEqual(kinds(m), ['time', 'user', 'assistant']);
  const top = m.items[0];
  m.dirty.clear();
  m.prepend([stamped(echo('earlier question'), t - 30 * 60000), stamped(said('earlier answer'), t - 20 * 60000)], null);
  assert.deepEqual(kinds(m), ['time', 'user', 'assistant', 'user', 'assistant']);
  assert.deepEqual(marks(m), [t - 30 * 60000]);
  // The page is told to take the old mark down.
  assert.equal(top.removed, true);
  assert.ok(m.dirty.has(top));
  assert.equal(m.lastAt, t + 60000);
});

test('earlier conversation put ahead keeps the top time when there was a pause there', () => {
  const m = new ChatModel();
  const t = day(2026, 11, 10, 12, 0);
  for (const line of [stamped(echo('later question'), t), stamped(said('later answer'), t + 60000)]) m.durable(line);
  m.prepend([stamped(echo('earlier question'), t - 3 * PAUSE_MS), stamped(said('earlier answer'), t - 3 * PAUSE_MS + 60000)], null);
  assert.deepEqual(kinds(m), ['time', 'user', 'assistant', 'time', 'user', 'assistant']);
  assert.deepEqual(marks(m), [t - 3 * PAUSE_MS, t]);
});

test('a run of calls cut by where a page ends is one run again, with no time between its halves', () => {
  const m = new ChatModel();
  const t = day(2026, 11, 10, 10, 0);
  for (const line of [stamped(used('t2', 'Read', { file_path: 'b' }), t + 120000), stamped(returned('t2', 'b'), t + 121000)]) m.durable(line);
  assert.deepEqual(kinds(m), ['time', 'tools']);
  m.prepend([stamped(echo('read both'), t), stamped(used('t1', 'Read', { file_path: 'a' }), t + 60000), stamped(returned('t1', 'a'), t + 61000)], null);
  assert.deepEqual(kinds(m), ['time', 'user', 'tools']);
  assert.deepEqual(calls(m).map(c => c.id), ['t1', 't2']);
});

test('a pause the earlier page ended on is said where the shown part begins', () => {
  const t = day(2026, 11, 10, 9, 0), back = t + 2 * PAUSE_MS;
  const older = [stamped(echo('build it'), t), stamped(used('t1', 'Bash', { command: 'make' }), t + 60000), stamped(returned('t1', 'built'), back)];
  // What is shown began with its own time: it stays, though the line just before it is seconds older.
  const withTime = new ChatModel();
  withTime.durable(stamped(said('Built.'), back + 30000));
  withTime.prepend(older, null);
  assert.deepEqual(kinds(withTime), ['time', 'user', 'tools', 'time', 'assistant']);
  // What is shown had no time of its own: the pause is said from the page's.
  const without = new ChatModel();
  without.durable(said('Built.'));
  without.prepend(older, null);
  assert.deepEqual(kinds(without), ['time', 'user', 'tools', 'time', 'assistant']);
  assert.deepEqual(marks(without), [t, back]);
});

test('starting over forgets the clock', () => {
  const m = new ChatModel();
  const t = day(2026, 11, 10, 10, 0);
  m.durable(stamped(echo('one'), t));
  m.durable(stamped(returned('nothing', 'x'), t + 2 * PAUSE_MS));
  assert.equal(m.markAt, t + 2 * PAUSE_MS);
  m.reset();
  assert.equal(m.lastAt, null);
  assert.equal(m.markAt, null);
  m.durable(stamped(echo('another conversation'), t - 5 * PAUSE_MS));
  assert.deepEqual(marks(m), [t - 5 * PAUSE_MS]);
});

test('a pause is the start, an hour, or a change of day', () => {
  const t = day(2026, 11, 10, 10, 0);
  assert.equal(pauseBefore(null, t), true);
  assert.equal(pauseBefore(t, t + PAUSE_MS - 1), false);
  assert.equal(pauseBefore(t, t + PAUSE_MS), true);
  assert.equal(pauseBefore(day(2026, 11, 10, 23, 59), day(2026, 11, 11, 0, 1)), true);
  assert.equal(PAUSE_MS, 3600000);
});

test('a time is worded from now: today, yesterday, a weekday, a date, a year', () => {
  const now = day(2026, 11, 17, 15, 0);
  const morning = day(2026, 11, 17, 9, 5);
  assert.equal(timeLabel(morning, now), 'Today ' + clockOf(morning));
  // Yesterday by the calendar, though less than a day ago.
  const lastNight = day(2026, 11, 16, 23, 50);
  assert.equal(timeLabel(lastNight, now), 'Yesterday ' + clockOf(lastNight));
  assert.equal(timeLabel(day(2026, 11, 16, 23, 59), day(2026, 11, 17, 0, 1)), 'Yesterday ' + clockOf(day(2026, 11, 16, 23, 59)));
  const friday = day(2026, 11, 13, 9, 5);
  assert.equal(timeLabel(friday, now), new Date(friday).toLocaleDateString(undefined, { weekday: 'long' }) + ' ' + clockOf(friday));
  const sixDays = day(2026, 11, 11, 9, 5), sevenDays = day(2026, 11, 10, 9, 5);
  assert.ok(timeLabel(sixDays, now).startsWith(new Date(sixDays).toLocaleDateString(undefined, { weekday: 'long' }) + ' '));
  const dated = { weekday: 'long', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' };
  // The weekday is spelled out in a date as it is on its own, so a column of times reads as one form.
  assert.ok(timeLabel(sevenDays, now).startsWith(new Date(sevenDays).toLocaleDateString(undefined, { weekday: 'long' }) + ','));
  assert.equal(timeLabel(sevenDays, now), new Date(sevenDays).toLocaleString(undefined, dated));
  assert.ok(!timeLabel(sevenDays, now).includes('2026'));
  const lastYear = day(2025, 12, 30, 9, 5);
  assert.equal(timeLabel(lastYear, now), new Date(lastYear).toLocaleString(undefined, Object.assign({ year: 'numeric' }, dated)));
  assert.ok(timeLabel(lastYear, now).includes('2025'));
  // A clock a little ahead of this one is still today.
  assert.equal(timeLabel(now + 90000, now), 'Today ' + clockOf(now + 90000));
});

test('the same time is worded again as the days go by', () => {
  const said = day(2026, 11, 14, 16, 30);
  assert.equal(timeLabel(said, day(2026, 11, 14, 18, 0)), 'Today ' + clockOf(said));
  assert.equal(timeLabel(said, day(2026, 11, 15, 8, 0)), 'Yesterday ' + clockOf(said));
  assert.equal(timeLabel(said, day(2026, 11, 17, 8, 0)), new Date(said).toLocaleDateString(undefined, { weekday: 'long' }) + ' ' + clockOf(said));
});

test('after other words a time leaves today unsaid and yesterday in lower case', () => {
  const now = day(2026, 11, 17, 15, 0);
  assert.equal(timeLabel(day(2026, 11, 17, 9, 5), now, true), clockOf(day(2026, 11, 17, 9, 5)));
  assert.equal(timeLabel(day(2026, 11, 16, 9, 5), now, true), 'yesterday ' + clockOf(day(2026, 11, 16, 9, 5)));
  assert.equal(timeLabel(day(2026, 11, 13, 9, 5), now, true), timeLabel(day(2026, 11, 13, 9, 5), now));
});

test('how long since is said in whole words', () => {
  const now = day(2026, 11, 17, 15, 0), minute = 60000, hour = 60 * minute, dayMs = 24 * hour;
  assert.equal(since(now, now), '1 minute ago');
  assert.equal(since(now - 59 * minute, now), '59 minutes ago');
  assert.equal(since(now - 60 * minute, now), '1 hour ago');
  assert.equal(since(now - 119 * minute, now), '1 hour ago');
  assert.equal(since(now - 23 * hour, now), '23 hours ago');
  assert.equal(since(now - dayMs, now), '1 day ago');
  assert.equal(since(now - 59 * dayMs, now), '59 days ago');
  assert.equal(since(now - 60 * dayMs, now), '2 months ago');
  assert.equal(since(now - 729 * dayMs, now), '24 months ago');
  assert.equal(since(now - 730 * dayMs, now), '2 years ago');
  assert.equal(since(now + 5 * minute, now), '1 minute ago');
});

// ---- the stylesheet ----

test('table headers and footers say their own break-inside, so the browser\'s built-in media condition is never used', () => {
  // Chromium 157's built-in rule for thead and tfoot is `break-inside: if(media(overflow-block: paged): avoid;)`. Left
  // in force it stalled the deck's page for minutes (2026-11-17); see AGENTS.md. The rule must cover both elements
  // wherever a chat draws a table, and must come from the page's own stylesheet.
  const css = require('node:fs').readFileSync(require('node:path').join(__dirname, '../../wwwroot/chat.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  const rule = /(^|\})\s*([^{}]*)\{([^{}]*break-inside\s*:\s*auto\s*;?[^{}]*)\}/gm;
  const selectors = [];
  for (const m of css.matchAll(rule)) selectors.push(...m[2].split(',').map(s => s.replace(/\/\*[\s\S]*?\*\//g, '').trim()));
  assert.ok(selectors.includes('.chat thead'), 'no rule for .chat thead: ' + JSON.stringify(selectors));
  assert.ok(selectors.includes('.chat tfoot'), 'no rule for .chat tfoot: ' + JSON.stringify(selectors));
  assert.ok(!/if\s*\(\s*media/.test(css), 'the stylesheet itself must not use if(media())');
});

// ---- the agent's skills ----

test('/skills is offered only where the host can have the agent read its skills again', () => {
  const { offeredCommands, matchCommands } = require('../../wwwroot/chat.js');
  const agent = [{ name: 'goal', description: 'Set a goal' }, { name: 'council', description: 'Ask the council' }];
  assert.deepEqual(offeredCommands(agent, 6, false).map(c => c.name), ['resume', 'rename', 'goal', 'council']);
  assert.deepEqual(offeredCommands(agent, 6).map(c => c.name), ['resume', 'rename', 'goal', 'council']);
  const offered = offeredCommands(agent, 6, true);
  assert.deepEqual(offered.map(c => c.name), ['resume', 'rename', 'skills', 'goal', 'council']);
  assert.equal(offered[2].hint, '[reload]');
  // What follows its name is optional: the menu runs it on Enter instead of waiting for more.
  assert.equal(offered[2].bare, true);
  assert.ok(offered.filter(c => c.name !== 'skills').every(c => !c.bare));
  assert.deepEqual(matchCommands(offered, 'sk').map(c => c.name), ['skills']);
  // An agent's own command of that name gives way to the one that works here, as with /resume.
  const both = offeredCommands(agent.concat([{ name: 'skills', description: 'the agent\'s' }]), 6, true);
  assert.deepEqual(both.filter(c => c.name === 'skills').map(c => c.description), [offered[2].description]);
  // A host too old to take requests offers none of the pane's own commands.
  assert.deepEqual(offeredCommands(agent, 3, true).map(c => c.name), ['goal', 'council']);
});

test('the catalog says whether the agent\'s skills can be reloaded, and says no unless it says yes', () => {
  const m = new ChatModel();
  assert.equal(m.catalog.reload, false);
  m.setCatalog({ agent: 'Antigravity', commands: [], models: [], reload: true });
  assert.equal(m.catalog.reload, true);
  for (const reload of [undefined, false, 'true', 1, null]) {
    m.setCatalog({ agent: 'OpenCode', commands: [], models: [], reload });
    assert.equal(m.catalog.reload, false);
  }
});

test('/skills alone lists, /skills reload reloads, and anything else after it is shown how to ask', () => {
  const { skillsAsked } = require('../../wwwroot/chat.js');
  assert.equal(skillsAsked('/skills'), 'list');
  assert.equal(skillsAsked('  /skills  '), 'list');
  assert.equal(skillsAsked('/skills list'), 'list');
  assert.equal(skillsAsked('/skills reload'), 'reload');
  assert.equal(skillsAsked('/skills   RELOAD '), 'reload');
  assert.equal(skillsAsked('/skills reload now'), 'usage');
  assert.equal(skillsAsked('/skills enable council'), 'usage');
  // Not this command: it goes to the agent as typed.
  for (const other of ['/skillset', '/skill', 'run /skills', '/skills-audit reload', 'skills reload', '']) assert.equal(skillsAsked(other), null);
});

test('the list of skills is one line naming each, and says so when there are none', () => {
  const { skillsNote } = require('../../wwwroot/chat.js');
  assert.equal(skillsNote('Antigravity', [{ name: 'goal' }, { name: 'council' }]),
    'Antigravity offers 2 commands and skills: /goal, /council. /skills reload reads them again from disk.');
  assert.equal(skillsNote('Antigravity', []), 'Antigravity has said no commands or skills. /skills reload reads them from disk.');
});

test('a reload says what was added, removed and updated, or that the list is the same', () => {
  const { reloadNote } = require('../../wwwroot/chat.js');
  assert.equal(reloadNote({ ok: true, count: 15, added: [], removed: [], changed: [] }), 'Skills reloaded: the same 15 commands and skills.');
  assert.equal(reloadNote({ ok: true, count: 16, added: ['rigprobe-two'], removed: [], changed: [] }), 'Skills reloaded. Added: /rigprobe-two.');
  assert.equal(reloadNote({ ok: true, count: 15, added: ['a', 'b'], removed: ['c'], changed: ['d'] }), 'Skills reloaded. Added: /a, /b. Removed: /c. Updated: /d.');
  // A host that says less than this is read as having said nothing changed.
  assert.equal(reloadNote({ ok: true }), 'Skills reloaded: the same 0 commands and skills.');
  assert.equal(reloadNote({ ok: true, count: 3, added: 'x', removed: null }), 'Skills reloaded: the same 3 commands and skills.');
});

test('when the session ends, a subagent in the background and its calls are over, and a finished call is as it was', () => {
  const m = new ChatModel();
  m.durable(used('a1', 'Agent', { description: 'Long job', prompt: 'p' }));
  m.durable(task('task_started', 'a1', { description: 'Long job', is_backgrounded: true }));
  m.durable(returned('a1', 'Started in the background.'));
  m.durable(under('a1', used('c1', 'Bash', { command: 'sleep 60' })));
  m.durable(used('d1', 'Read', { file_path: 'x' }));
  m.durable(returned('d1', 'ok'));
  m.durable(done());
  const agent = calls(m).find(c => c.id === 'a1'), read = calls(m).find(c => c.id === 'd1');
  assert.equal(taskRunning(agent), true);
  m.exited(0);
  assert.equal(taskRunning(agent), false);
  assert.equal(runOverview([agent]).running, false);
  assert.equal(agent.children[0].stopped, true);
  assert.ok(!read.stopped);
});

// ---- a timer ----

test('a timer left running when the turn ends is still running, and done when it reports', () => {
  const m = new ChatModel();
  for (const line of [echo('remind me'), used('s1', 'Schedule', { DurationSeconds: 60 }), used('t1', 'Bash', { command: 'sleep 99' }), said('Scheduled.'), done()]) m.durable(line);
  const timer = calls(m).find(c => c.id === 's1'), shell = calls(m).find(c => c.id === 't1');
  // Any other call with no result at the end of the turn was cut off; the timer was not.
  assert.equal(shell.stopped, true);
  assert.ok(!timer.stopped);
  assert.equal(timer.result, null);
  m.durable(returned('s1', 'fired'));
  assert.deepEqual(timer.result, { text: 'fired', error: false });
  // The session ends with another still to fire: nothing is running any more, the timer and a call cut off mid-turn alike.
  for (const line of [echo('again'), used('s2', 'Schedule', { DurationSeconds: 600 }), done(), echo('go on'), used('t2', 'Bash', { command: 'sleep 99' })]) m.durable(line);
  const [later, cut] = ['s2', 't2'].map(id => calls(m).find(c => c.id === id));
  assert.deepEqual([!!later.stopped, !!cut.stopped], [false, false]);
  m.exited(0);
  assert.deepEqual([later.stopped, cut.stopped], [true, true]);
  assert.deepEqual(timer.result, { text: 'fired', error: false });
  // Read back from an earlier process, it is not waiting on anything.
  const old = new ChatModel();
  old.durable(Object.assign(used('s9', 'Schedule', {}), { history: true }));
  assert.equal(calls(old)[0].stopped, true);
});
