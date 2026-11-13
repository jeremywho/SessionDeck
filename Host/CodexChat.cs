using System.IO;
using System.Text;
using System.Text.Json;
using Effect = SessionDeck.Host.ChatEffect;
using Kind = SessionDeck.Host.ChatEffectKind;
using Seed = SessionDeck.Host.ChatSeed;

namespace SessionDeck.Host;

/// <summary>
/// A Codex chat: <c>codex app-server</c> on plain pipes, spoken to in JSON-RPC (one object per line).
/// Codex has no stream-json mode, so this turns what the app server says into the lines a chat
/// viewer already reads from Claude (a user line, an assistant line, a tool call and its result, a
/// permission request, a result at the end of a turn), and what a viewer sends into the app
/// server's requests. The pane is then one pane for both. Measured against codex-cli 0.167.2.
/// <para>Unlike Claude on pipes, one app server can start, resume and list conversations
/// ("threads") itself, so moving to another conversation and reading earlier turns are requests
/// here and never a restart.</para>
/// </summary>
internal sealed class CodexChat : IChatAgent
{
    /// <summary>The two ways a Codex chat runs: asking before each command and file change, or not asking. Named as the pane names Claude's modes, so a restart and the picker need no second vocabulary.</summary>
    public const string Ask = "default", Full = "bypassPermissions";

    const string RequestPrefix = "cx-";
    const int SeedTurns = 30, PageTurns = 30;

    sealed record Pending(string What, string Request = "", string Tag = "", string Uuid = "", string Session = "");

    readonly string _cwd;
    readonly int _budget;
    readonly Dictionary<long, Pending> _pending = new();
    /// <summary>Requests for approval the app server is waiting on, by the id a viewer answers with: the JSON-RPC id, and whether it is for a file change.</summary>
    readonly Dictionary<string, (JsonElement Id, bool File)> _open = new(StringComparer.Ordinal);
    readonly HashSet<string> _toolsShown = new(StringComparer.Ordinal);
    /// <summary>For the first message of a page already given out: the cursor of the page before it (null when there is none).</summary>
    readonly Dictionary<string, string?> _pageBefore = new(StringComparer.Ordinal);
    readonly List<JsonElement> _waiting = new();
    long _next = 1;
    string _thread = "", _resume, _turn = "", _model, _effort, _mode, _catalog = "";
    bool _turnStarting, _titled;

    /// <param name="session">The thread to resume, or empty to start one.</param>
    /// <param name="budget">How many bytes of earlier conversation the scrollback is started with.</param>
    public CodexChat(string cwd, string session, string model, string effort, string mode, int budget = 2 * 1024 * 1024)
    {
        _cwd = cwd;
        _resume = session;
        _model = model;
        _effort = effort;
        _mode = mode == Ask ? Ask : Full;
        _budget = budget;
    }

    public string Thread => _thread;

    public string CatalogFrame() => _catalog;

    public string SettingsFrame() => JsonSerializer.Serialize(new { type = "settings", mode = _mode, model = _model, effort = _effort, resume = "" });

    // ---- to the app server ----

    Effect Call(string method, object @params, Pending pending)
    {
        long id = _next++;
        _pending[id] = pending;
        return new Effect(Kind.ToCli, JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params }));
    }

    static Effect Notify(string method) => new(Kind.ToCli, JsonSerializer.SerializeToUtf8Bytes(new { method, @params = new { } }));

    static Effect Durable(object line) => new(Kind.Durable, JsonSerializer.SerializeToUtf8Bytes(line));
    static Effect Live(object line) => new(Kind.Live, JsonSerializer.SerializeToUtf8Bytes(line));
    static Effect Frame(string json) => new(Kind.Frame, Encoding.UTF8.GetBytes(json));

    string ApprovalPolicy => _mode == Ask ? "untrusted" : "never";

    /// <summary>The first thing said to the app server. The rest follows its answer.</summary>
    public List<Effect> Start() => new()
    {
        Call("initialize", new { clientInfo = new { name = "session-deck", title = "Session Deck", version = "1" }, capabilities = (object?)null }, new Pending("initialize")),
    };

    Effect OpenThread()
    {
        // The sandbox is off in both modes: on Windows the restricted-token sandbox refuses to run commands at all
        // ("cannot enforce split writable root sets"), so asking first is the only protection there is to offer.
        if (_resume.Length > 0)
            return Call("thread/resume", Clean(new Dictionary<string, object?> { ["threadId"] = _resume, ["excludeTurns"] = true, ["approvalPolicy"] = ApprovalPolicy, ["sandbox"] = "danger-full-access", ["model"] = Blank(_model) }), new Pending("thread", Session: _resume));
        return Call("thread/start", Clean(new Dictionary<string, object?> { ["cwd"] = _cwd, ["approvalPolicy"] = ApprovalPolicy, ["sandbox"] = "danger-full-access", ["model"] = Blank(_model) }), new Pending("thread"));
    }

    static string? Blank(string s) => s.Length > 0 ? s : null;

    static Dictionary<string, object?> Clean(Dictionary<string, object?> d)
    {
        foreach (var key in d.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) d.Remove(key);
        return d;
    }

    Effect StartTurn(JsonElement input)
    {
        _turnStarting = true;
        return Call("turn/start", Clean(new Dictionary<string, object?>
        {
            ["threadId"] = _thread, ["input"] = input, ["approvalPolicy"] = ApprovalPolicy, ["sandboxPolicy"] = new { type = "dangerFullAccess" },
            ["model"] = Blank(_model), ["effort"] = Blank(_effort),
        }), new Pending("turn"));
    }

    Effect Steer(JsonElement input) => Call("turn/steer", new { threadId = _thread, expectedTurnId = _turn, input }, new Pending("steer"));

    // ---- from a viewer ----

    /// <summary>One line a viewer typed or clicked: a message, an answer to a request for approval, or a change of setting.</summary>
    public List<Effect> FromViewer(JsonElement line, HostRecord record, DateTime now)
    {
        var effects = new List<Effect>();
        switch (Text(line, "type"))
        {
            case "user":
                if (!Get(line, "message", out var message) || !Get(message, "content", out var content)) break;
                var input = Input(content);
                if (input.GetArrayLength() == 0) break;
                record.LastEvent = "UserPromptSubmit";
                Status(record, "busy", now);
                // Before the thread is open, or between asking for a turn and learning its id, there is nothing to address the message to yet.
                if (_thread.Length == 0 || _turnStarting) _waiting.Add(input);
                else effects.Add(_turn.Length > 0 ? Steer(input) : StartTurn(input));
                break;
            case "control_response":
                if (!Get(line, "response", out var response)) break;
                string key = Text(response, "request_id");
                if (!_open.Remove(key, out var asked)) break;
                bool allow = false, always = false;
                if (Get(response, "response", out var answer) && answer.ValueKind == JsonValueKind.Object)
                {
                    allow = Text(answer, "behavior") == "allow";
                    always = Get(answer, "updatedPermissions", out var kept) && kept.ValueKind == JsonValueKind.Array && kept.GetArrayLength() > 0;
                }
                effects.Add(new Effect(Kind.ToCli, JsonSerializer.SerializeToUtf8Bytes(new { id = asked.Id, result = new { decision = allow ? always ? "acceptForSession" : "accept" : "decline" } })));
                effects.Add(new Effect(Kind.Durable, Encoding.UTF8.GetBytes(line.GetRawText())));
                if (_open.Count == 0) Status(record, "busy", now);
                break;
            case "control_request":
                string id = Text(line, "request_id");
                if (!Get(line, "request", out var request)) break;
                string subtype = Text(request, "subtype");
                bool done = true;
                if (subtype == "interrupt") { if (_turn.Length > 0) effects.Add(Call("turn/interrupt", new { threadId = _thread, turnId = _turn }, new Pending("interrupt"))); }
                else if (subtype == "set_model" && Text(request, "model") is { Length: > 0 } model) record.Model = _model = model;
                else if (subtype == "set_permission_mode" && Text(request, "mode") is Ask or Full) record.PermissionMode = _mode = Text(request, "mode");
                else if (subtype == "apply_flag_settings" && Get(request, "settings", out var settings) && Text(settings, "effortLevel") is { Length: > 0 } effort) record.Effort = _effort = effort;
                else done = false;
                // The model, the effort and the mode are sent with each turn, so a change is in force from the next one.
                effects.Add(Live(done
                    ? new { type = "control_response", response = (object)new { subtype = "success", request_id = id, response = new { mode = _mode } } }
                    : new { type = "control_response", response = (object)new { subtype = "error", request_id = id, error = $"Codex has no {subtype} setting" } }));
                if (done && subtype != "interrupt") effects.Add(Frame(SettingsFrame()));
                break;
        }
        return effects;
    }

    /// <summary>A viewer's message as the app server takes it: text, and images as data URLs.</summary>
    static JsonElement Input(JsonElement content)
    {
        var parts = new List<object>();
        if (content.ValueKind == JsonValueKind.String) { if (content.GetString() is { Length: > 0 } text) parts.Add(new { type = "text", text, text_elements = Array.Empty<object>() }); }
        else if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                if (Text(block, "type") == "text" && Text(block, "text") is { Length: > 0 } said) parts.Add(new { type = "text", text = said, text_elements = Array.Empty<object>() });
                else if (Text(block, "type") == "image" && Get(block, "source", out var source) && Text(source, "data") is { Length: > 0 } data)
                    parts.Add(new { type = "image", url = $"data:{Text(source, "media_type")};base64,{data}" });
            }
        return JsonSerializer.SerializeToElement(parts);
    }

    /// <summary>The conversations in this folder, for the viewer that asked.</summary>
    public List<Effect> Sessions(string request, string tag) => new()
    {
        Call("thread/list", new { cwd = _cwd, limit = 100, sortKey = "updated_at", useStateDbOnly = true }, new Pending("sessions", request, tag)),
    };

    /// <summary>Move to another conversation. Refused, to the asking viewer, when it is not one or a turn is running.</summary>
    public List<Effect> Resume(string request, string session, string tag)
    {
        string refusal = !ChatSessions.IsSessionId(session) ? "That conversation is not in this folder."
            : string.Equals(session, _thread, StringComparison.OrdinalIgnoreCase) ? "That conversation is the one open here."
            : _turn.Length > 0 || _turnStarting ? "Stop the current turn first."
            : _pending.Values.Any(p => p.What is "switch" or "seed") ? "The session is already changing." : "";
        if (refusal.Length > 0) return new() { new Effect(Kind.Reply, ChatSessions.RefusedFrame(request, refusal), tag) };
        return new()
        {
            Call("thread/resume", new { threadId = session, excludeTurns = true, approvalPolicy = ApprovalPolicy, sandbox = "danger-full-access" }, new Pending("switch", request, tag, Session: session)),
        };
    }

    /// <summary>
    /// The conversation before the message a viewer names as its oldest. The app server pages turns
    /// newest first behind an opaque cursor; the cursor that follows a page already given out is
    /// kept by that page's first message, and a message not known that way is looked for from the
    /// newest turn back.
    /// </summary>
    public List<Effect> History(string request, string uuid, string tag)
    {
        if (_thread.Length == 0 || uuid.Length == 0) return new() { new Effect(Kind.Reply, HistoryFrame(new List<byte[]>(), false, request), tag) };
        if (_pageBefore.TryGetValue(uuid, out var cursor))
        {
            if (cursor == null) return new() { new Effect(Kind.Reply, HistoryFrame(new List<byte[]>(), false, request), tag) };
            return new() { Turns(cursor, new Pending("history", request, tag)) };
        }
        return new() { Turns(null, new Pending("walk", request, tag, uuid)) };
    }

    Effect Turns(string? cursor, Pending pending) =>
        Call("thread/turns/list", Clean(new Dictionary<string, object?> { ["threadId"] = _thread, ["limit"] = pending.What == "seed" ? SeedTurns : PageTurns, ["sortDirection"] = "desc", ["itemsView"] = "full", ["cursor"] = cursor }), pending);

    static byte[] HistoryFrame(List<byte[]> lines, bool more, string request) => ChatStream.HistoryFrame(new ChatTranscript.Page(lines, more ? 1 : -1), request);

    // ---- from the app server ----

    /// <summary>One line the app server said: an answer to something asked, a request of its own, or a notification.</summary>
    public List<Effect> FromCli(JsonElement msg, HostRecord record, DateTime now)
    {
        var effects = new List<Effect>();
        if (msg.ValueKind != JsonValueKind.Object) return effects;
        bool hasId = Get(msg, "id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        string method = Text(msg, "method");
        if (!Get(msg, "params", out var p)) p = default;
        if (method.Length == 0)
        {
            if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long answered) && _pending.Remove(answered, out var pending)) Answered(pending, msg, record, now, effects);
            return effects;
        }
        if (hasId) { Asked(id, method, p, record, now, effects); return effects; }
        // Another thread's news is not this conversation's (the one moved away from can still be finishing something).
        if (Text(p, "threadId") is { Length: > 0 } about && !string.Equals(about, _thread, StringComparison.OrdinalIgnoreCase)) return effects;
        switch (method)
        {
            case "turn/started":
                if (Get(p, "turn", out var started)) _turn = Text(started, "id");
                _turnStarting = false;
                effects.Add(Live(new { type = "system", subtype = "init", session_id = _thread, model = _model, permissionMode = _mode }));
                Status(record, "busy", now);
                Flush(effects);
                break;
            case "item/started":
                if (Get(p, "item", out var began)) ItemStarted(began, record, effects);
                break;
            case "item/agentMessage/delta":
                effects.Add(Live(new { type = "stream_event", @event = new { type = "content_block_delta", delta = new { type = "text_delta", text = Text(p, "delta") } }, parent_tool_use_id = (string?)null }));
                break;
            case "item/reasoning/summaryTextDelta":
            case "item/reasoning/textDelta":
                effects.Add(Live(new { type = "stream_event", @event = new { type = "content_block_delta", delta = new { type = "thinking_delta", thinking = Text(p, "delta") } }, parent_tool_use_id = (string?)null }));
                break;
            case "item/completed":
                if (Get(p, "item", out var ended))
                {
                    foreach (var line in Lines(ended, history: false, _toolsShown.Remove(Text(ended, "id")))) effects.Add(new Effect(Kind.Durable, line));
                    // A conversation is called by its name, and until it has one by what was first said in it.
                    if (!_titled && Text(ended, "type") == "userMessage" && FirstText(ended) is { Length: > 0 } first) { _titled = true; Title(Short(first), record, effects); }
                }
                break;
            case "turn/completed":
                string status = Get(p, "turn", out var turn) ? Text(turn, "status") : "";
                string why = turn.ValueKind == JsonValueKind.Object && Get(turn, "error", out var error) ? Text(error, "message") : "";
                _turn = "";
                _turnStarting = false;
                foreach (string key in _open.Keys.ToList()) effects.Add(Durable(new { type = "control_cancel_request", request_id = key }));
                _open.Clear();
                if (status == "interrupted") effects.Add(Durable(new { type = "user", message = new { role = "user", content = "[Request interrupted by user]" }, parent_tool_use_id = (string?)null }));
                effects.Add(Durable(new { type = "result", subtype = status == "failed" ? "error" : status == "interrupted" ? "error_during_execution" : "success", is_error = status == "failed", result = why }));
                record.LastEvent = "Stop";
                Status(record, "idle", now);
                Flush(effects);
                break;
            case "serverRequest/resolved":
                // Settled without a viewer's answer (the turn was stopped, or another client answered).
                string settled = RequestPrefix + (Get(p, "requestId", out var rid) ? rid.GetRawText().Trim('"') : "");
                if (_open.Remove(settled))
                {
                    effects.Add(Durable(new { type = "control_cancel_request", request_id = settled }));
                    if (_open.Count == 0 && _turn.Length > 0) Status(record, "busy", now);
                }
                break;
            case "thread/name/updated":
                if (Text(p, "threadName") is { Length: > 0 } named) { _titled = true; Title(named, record, effects); }
                break;
            case "error":
                if (Get(p, "error", out var failed)) effects.Add(Durable(new { type = "system", subtype = "api_error", error = new { message = Text(failed, "message") } }));
                break;
        }
        return effects;
    }

    /// <summary>What was first said, as short as a tab's name.</summary>
    static string Short(string text)
    {
        string line = ChatSessions.OneLine(text);
        return line.Length > 60 ? line[..59].TrimEnd() + "…" : line;
    }

    void Title(string title, HostRecord record, List<Effect> effects)
    {
        if (title.Length == 0 || record.Title == title) return;
        record.Title = title;
        effects.Add(Frame(JsonSerializer.Serialize(new { type = "title", title })));
    }

    /// <summary>Messages typed while there was no turn to add them to: the first starts one, and the rest wait for its id.</summary>
    void Flush(List<Effect> effects)
    {
        if (_thread.Length == 0 || _turnStarting || _waiting.Count == 0) return;
        if (_turn.Length > 0) { foreach (var input in _waiting) effects.Add(Steer(input)); _waiting.Clear(); return; }
        effects.Add(StartTurn(_waiting[0]));
        _waiting.RemoveAt(0);
    }

    /// <summary>The app server asks something of its client. An approval goes to the viewers; anything else is declined, because an unanswered request holds the turn for good.</summary>
    void Asked(JsonElement id, string method, JsonElement p, HostRecord record, DateTime now, List<Effect> effects)
    {
        bool file = method == "item/fileChange/requestApproval";
        if (!file && method != "item/commandExecution/requestApproval")
        {
            effects.Add(new Effect(Kind.ToCli, JsonSerializer.SerializeToUtf8Bytes(new { id, error = new { code = -32601, message = $"Session Deck does not handle {method}" } })));
            return;
        }
        string key = RequestPrefix + id.GetRawText().Trim('"');
        string item = Text(p, "itemId"), command = Command(p);
        _open[key] = (id.Clone(), file);
        // What "always" means is Codex's own: the same command again, or any file change, for the rest of the session.
        object remember = file
            ? new[] { (object)new { type = "setMode", mode = "acceptEdits", destination = "session" } }
            : new[] { (object)new { type = "addRules", rules = new[] { new { toolName = "Shell", ruleContent = command } }, behavior = "allow", destination = "session" } };
        effects.Add(Durable(new
        {
            type = "control_request", request_id = key,
            request = new { subtype = "can_use_tool", tool_name = file ? "Edit" : "Shell", tool_use_id = item, description = Text(p, "reason"), input = file ? (object)new { } : new { command }, permission_suggestions = remember },
        }));
        record.LastTool = file ? "Edit" : "Shell";
        record.LastEvent = "PermissionRequest";
        Status(record, "waiting", now);
    }

    void Answered(Pending pending, JsonElement msg, HostRecord record, DateTime now, List<Effect> effects)
    {
        bool ok = Get(msg, "result", out var result) && result.ValueKind == JsonValueKind.Object;
        string why = Get(msg, "error", out var error) ? Text(error, "message") : "";
        switch (pending.What)
        {
            case "initialize":
                effects.Add(Notify("initialized"));
                effects.Add(Call("model/list", new { }, new Pending("models")));
                effects.Add(OpenThread());
                break;
            case "models":
                if (ok) { _catalog = Catalog(result); effects.Add(Frame(_catalog)); }
                break;
            case "thread":
            case "switch":
                if (!ok)
                {
                    if (pending.What == "switch") { effects.Add(new Effect(Kind.Reply, ChatSessions.RefusedFrame(pending.Request, "Could not resume that conversation" + (why.Length > 0 ? ": " + why : ".")), pending.Tag)); break; }
                    // The conversation this session was started on is gone: a new one, and a line that says so.
                    if (_resume.Length == 0) { effects.Add(Durable(new { type = "host", subtype = "stderr", text = "Codex could not start a conversation" + (why.Length > 0 ? ": " + why : ".") })); break; }
                    effects.Add(Durable(new { type = "host", subtype = "stderr", text = "Could not resume that conversation" + (why.Length > 0 ? ": " + why : ".") + " A new one was started." }));
                    _resume = "";
                    effects.Add(OpenThread());
                    break;
                }
                if (!Get(result, "thread", out var thread)) break;
                bool resumed = pending.Session.Length > 0;
                _thread = Text(thread, "id");
                _turn = "";
                _toolsShown.Clear();
                _pageBefore.Clear();
                // A session keeps the model and effort someone picked; otherwise it runs on what the conversation was on.
                if (_model.Length == 0 || pending.What == "switch") _model = Text(result, "model") is { Length: > 0 } m ? m : _model;
                if (_effort.Length == 0 || pending.What == "switch") _effort = Text(result, "reasoningEffort") is { Length: > 0 } e ? e : _effort;
                string name = Text(thread, "name");
                _titled = name.Length > 0;
                string title = _titled ? name : Short(Text(thread, "preview"));
                _titled |= title.Length > 0;
                record.Model = _model;
                record.Effort = _effort;
                record.PermissionMode = _mode;
                effects.Add(Frame(SettingsFrame()));
                if (resumed) effects.Add(Turns(null, new Pending("seed", pending.Request, pending.Tag, Session: title + "\n" + Text(thread, "path"))));
                else
                {
                    record.SessionId = _thread;
                    record.TranscriptPath = Text(thread, "path");
                    Flush(effects);
                }
                break;
            case "seed":
            {
                string[] named = pending.Session.Split('\n', 2);
                string? next = null;
                var lines = ok ? Chronological(result, out next) : new List<byte[]>();
                bool earlier = next != null;
                int size = lines.Sum(l => l.Length + 1), drop = 0;
                while (drop < lines.Count - 1 && size > _budget) size -= lines[drop++].Length + 1;
                if (drop > 0) { lines.RemoveRange(0, drop); earlier = true; }
                else if (FirstUuid(lines) is { Length: > 0 } first) _pageBefore[first] = next;
                record.SessionId = _thread;
                record.TranscriptPath = named.Length > 1 ? named[1] : "";
                record.Title = named[0];
                record.LastTool = "";
                record.LastEvent = "";
                Status(record, "idle", now);
                effects.Add(new Effect(Kind.Reset, Array.Empty<byte>(), Seed: new Seed(_thread, named[0], record.TranscriptPath, lines, earlier)));
                Flush(effects);
                break;
            }
            case "history":
            case "walk":
            {
                if (!ok) { effects.Add(new Effect(Kind.Reply, HistoryFrame(new List<byte[]>(), false, pending.Request), pending.Tag)); break; }
                var lines = Chronological(result, out string? next, pending.What == "walk" ? pending.Uuid : null, out bool found);
                // Not on this page: the message is further back, or (at the last page) nowhere.
                if (pending.What == "walk" && !found)
                {
                    if (next != null) effects.Add(Turns(next, pending));
                    else effects.Add(new Effect(Kind.Reply, HistoryFrame(new List<byte[]>(), false, pending.Request), pending.Tag));
                    break;
                }
                // The message opened its page: what comes before it is the next page, whole.
                if (lines.Count == 0 && next != null) { effects.Add(Turns(next, new Pending("history", pending.Request, pending.Tag))); break; }
                if (FirstUuid(lines) is { Length: > 0 } first) _pageBefore[first] = next;
                effects.Add(new Effect(Kind.Reply, HistoryFrame(lines, next != null, pending.Request), pending.Tag));
                break;
            }
            case "sessions":
            {
                var entries = new List<ChatSessions.Entry>();
                if (ok && Get(result, "data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var t in data.EnumerateArray())
                    {
                        string tid = Text(t, "id");
                        if (tid.Length == 0 || string.Equals(tid, _thread, StringComparison.OrdinalIgnoreCase)) continue;
                        string label = Text(t, "name") is { Length: > 0 } given ? given : ChatSessions.OneLine(Text(t, "preview"));
                        long at = Get(t, "updatedAt", out var u) && u.TryGetInt64(out long seconds) ? seconds * 1000 : 0;
                        if (label.Length > 0) entries.Add(new ChatSessions.Entry(tid, label, at, false));
                    }
                effects.Add(new Effect(Kind.Reply, ChatSessions.ListFrame(pending.Request, _thread, entries), pending.Tag));
                break;
            }
            case "turn":
                if (ok && Get(result, "turn", out var turn)) _turn = Text(turn, "id");
                _turnStarting = false;
                if (!ok)
                {
                    effects.Add(Durable(new { type = "result", subtype = "error", is_error = true, result = why.Length > 0 ? why : "Codex did not start the turn" }));
                    record.LastEvent = "Stop";
                    Status(record, "idle", now);
                }
                Flush(effects);
                break;
            case "steer":
                // The turn ended before the message reached it: it starts the next one.
                if (!ok && Get(msg, "error", out _)) effects.Add(Durable(new { type = "host", subtype = "stderr", text = "That message did not reach the running turn" + (why.Length > 0 ? ": " + why : ".") }));
                break;
        }
    }

    static string Catalog(JsonElement models)
    {
        var list = new List<object>();
        if (Get(models, "data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
            {
                if (Text(m, "id").Length == 0 || (Get(m, "hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)) continue;
                var efforts = new List<string>();
                if (Get(m, "supportedReasoningEfforts", out var levels) && levels.ValueKind == JsonValueKind.Array)
                    foreach (var level in levels.EnumerateArray())
                        if (Text(level, "reasoningEffort") is { Length: > 0 } effort) efforts.Add(effort);
                list.Add(new { value = Text(m, "id"), resolved = Text(m, "model"), name = Text(m, "displayName"), description = Text(m, "description"), efforts });
            }
        return JsonSerializer.Serialize(new
        {
            type = "catalog", agent = "Codex", models = list, commands = Array.Empty<object>(),
            modes = new[] { new[] { Ask, "Ask before acting", "Ask" }, new[] { Full, "Full access", "Full" } },
        });
    }

    // ---- items as lines ----

    void ItemStarted(JsonElement item, HostRecord record, List<Effect> effects)
    {
        string type = Text(item, "type");
        if (type is "agentMessage" or "reasoning")
        {
            effects.Add(Live(new { type = "stream_event", @event = new { type = "content_block_start", content_block = new { type = type == "reasoning" ? "thinking" : "text" } }, parent_tool_use_id = (string?)null }));
            return;
        }
        if (type is "userMessage" or "hookPrompt" or "functionCallOutput" or "plan") return;
        if (ToolCall(item, history: false) is not { } call) return;
        _toolsShown.Add(Text(item, "id"));
        record.LastTool = ToolName(item);
        effects.Add(new Effect(Kind.Durable, call));
    }

    /// <summary>
    /// A finished item as the lines a viewer reads: a message, or a tool call and its result.
    /// <paramref name="callShown"/> says the call was already sent when the item began.
    /// </summary>
    internal static List<byte[]> Lines(JsonElement item, bool history, bool callShown = false)
    {
        var lines = new List<byte[]>();
        string id = Text(item, "id");
        switch (Text(item, "type"))
        {
            case "userMessage":
                lines.Add(Line(new Dictionary<string, object?> { ["type"] = "user", ["uuid"] = id, ["isReplay"] = true, ["message"] = new { role = "user", content = UserContent(item) }, ["parent_tool_use_id"] = null }, history));
                break;
            case "agentMessage":
                if (Text(item, "text").Length > 0 || !history)
                    lines.Add(Assistant(id, new { type = "text", text = Text(item, "text") }, history));
                break;
            case "plan":
                if (Text(item, "text") is { Length: > 0 } plan) lines.Add(Assistant(id, new { type = "text", text = plan }, history));
                break;
            case "reasoning":
                string thought = string.Join("\n\n", Strings(item, "summary").Concat(Strings(item, "content")).Where(s => s.Length > 0));
                if (thought.Length > 0 || !history) lines.Add(Assistant(id, new { type = "thinking", thinking = thought }, history));
                break;
            case "hookPrompt":
            case "functionCallOutput":
                break;
            default:
                if (!callShown && ToolCall(item, history) is { } call) lines.Add(call);
                if (ToolResult(item, history) is { } result) lines.Add(result);
                break;
        }
        return lines;
    }

    static byte[] Assistant(string id, object block, bool history) =>
        Line(new Dictionary<string, object?> { ["type"] = "assistant", ["uuid"] = id, ["message"] = new { role = "assistant", content = new[] { block } }, ["parent_tool_use_id"] = null }, history);

    static byte[] Line(Dictionary<string, object?> line, bool history)
    {
        if (history) line["history"] = true;
        return JsonSerializer.SerializeToUtf8Bytes(line);
    }

    static string ToolName(JsonElement item) => Text(item, "type") switch
    {
        "commandExecution" => "Shell",
        "fileChange" => "Edit",
        "mcpToolCall" => Text(item, "server") + ":" + Text(item, "tool"),
        "dynamicToolCall" => Text(item, "tool"),
        "collabAgentToolCall" or "subAgentActivity" => "Agent",
        "webSearch" => "WebSearch",
        var other => other.Length > 0 ? char.ToUpperInvariant(other[0]) + other[1..] : "Tool",
    };

    static byte[]? ToolCall(JsonElement item, bool history)
    {
        string id = Text(item, "id");
        if (id.Length == 0) return null;
        object input = Text(item, "type") switch
        {
            "commandExecution" => new { command = Command(item) },
            "fileChange" => new { file_path = string.Join(", ", Changes(item).Select(c => c.Path)), diff = string.Join("\n", Changes(item).Select(c => c.Diff)) },
            "mcpToolCall" or "dynamicToolCall" => Get(item, "arguments", out var arguments) ? (object)arguments : new { },
            "collabAgentToolCall" => new { prompt = Text(item, "prompt") },
            "webSearch" => new { query = Text(item, "query") },
            _ => new { },
        };
        return Assistant(id, new { type = "tool_use", id, name = ToolName(item), input }, history);
    }

    static byte[]? ToolResult(JsonElement item, bool history)
    {
        string id = Text(item, "id"), status = Text(item, "status");
        if (id.Length == 0 || status == "inProgress") return null;
        bool failed = status is "failed" or "declined";
        string text = "";
        switch (Text(item, "type"))
        {
            case "commandExecution":
                text = Text(item, "aggregatedOutput");
                if (Get(item, "exitCode", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0) failed = true;
                if (status == "declined" && text.Length == 0) text = "Declined.";
                break;
            case "fileChange":
                if (failed) text = status == "declined" ? "Declined." : "The change was not applied.";
                break;
            case "mcpToolCall":
                if (Get(item, "error", out var error) && Text(error, "message") is { Length: > 0 } message) { text = message; failed = true; }
                else if (Get(item, "result", out var result) && result.ValueKind == JsonValueKind.Object && Get(result, "content", out var content) && content.ValueKind == JsonValueKind.Array)
                    text = string.Join("\n", content.EnumerateArray().Select(c => Text(c, "text") is { Length: > 0 } t ? t : c.GetRawText()));
                break;
        }
        return Line(new Dictionary<string, object?>
        {
            ["type"] = "user", ["uuid"] = id,
            ["message"] = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = id, content = text, is_error = failed } } },
            ["parent_tool_use_id"] = null,
        }, history);
    }

    /// <summary>The command as the person would type it: Codex wraps each one in a shell invocation, and says what is inside.</summary>
    static string Command(JsonElement item)
    {
        if (Get(item, "commandActions", out var actions) && actions.ValueKind == JsonValueKind.Array && actions.GetArrayLength() == 1 && Text(actions[0], "command") is { Length: > 0 } inner) return inner;
        return Text(item, "command");
    }

    /// <summary>Each changed file with its change as diff lines: a new file's text is all additions, a removed one's all removals.</summary>
    static List<(string Path, string Diff)> Changes(JsonElement item)
    {
        var changes = new List<(string, string)>();
        if (!Get(item, "changes", out var list) || list.ValueKind != JsonValueKind.Array) return changes;
        foreach (var change in list.EnumerateArray())
        {
            string diff = Text(change, "diff"), kind = Get(change, "kind", out var k) ? Text(k, "type") : "";
            if (kind is "add" or "delete")
                diff = string.Join("\n", diff.TrimEnd('\n').Split('\n').Select(l => (kind == "add" ? "+" : "-") + l.TrimEnd('\r')));
            changes.Add((Text(change, "path"), diff.TrimEnd('\n')));
        }
        return changes;
    }

    static object UserContent(JsonElement item)
    {
        var blocks = new List<object>();
        string text = "";
        if (Get(item, "content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var part in content.EnumerateArray())
            {
                string type = Text(part, "type");
                if (type == "text") text += (text.Length > 0 ? "\n" : "") + Text(part, "text");
                // The picture itself stays out of the scrollback, as with Claude: megabytes no later viewer reads.
                else if (type is "image" or "localImage") blocks.Add(new { type = "image", source = new { type = "base64", media_type = "", data = "", omitted = Text(part, "url").Length } });
            }
        if (blocks.Count == 0) return text;
        if (text.Length > 0) blocks.Insert(0, new { type = "text", text });
        return blocks;
    }

    static string FirstText(JsonElement item)
    {
        if (Get(item, "content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var part in content.EnumerateArray())
                if (Text(part, "type") == "text" && Text(part, "text") is { Length: > 0 } text) return text;
        return "";
    }

    List<byte[]> Chronological(JsonElement page, out string? next) => Chronological(page, out next, null, out _);

    /// <summary>
    /// A page of turns (newest first, as the app server gives them) as lines oldest first. With
    /// <paramref name="before"/>, only what precedes the item of that id, and whether it was on the page.
    /// </summary>
    static List<byte[]> Chronological(JsonElement page, out string? next, string? before, out bool found)
    {
        next = Text(page, "nextCursor") is { Length: > 0 } cursor ? cursor : null;
        found = before == null;
        var lines = new List<byte[]>();
        if (!Get(page, "data", out var turns) || turns.ValueKind != JsonValueKind.Array) return lines;
        var newestFirst = turns.EnumerateArray().ToList();
        int from = 0;
        JsonElement? partial = null;
        if (before != null)
            for (int i = 0; i < newestFirst.Count && !found; i++)
                if (newestFirst[i].TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.EnumerateArray().Any(item => Text(item, "id") == before))
                {
                    found = true;
                    partial = newestFirst[i];
                    from = i + 1;
                }
        if (!found) return lines;
        for (int i = newestFirst.Count - 1; i >= from; i--) AddTurn(newestFirst[i], null, lines);
        if (partial is { } cut) AddTurn(cut, before, lines);
        return lines;
    }

    static void AddTurn(JsonElement turn, string? stopAt, List<byte[]> lines)
    {
        if (!Get(turn, "items", out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            if (stopAt != null && Text(item, "id") == stopAt) return;
            lines.AddRange(Lines(item, history: true));
        }
        if (stopAt == null && Text(turn, "status") == "interrupted")
            lines.Add(JsonSerializer.SerializeToUtf8Bytes(new { type = "user", history = true, message = new { role = "user", content = "[Request interrupted by user]" }, parent_tool_use_id = (string?)null }));
    }

    /// <summary>The id of the first message in the lines: what a viewer will name as its oldest.</summary>
    static string FirstUuid(List<byte[]> lines)
    {
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            if (Text(doc.RootElement, "type") is "user" or "assistant" && Text(doc.RootElement, "uuid") is { Length: > 0 } uuid) return uuid;
        }
        return "";
    }

    static IEnumerable<string> Strings(JsonElement item, string name) =>
        Get(item, name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString() ?? "")
            : Array.Empty<string>();

    static void Status(HostRecord record, string status, DateTime now)
    {
        if (record.AgentStatus == status) return;
        record.AgentStatus = status;
        record.StatusAt = now;
    }

    /// <summary>A property of an object, and false for anything that is not one: what a viewer or the app server sends is not trusted to have its shape.</summary>
    static bool Get(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value);
    }

    static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && Get(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
