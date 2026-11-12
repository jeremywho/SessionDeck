// node --test Tests/js
// The lines here are shaped like the ones Claude Code 2.1.322 wrote in stream-json mode, cut down to the fields the pane reads.
const test = require('node:test');
const assert = require('node:assert/strict');
const { ChatModel, LineReader, Wire, toolBrief, commandQuery, matchCommands, modelInForce, shortModel, lineDiff, runOverview, alwaysOffer, planExits, planText } = require('../../wwwroot/chat.js');

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
  assert.deepEqual(runOverview([tool('Read', done), tool('Read', done), tool('Grep', done)]),
    { words: '3 tool calls', breakdown: 'Read 2, Grep', failed: 0, asking: false, running: false, state: 'done' });
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
