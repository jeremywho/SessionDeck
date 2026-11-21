using System.Globalization;
using System.Text;
using System.Text.Json;
using Effect = SessionDeck.Host.ChatEffect;
using Kind = SessionDeck.Host.ChatEffectKind;

namespace SessionDeck.Host;

/// <summary>
/// A chat with an agent that speaks the Agent Client Protocol: JSON-RPC 2.0 over the agent's stdio,
/// one object per line (<c>agy acp</c>, <c>opencode acp</c>). It turns the agent's session updates
/// into the lines a chat viewer already reads from Claude, and a viewer's lines into ACP requests,
/// so the pane is one pane for every agent. Measured against Antigravity 1.5.8 and OpenCode 2.2.6.
/// <para>What the protocol gives, and what follows from it here: a turn is one
/// <c>session/prompt</c> request answered when the turn ends, with <c>session/update</c>
/// notifications in between; text arrives in chunks with no end mark, so a message is whole when
/// something else follows it; a past conversation is reopened with <c>session/load</c>, which
/// replays all of it as updates before answering, so an earlier page is read from what is kept here; the
/// model, effort and mode are <c>configOptions</c> the agent lists and the client sets; there is
/// no adding to a running turn, so a message typed mid-turn waits for the turn to end.</para>
/// </summary>
internal sealed class AcpChat : IChatAgent
{
    /// <summary>The providers whose chat is this translator, by the name the host record carries.</summary>
    public static bool Speaks(string provider) => provider is "Agy" or "OpenCode";

    /// <summary>What the pane calls the agent ("Message …").</summary>
    public static string NameOf(string provider) => provider == "Agy" ? "Antigravity" : provider;

    const string RequestPrefix = "acp-";

    sealed record Pending(string What, string Request = "", string Tag = "", string Session = "", string ViewerId = "", List<ChatSessions.Entry>? Listed = null, int Page = 0);
    sealed record Choice(string Value, string Name);
    sealed record Option(string Id, string Category, string Current, List<Choice> Choices);

    sealed class Tool
    {
        public string Id = "", Title = "", Kind = "", Name = "", Status = "";
        public JsonElement Input, Content, Output;
        public bool Shown, Done;
        /// <summary>A call that starts a subagent, counted once among those waiting for theirs to show up on disk.</summary>
        public bool Spawned;
        /// <summary>Made by the process running now, not read back from an earlier one.</summary>
        public bool Live;
    }

    readonly string _provider, _cwd;
    readonly int _budget;
    readonly Dictionary<long, Pending> _pending = new();
    readonly Dictionary<string, (JsonElement Id, List<(string Id, string Kind)> Options, string Tool)> _open = new(StringComparer.Ordinal);
    /// <summary>A viewer asked for the turn to stop, and the agent has not yet said it did.</summary>
    bool _stopping;
    /// <summary>The agent would not open a session, and has not been asked again.</summary>
    bool _unopened;
    Dictionary<string, Tool> _tools = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    readonly List<JsonElement> _waiting = new();
    readonly List<byte[]> _replay = new();
    readonly StringBuilder _run = new();
    List<Option> _options = new();
    string _commands = "[]";
    long _next = 1, _made;
    string _session = "", _resume, _catalog = "", _runKind = "", _runId = "";
    string _wantModel, _wantEffort, _wantMode;
    /// <summary>The conversation being reopened: until the agent answers, its updates are the past being replayed and not something happening now.</summary>
    string? _loading;
    bool _prompting, _canList, _canLoad, _titled;
    /// <summary>The agent can be attached to a conversation again without replaying it (<c>session/resume</c>).</summary>
    bool _canResume;
    /// <summary>The open conversation is being attached to again and the agent has not answered: no turn is started meanwhile.</summary>
    bool _resuming;
    /// <summary>A viewer asked for the commands to be read again: who, and what they were. Answered when the agent next says its commands.</summary>
    (string Request, string Tag, Dictionary<string, string> Before)? _reload;

    /// <summary>
    /// Whether attaching to the open conversation again makes the agent read its commands and skills
    /// from disk and say them. Measured, not assumed: Antigravity 1.5.13 does. OpenCode 2.2.6 answers
    /// with the list it already had (it reads a command's file when the command is used, so a new one
    /// works at once and only its list is stale).
    /// </summary>
    internal static bool RereadsCommands(string provider) => provider == "Agy";

    bool CanReload => _canResume && RereadsCommands(_provider);

    /// <summary>The context use said while a conversation was being replayed: the record's once that conversation is the one served.</summary>
    long _replayUsed, _replayWindow;

    /// <summary>When the agent last said or did something.</summary>
    DateTime _heardAt;
    /// <summary>
    /// The agent is working with no turn of ours running. Antigravity goes on after it has ended a turn
    /// (measured, 1.5.13: tool calls and a closing message 20 s to minutes after <c>end_turn</c>), and
    /// nothing in the protocol says when that is over.
    /// </summary>
    bool _unasked;

    /// <summary>
    /// The call an agent leaves running to come back by itself. Antigravity's <c>schedule</c> is its timer
    /// and its wait on a background task both (measured, 1.5.13): the turn ends with the call still in
    /// progress, and when it fires the agent goes on unasked. The timer lives in the agent's process, so
    /// one read back from an earlier process is not waiting on anything.
    /// </summary>
    bool Waking => _tools.Values.Any(t => t.Live && t.Shown && !t.Done && t.Name == "schedule");

    /// <summary>What a session with no turn running is: waiting for the person, or for a timer or a subagent of its own.</summary>
    string Rest(DateTime now) => Waking || Helping(now) ? "scheduled" : "idle";

    /// <summary>A session at rest is what <see cref="Rest"/> says now: a subagent can finish, or go quiet, with the agent saying nothing.</summary>
    void Settle(HostRecord record, DateTime now)
    {
        if (record.AgentStatus is "idle" or "scheduled") Status(record, Rest(now), now);
    }

    /// <summary>How long a pause makes what the agent said unasked a message of the conversation.</summary>
    internal static readonly TimeSpan SaidQuiet = TimeSpan.FromSeconds(2);
    /// <summary>How long with nothing heard and no call running before work nobody asked for is taken to be over.</summary>
    internal static readonly TimeSpan WorkQuiet = TimeSpan.FromSeconds(30);
    /// <summary>A call that never reports back does not keep the session "working" for ever.</summary>
    internal static readonly TimeSpan CallQuiet = TimeSpan.FromMinutes(10);

    Effect Working() => Live(new { type = "system", subtype = "init", session_id = _session, model = Current("model"), permissionMode = Current("mode") });

    /// <summary>The agent said or did something that is happening now. Outside a turn that is work of its own: the session is busy again.</summary>
    void Heard(string kind, JsonElement update, HostRecord record, DateTime now, List<Effect> effects)
    {
        bool said = (kind is "agent_message_chunk" or "agent_thought_chunk") && Get(update, "content", out var content) && Text(content, "text").Length > 0;
        bool began = kind == "tool_call" || (kind == "tool_call_update" && Text(update, "status") is "pending" or "in_progress");
        // A timer that fires is the agent starting on what the timer was for: it speaks a few seconds later (measured).
        if (kind == "tool_call_update" && Text(update, "status") is "completed" or "failed"
            && _tools.TryGetValue(Text(update, "toolCallId"), out var timer) && timer is { Live: true, Done: false, Name: "schedule" }) began = true;
        if (!said && !began && kind != "tool_call_update") return;
        _heardAt = now;
        // Any other call that only now reports it finished is the end of something, not the start of more.
        if (!(said || began) || _prompting || _unasked) return;
        _unasked = true;
        effects.Add(Working());
        Status(record, "busy", now);
    }

    /// <summary>
    /// What an agent says is whole when something else follows it. Said outside a turn, nothing may
    /// follow: it stayed out of the conversation (and off every viewer that was not watching it being
    /// typed) until the next message was sent. A pause is the only end it has, so a pause makes it a
    /// line; and a longer one, with no call running, ends the work as the end of a turn does.
    /// </summary>
    public List<Effect> Tick(HostRecord record, DateTime now) => Logged(Ticked(record, now), now);

    List<Effect> Ticked(HostRecord record, DateTime now)
    {
        var effects = new List<Effect>();
        if (_prompting || _loading != null || _session.Length == 0) return effects;
        var quiet = now - _heardAt;
        if (_runKind.Length > 0 && quiet >= SaidQuiet)
        {
            if (_run.ToString().Trim().Length == 0) EndRun(); else Flush(false, effects);
        }
        if (!_unasked && _runKind.Length == 0) Settle(record, now);
        if (!_unasked || _runKind.Length > 0 || quiet < WorkQuiet) return effects;
        // A timer waiting to fire is not work going on: the session rests, scheduled, until it does.
        if (_tools.Values.Any(t => t.Live && t.Shown && !t.Done && t.Name != "schedule") && quiet < CallQuiet) return effects;
        _unasked = false;
        effects.Add(Durable(new { type = "result", subtype = "success", is_error = false, result = "" }));
        record.LastEvent = "Stop";
        Status(record, Rest(now), now);
        return effects;
    }

    /// <summary>Where the names given to conversations are kept: the protocol has no rename.</summary>
    readonly IChatNames _names;
    /// <summary>The name given to the conversation open now, or empty: nothing the agent calls it replaces one.</summary>
    string _given = "";

    /// <summary>What the agent keeps on disk about a conversation, where its protocol does not say it.</summary>
    readonly IAgentDisk _disk;
    /// <summary>The open conversation's calls, while another conversation is being read to move to.</summary>
    Dictionary<string, Tool>? _held;
    /// <summary>The ids the agent gave to messages already kept: a viewer names a line by its id, so none is used twice.</summary>
    readonly HashSet<string> _lineIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Every line of the open conversation, oldest first: what was replayed, then what has been kept
    /// since. The scrollback holds only the end of it, and the protocol cannot be asked for a part
    /// of a conversation, so this is what an earlier page is read from.
    /// </summary>
    readonly List<byte[]> _log = new();
    /// <summary>For each line of the log: its id, the call it is under, and whether it is a message of the conversation itself.</summary>
    readonly List<(string Id, string Under, bool Top)> _logShape = new();
    long _logBytes;
    /// <summary>What a conversation being opened replayed, all of it: the log once that conversation is the one served.</summary>
    List<byte[]>? _opening;
    /// <summary>The most the log holds. Past it the oldest lines go, and there is no page before them.</summary>
    internal long LogMost = 64L * 1024 * 1024;
    internal const int PageBytes = 256 * 1024, PageLines = 400;

    public AcpChat(string provider, string cwd, string session, string model, string effort, string mode, int budget = 2 * 1024 * 1024, IChatNames? names = null, IAgentDisk? disk = null)
    {
        _names = names ?? new MemoryChatNames();
        _disk = disk ?? new NoAgentDisk();
        _provider = provider;
        _cwd = cwd;
        _resume = session;
        _wantModel = model;
        _wantEffort = effort;
        _wantMode = mode;
        _budget = budget;
    }

    public string CatalogFrame() => _catalog;

    Option? OptionOf(string category) => _options.FirstOrDefault(o => o.Category == category);
    string Current(string category) => OptionOf(category)?.Current ?? "";

    public string SettingsFrame() => JsonSerializer.Serialize(new { type = "settings", mode = Current("mode"), model = Current("model"), effort = Current("thought_level"), resume = "" });

    // ---- to the agent ----

    Effect Call(string method, object @params, Pending pending)
    {
        long id = _next++;
        _pending[id] = pending;
        return new Effect(Kind.ToCli, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params }));
    }

    static Effect Notify(string method, object @params) => new(Kind.ToCli, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method, @params }));
    static Effect Answer(JsonElement id, object result) => new(Kind.ToCli, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, result }));
    static Effect Durable(object line) => new(Kind.Durable, JsonSerializer.SerializeToUtf8Bytes(line));
    static Effect Live(object line) => new(Kind.Live, JsonSerializer.SerializeToUtf8Bytes(line));
    static Effect Frame(string json) => new(Kind.Frame, Encoding.UTF8.GetBytes(json));

    /// <summary>
    /// The client says what it can do for the agent: nothing. Reading and writing files and running
    /// terminals for it are offers a client may make; unoffered, the agent uses its own tools.
    /// </summary>
    public List<Effect> Start() => new()
    {
        Call("initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false },
            clientInfo = new { name = "session-deck", title = "Session Deck", version = "1" },
        }, new Pending("initialize")),
    };

    Effect Open()
    {
        if (_resume.Length > 0 && _canLoad)
        {
            _loading = _resume;
            _replay.Clear();
            _tools.Clear();
            return Call("session/load", new { sessionId = _resume, cwd = _cwd, mcpServers = Array.Empty<object>() }, new Pending("load", Session: _resume));
        }
        return Call("session/new", new { cwd = _cwd, mcpServers = Array.Empty<object>() }, new Pending("new"));
    }

    // ---- from a viewer ----

    public List<Effect> FromViewer(JsonElement line, HostRecord record, DateTime now) => Logged(Viewed(line, record, now), now);

    List<Effect> Viewed(JsonElement line, HostRecord record, DateTime now)
    {
        var effects = new List<Effect>();
        switch (Text(line, "type"))
        {
            case "user":
                if (!Get(line, "message", out var message) || !Get(message, "content", out var content)) break;
                var prompt = Prompt(content);
                if (prompt.GetArrayLength() == 0) break;
                // There is no adding to a running turn in this protocol: what is typed meanwhile is the next turn.
                if (_session.Length == 0 || _prompting || _loading != null || _resuming) _waiting.Add(prompt);
                else Send(prompt, record, now, effects);
                // OpenCode's service can fail a session once and open it the next time: a message is reason to ask again.
                if (_unopened) { _unopened = false; effects.Add(Open()); }
                break;
            case "control_response":
                if (!Get(line, "response", out var response)) break;
                string key = Text(response, "request_id");
                if (!_open.Remove(key, out var asked)) break;
                bool allow = false, always = false;
                if (Get(response, "response", out var answer))
                {
                    allow = Text(answer, "behavior") == "allow";
                    always = Get(answer, "updatedPermissions", out var kept) && kept.ValueKind == JsonValueKind.Array && kept.GetArrayLength() > 0;
                }
                effects.Add(Answer(asked.Id, Chosen(asked.Options, allow, always)));
                effects.Add(new Effect(Kind.Durable, Encoding.UTF8.GetBytes(line.GetRawText())));
                if (_open.Count == 0 && _prompting) Status(record, "busy", now);
                break;
            case "control_request":
                string id = Text(line, "request_id");
                if (!Get(line, "request", out var request)) break;
                string subtype = Text(request, "subtype");
                if (subtype == "interrupt")
                {
                    if (_prompting) { _stopping = true; CancelAsked(effects); effects.Add(Notify("session/cancel", new { sessionId = _session })); }
                    effects.Add(Live(new { type = "control_response", response = new { subtype = "success", request_id = id } }));
                    break;
                }
                (string category, string value) = subtype switch
                {
                    "set_model" => ("model", Text(request, "model")),
                    "set_permission_mode" => ("mode", Text(request, "mode")),
                    "apply_flag_settings" => ("thought_level", Get(request, "settings", out var settings) ? Text(settings, "effortLevel") : ""),
                    _ => ("", ""),
                };
                var option = OptionOf(category);
                if (option == null || value.Length == 0 || _session.Length == 0 || !option.Choices.Any(c => c.Value == value))
                {
                    effects.Add(Live(new { type = "control_response", response = new { subtype = "error", request_id = id, error = $"{NameOf(_provider)} has no such setting" } }));
                    break;
                }
                effects.Add(Call("session/set_config_option", new { sessionId = _session, configId = option.Id, value }, new Pending("config", ViewerId: id)));
                break;
        }
        return effects;
    }

    /// <summary>The option that says what the viewer chose: the agent names its own options, each of a known kind.</summary>
    static object Chosen(List<(string Id, string Kind)> options, bool allow, bool always)
    {
        string[] kinds = allow ? always ? new[] { "allow_always", "allow_once" } : new[] { "allow_once", "allow_always" } : new[] { "reject_once", "reject_always" };
        foreach (string kind in kinds)
            if (options.FirstOrDefault(o => o.Kind == kind) is { Id.Length: > 0 } hit) return new { outcome = new { outcome = "selected", optionId = hit.Id } };
        // Nothing of the kind on offer: declining is saying the request was called off.
        return new { outcome = new { outcome = "cancelled" } };
    }

    /// <summary>Requests still waiting when the turn is stopped or over: answered as called off, as the protocol asks, and closed for the viewers.</summary>
    void CancelAsked(List<Effect> effects)
    {
        foreach (var (key, asked) in _open)
        {
            effects.Add(Answer(asked.Id, new { outcome = new { outcome = "cancelled" } }));
            effects.Add(Durable(new { type = "control_cancel_request", request_id = key }));
        }
        _open.Clear();
    }

    void Send(JsonElement prompt, HostRecord record, DateTime now, List<Effect> effects)
    {
        // What the agent was saying unasked comes before what is said to it now, as it did for anyone watching.
        Flush(false, effects);
        _unasked = false;
        _prompting = true;
        // The agent does not have to say back what it was told (Antigravity does, OpenCode does not): the message is shown as sent.
        effects.Add(new Effect(Kind.Durable, UserLine("u-" + ++_made, prompt, history: false)));
        effects.Add(Working());
        effects.Add(Call("session/prompt", new { sessionId = _session, prompt }, new Pending("prompt")));
        record.LastEvent = "UserPromptSubmit";
        Status(record, "busy", now);
        if (!_titled && FirstText(prompt) is { Length: > 0 } first) { _titled = true; Title(Short(first), record, effects); }
    }

    /// <summary>A viewer's message as ACP content blocks: text, and images with their bytes.</summary>
    static JsonElement Prompt(JsonElement content)
    {
        var blocks = new List<object>();
        if (content.ValueKind == JsonValueKind.String) { if (content.GetString() is { Length: > 0 } text) blocks.Add(new { type = "text", text }); }
        else if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                if (Text(block, "type") == "text" && Text(block, "text") is { Length: > 0 } said) blocks.Add(new { type = "text", text = said });
                else if (Text(block, "type") == "image" && Get(block, "source", out var source) && Text(source, "data") is { Length: > 0 } data)
                    blocks.Add(new { type = "image", mimeType = Text(source, "media_type"), data });
            }
        return JsonSerializer.SerializeToElement(blocks);
    }

    public List<Effect> Sessions(string request, string tag)
    {
        if (!_canList || _session.Length == 0) return new() { new Effect(Kind.Reply, ChatSessions.ListFrame(request, _session, new List<ChatSessions.Entry>()), tag) };
        return new() { Call("session/list", new { cwd = _cwd }, new Pending("sessions", request, tag)) };
    }

    /// <summary>How many pages of conversations are asked for before the list is shown as it stands.</summary>
    internal const int ListPages = 10;

    /// <summary>Whether two spellings name one folder: agents write a Windows path as given, lower-cased, or as <c>/C:/…</c>.</summary>
    internal static bool SameFolder(string a, string b) => string.Equals(Folder(a), Folder(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A folder written as a URL's path is (<c>/C:/Repos/x</c>).</summary>
    static bool Placed(string folder) => folder.Length > 2 && folder[0] == '/' && folder[2] == ':';

    static string Folder(string path)
    {
        string p = path.Replace('/', '\\');
        if (p.Length >= 3 && p[0] == '\\' && char.IsAsciiLetter(p[1]) && p[2] == ':') p = p[1..];
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    /// <summary>A session id as an agent may write one (a UUID for Antigravity, <c>ses_…</c> for OpenCode): short and plain, since it goes back to the agent as is.</summary>
    internal static bool IsSessionId(string id) => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public List<Effect> Resume(string request, string session, string tag)
    {
        string refusal = !IsSessionId(session) ? "That is not a conversation."
            : session == _session ? "That conversation is the one open here."
            : !_canLoad ? $"{NameOf(_provider)} cannot reopen a conversation."
            : _prompting ? "Stop the current turn first."
            : _loading != null || _resuming || _session.Length == 0 ? "The session is already changing." : "";
        if (refusal.Length > 0) return new() { new Effect(Kind.Reply, ChatSessions.RefusedFrame(request, refusal), tag) };
        _loading = session;
        _reload = null;
        _unasked = false;
        (_replayUsed, _replayWindow) = (0, 0);
        _replay.Clear();
        // The replay fills these with the other conversation's calls. Until that one is the one open, this one's
        // calls (its timers) are set aside, not forgotten, and its subagents are still followed.
        _held = _tools;
        _tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
        EndRun();
        return new() { Call("session/load", new { sessionId = session, cwd = _cwd, mcpServers = Array.Empty<object>() }, new Pending("load", request, tag, session)) };
    }

    /// <summary>
    /// Read the agent's commands and skills from disk again. The protocol has no request for that. An
    /// agent that reads them whenever a conversation is attached to (<see cref="RereadsCommands"/>) is
    /// attached to the one already open: it replays nothing, keeps the conversation and its settings,
    /// and then says its commands, which is when the asking viewer is answered. Refused while anything
    /// is in progress, since only a conversation at rest was measured.
    /// </summary>
    public List<Effect> Commands(string request, string tag)
    {
        string refusal = !CanReload ? $"{NameOf(_provider)} cannot reload its skills in a chat."
            : _session.Length == 0 || _loading != null ? "The session is still opening."
            : _prompting ? "Reload when the turn has finished."
            : _resuming ? "The skills are already being reloaded." : "";
        if (refusal.Length > 0) return new() { new Effect(Kind.Reply, ChatSessions.NoCommandsFrame(request, refusal), tag) };
        _reload = (request, tag, Described());
        _resuming = true;
        return new() { Call("session/resume", new { sessionId = _session, cwd = _cwd, mcpServers = Array.Empty<object>() }, new Pending("commands", request, tag)) };
    }

    /// <summary>The commands the agent last said, each with what it says of it.</summary>
    Dictionary<string, string> Described()
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(_commands);
        foreach (var c in doc.RootElement.EnumerateArray()) known[Text(c, "name")] = Text(c, "description") + "\n" + Text(c, "hint");
        return known;
    }

    /// <summary>The protocol has no rename, so the name is kept by the conversation here (<see cref="IChatNames"/>) and wins over what the agent calls it.</summary>
    public List<Effect> Rename(string request, string name, string tag, HostRecord record)
    {
        string refusal = _session.Length == 0 ? "There is no conversation to name yet."
            : _loading != null ? "The session is already changing."
            : !_names.Set(_session, name) ? "The name could not be saved." : "";
        if (refusal.Length > 0) return new() { new Effect(Kind.Reply, ChatSessions.NotRenamedFrame(request, refusal), tag) };
        var effects = new List<Effect>();
        _given = name;
        _titled = true;
        Title(name, record, effects);
        effects.Add(new Effect(Kind.Reply, ChatSessions.RenamedFrame(request, name), tag));
        return effects;
    }

    /// <summary>
    /// The conversation before the message a viewer names (the oldest it shows), as much as makes a
    /// page. Nothing when the message is not known, or is the first there is.
    /// </summary>
    public List<Effect> History(string request, string uuid, string tag)
    {
        int before = uuid.Length > 0 ? _logShape.FindIndex(s => s.Id == uuid) : -1, start = Math.Max(before, 0);
        long size = 0;
        while (start > 0 && before - start < PageLines && (start == before || size + _log[start - 1].Length + 1 <= PageBytes)) size += _log[--start].Length + 1;
        start = Whole(start, before);
        var lines = before > 0 ? _log.GetRange(start, before - start) : new List<byte[]>();
        return new() { new Effect(Kind.Reply, ChatStream.HistoryFrame(new ChatTranscript.Page(lines, before > 0 && start > 0 ? start : -1), request), tag) };
    }

    // ---- from the agent ----

    public List<Effect> FromCli(JsonElement msg, HostRecord record, DateTime now) => Logged(Told(msg, record, now), now);

    /// <summary>
    /// Where a page that was to start at <paramref name="start"/> has to start for a viewer to use it.
    /// A viewer asks for the page before the first message of the conversation itself that it shows,
    /// so a page that holds none would be asked for again and again; and it shows a subagent's line
    /// only under a call it has, so the page takes in the call of every such line in it. That can
    /// make it longer than a page is meant to be: as long as the subagent's work.
    /// </summary>
    int Whole(int start, int before)
    {
        for (bool moved = true; moved && start > 0;)
        {
            moved = false;
            var here = new HashSet<string>(StringComparer.Ordinal);
            for (int i = start; i < before; i++) here.Add(_logShape[i].Id);
            var asked = new HashSet<string>(StringComparer.Ordinal);
            for (int i = start; i < before; i++)
            {
                string under = _logShape[i].Under;
                if (under.Length == 0 || here.Contains(under) || !asked.Add(under)) continue;
                int call = _logShape.FindLastIndex(start - 1, start, s => s.Id == under);
                if (call < 0) continue;
                start = call;
                moved = true;
                break;
            }
            while (start > 0 && !_logShape[start].Top)
            {
                start--;
                moved = true;
            }
        }
        return start;
    }

    /// <summary>
    /// What is about to be kept is given its time and remembered, in the order it is kept. A message
    /// with no time of its own is being said now; one read back from an earlier process keeps what
    /// it has.
    /// </summary>
    List<Effect> Logged(List<Effect> effects, DateTime now)
    {
        for (int i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            if (effect.Kind == Kind.Reset)
            {
                _log.Clear();
                _logShape.Clear();
                _logBytes = 0;
                foreach (var line in _opening ?? effect.Seed!.Lines) Remember(line);
                _opening = null;
            }
            else if (effect.Kind == Kind.Durable)
            {
                byte[] line = ChatLines.StampedNow(effect.Data, now);
                effects[i] = effect with { Data = line };
                Remember(line);
            }
        }
        return effects;
    }

    void Remember(byte[] line)
    {
        _log.Add(line);
        _logShape.Add(Shape(line));
        _logBytes += line.Length;
        int gone = 0;
        while (_logBytes > LogMost && gone < _log.Count - 1) _logBytes -= _log[gone++].Length;
        if (gone == 0) return;
        _log.RemoveRange(0, gone);
        _logShape.RemoveRange(0, gone);
    }

    /// <summary>A line's id, the call it is under (a subagent's line, or word of the subagent's task), and whether it is a message of the conversation itself.</summary>
    static (string Id, string Under, bool Top) Shape(byte[] line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = Text(root, "type"), under = Text(root, "parent_tool_use_id");
            if (under.Length == 0 && type == "system") under = Text(root, "tool_use_id");
            return (Text(root, "uuid"), under, type is "user" or "assistant" && under.Length == 0);
        }
        catch (JsonException) { return ("", "", false); }
    }

    List<Effect> Told(JsonElement msg, HostRecord record, DateTime now)
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
        if (hasId)
        {
            if (method == "session/request_permission" && Text(p, "sessionId") == _session) Asked(id, p, record, now, effects);
            // About a conversation that is not the one shown here (OpenCode's child sessions): nobody to put it to.
            else if (method == "session/request_permission") effects.Add(Answer(id, new { outcome = new { outcome = "cancelled" } }));
            // Anything else the agent asks of its client (a file to read, a terminal to run) was never offered.
            else effects.Add(new Effect(Kind.ToCli, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Session Deck does not handle {method}" } })));
            return effects;
        }
        if (method != "session/update" || !Get(p, "update", out var update)) return effects;
        string about = Text(p, "sessionId");
        bool replay = _loading != null && about == _loading;
        if (!replay && about != _session) return effects;
        Update(update, replay, record, now, effects);
        return effects;
    }

    void Update(JsonElement update, bool replay, HostRecord record, DateTime now, List<Effect> effects)
    {
        string kind = Text(update, "sessionUpdate");
        if (!replay) Heard(kind, update, record, now, effects);
        switch (kind)
        {
            case "agent_message_chunk": Chunk("text", update, replay, effects); break;
            case "agent_thought_chunk": Chunk("thinking", update, replay, effects); break;
            // What was said to the agent is shown when it is sent; said back, it is only news while the past is replayed.
            case "user_message_chunk": if (replay) Chunk("user", update, replay, effects); break;
            case "tool_call":
            case "tool_call_update":
                Flush(replay, effects);
                Tooled(update, replay, record, now, effects);
                break;
            case "available_commands_update":
                if (Get(update, "availableCommands", out var commands) && commands.ValueKind == JsonValueKind.Array)
                {
                    _commands = JsonSerializer.Serialize(commands.EnumerateArray().Where(c => Text(c, "name").Length > 0)
                        .Select(c => new { name = Text(c, "name"), description = Text(c, "description"), hint = Get(c, "input", out var input) ? Text(input, "hint") : "" }));
                    Catalog(effects);
                    if (_reload is { } asked)
                    {
                        _reload = null;
                        var known = Described();
                        effects.Add(new Effect(Kind.Reply, ChatSessions.CommandsFrame(asked.Request, known.Count,
                            known.Keys.Where(n => !asked.Before.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal),
                            asked.Before.Keys.Where(n => !known.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal),
                            known.Where(c => asked.Before.TryGetValue(c.Key, out var was) && was != c.Value).Select(c => c.Key).OrderBy(n => n, StringComparer.Ordinal)), asked.Tag));
                    }
                }
                break;
            case "usage_update":
            {
                // How full the context is, as the agent counts it (Antigravity and OpenCode both say `used` of `size`).
                if (!Get(update, "used", out var u) || u.ValueKind != JsonValueKind.Number || !u.TryGetInt64(out long used) || used < 0) break;
                long size = Get(update, "size", out var z) && z.ValueKind == JsonValueKind.Number && z.TryGetInt64(out long all) && all > 0 ? all : 0;
                // Said while a conversation is replayed, it is about the one being opened, not the one still shown.
                if (replay) (_replayUsed, _replayWindow) = (used, size);
                else (record.ContextTokens, record.ContextWindow) = (used, size);
                break;
            }
            case "session_info_update":
                if (Text(update, "title") is not { Length: > 0 } title) break;
                // Said while a conversation is replayed, it names the one being opened, not the one still shown.
                if (replay) { if (_loading != null) _titles[_loading] = title; }
                else if (_given.Length == 0) { _titled = true; Title(title, record, effects); }
                else _titles[_session] = title;
                break;
            case "config_option_update":
                if (Get(update, "configOptions", out var options)) { Configure(options, record); Catalog(effects); effects.Add(Frame(SettingsFrame())); }
                break;
            case "current_mode_update":
                if (OptionOf("mode") is { } mode && Text(update, "currentModeId") is { Length: > 0 } now2)
                {
                    _options = _options.Select(o => o == mode ? o with { Current = now2 } : o).ToList();
                    record.PermissionMode = now2;
                    effects.Add(Frame(SettingsFrame()));
                }
                break;
        }
    }

    /// <summary>
    /// A replayed conversation with the times its messages were said, where the agent keeps them:
    /// Antigravity replays none and writes one for every step to disk (<see cref="AgyTimes"/>).
    /// What it keeps is its own business and can change, so anything unexpected leaves the lines as they came.
    /// </summary>
    List<byte[]> Timed(List<byte[]> lines, string session)
    {
        if (_provider != "Agy" || lines.Count == 0) return lines;
        try { return AgyTimes.Stamp(lines, _disk.Steps(session)); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException) { return lines; }
    }

    // ---- text: chunks with no end mark ----

    void Chunk(string kind, JsonElement update, bool replay, List<Effect> effects)
    {
        string text = Get(update, "content", out var content) ? Text(content, "text") : "", id = Text(update, "messageId");
        // A message is whole when something else follows it: another kind of thing, or a chunk of another message.
        if (_runKind.Length > 0 && (_runKind != kind || (id.Length > 0 && _runId.Length > 0 && id != _runId))) Flush(replay, effects);
        if (_runKind.Length == 0)
        {
            _runKind = kind;
            _runId = id;
            if (!replay) effects.Add(Live(new { type = "stream_event", @event = new { type = "content_block_start", content_block = new { type = kind } }, parent_tool_use_id = (string?)null }));
        }
        if (text.Length == 0) return;
        _run.Append(text);
        if (replay) return;
        object delta = kind == "text" ? new { type = "text_delta", text } : new { type = "thinking_delta", thinking = text };
        effects.Add(Live(new { type = "stream_event", @event = new { type = "content_block_delta", delta }, parent_tool_use_id = (string?)null }));
    }

    void EndRun()
    {
        _run.Clear();
        _runKind = _runId = "";
    }

    /// <summary>What has been said so far becomes a line of the conversation.</summary>
    void Flush(bool replay, List<Effect> effects)
    {
        if (_runKind.Length == 0) return;
        bool given = _runId.Length > 0;
        string kind = _runKind, text = _run.ToString(), id = given ? _runId + (kind == "thinking" ? ":t" : "") : (kind == "user" ? "u-" : "a-") + ++_made;
        EndRun();
        if (text.Trim().Length == 0 && (replay || kind != "text")) return;
        // A message the agent gave an id can be kept in two parts (said, a pause, said on).
        if (given && !_lineIds.Add(id)) id += "." + ++_made;
        byte[] line = kind switch
        {
            "user" => UserLine(id, JsonSerializer.SerializeToElement(new[] { new { type = "text", text } }), replay),
            "thinking" => Assistant(id, new { type = "thinking", thinking = text.Trim() }, replay),
            _ => Assistant(id, new { type = "text", text }, replay),
        };
        Keep(line, replay, effects);
    }

    void Keep(byte[] line, bool replay, List<Effect> effects)
    {
        if (replay) _replay.Add(line);
        else effects.Add(new Effect(Kind.Durable, line));
    }

    // ---- subagents ----

    /// <summary>Antigravity's call that starts a subagent.</summary>
    const string Spawn = "invoke_subagent";
    /// <summary>What a subagent with no role of its own is called.</summary>
    const string NoRole = "Subagent";
    const string Killed = "SUBAGENT_STATE_KILLED";

    /// <summary>
    /// A subagent the open conversation started in this process, under the call that started it.
    /// Antigravity ends a call that starts one at once and says no more of it over the protocol;
    /// what the subagent does is read from its transcript on disk (<see cref="AgyWatch"/>) and
    /// shown as a Claude subagent's work is: lines under the call, and a task that is running
    /// until the subagent has had its say. Its file never says it is done (it stays alive to be
    /// spoken to again), so it is over when its last step is words with no call: the end of a turn.
    /// </summary>
    sealed class Helper
    {
        public string Id = "", Call = "", Tag = "", Role = "", Last = "";
        public int Uses, Made;
        /// <summary>Its turn has ended, or it was killed.</summary>
        public bool Over;
        /// <summary>Viewers have been told it is at work, and not yet that it stopped.</summary>
        public bool Told;
        public DateTime HeardAt;
        public readonly Queue<string> Open = new();
    }

    /// <summary>The calls of this process that started a subagent not yet seen on disk, oldest first.</summary>
    readonly List<(string Call, DateTime At)> _spawns = new();
    /// <summary>Subagents seen on disk with no call yet to put them under, and what they have done meanwhile.</summary>
    readonly List<(AgySubagent Subagent, List<AgyStep> Steps, DateTime At)> _unclaimed = new();
    /// <summary>Subagents no call asked for in time: not this conversation's to show, whatever is called for later.</summary>
    readonly HashSet<string> _strangers = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a call and a subagent wait for each other. Measured: the subagent's file is there 0.1
    /// to 1.6 s after its call is announced. A call nothing came for in this long started nothing, and
    /// a subagent no call came for was not started here; without the limit either would be paired with
    /// the next of the other kind, and every pairing after it would be off by one.
    /// </summary>
    internal static readonly TimeSpan SpawnWait = TimeSpan.FromSeconds(60);
    readonly Dictionary<string, Helper> _helpers = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a subagent that has not ended its turn is taken to be at work with nothing new on
    /// disk. A step is written when it ends, so a long command is silence; and a subagent whose
    /// process died is silence for ever.
    /// </summary>
    internal static readonly TimeSpan HelperQuiet = TimeSpan.FromMinutes(30);

    bool Helping(DateTime now) => _helpers.Values.Any(h => !h.Over && now - h.HeardAt < HelperQuiet);

    /// <summary>The open conversation's subagents are no longer followed: it is another conversation now, or none.</summary>
    void Alone()
    {
        _helpers.Clear();
        _spawns.Clear();
        _unclaimed.Clear();
        _strangers.Clear();
    }

    /// <summary>What the open conversation's subagents wrote to disk since the last look.</summary>
    public List<Effect> Outside(AgyNews news, HostRecord record, DateTime now) => Logged(Seen(news, record, now), now);

    List<Effect> Seen(AgyNews news, HostRecord record, DateTime now)
    {
        var effects = new List<Effect>();
        if (news.Conversation != _session) return effects;
        bool reported = false;
        foreach (var one in news.Subagents)
        {
            if (_helpers.TryGetValue(one.Subagent.Id, out var helper))
            {
                bool was = helper.Over;
                foreach (var line in Walked(helper, one.Steps, one.Subagent.State, now, history: false)) effects.Add(new Effect(Kind.Durable, line));
                reported |= !was && helper.Over && one.Subagent.State != Killed;
                continue;
            }
            if (_strangers.Contains(one.Subagent.Id)) continue;
            int waiting = _unclaimed.FindIndex(u => u.Subagent.Id == one.Subagent.Id);
            if (waiting >= 0)
            {
                _unclaimed[waiting].Steps.AddRange(one.Steps);
                _unclaimed[waiting] = (one.Subagent, _unclaimed[waiting].Steps, _unclaimed[waiting].At);
            }
            else _unclaimed.Add((one.Subagent, new List<AgyStep>(one.Steps), now));
        }
        Claim(now, effects);
        // A subagent that has had its say has reported to the agent, which goes on from there (measured: it speaks
        // within seconds, and can be a second behind the subagent's last words): the session is at work, not done.
        if (reported && !_prompting && !_unasked)
        {
            _unasked = true;
            _heardAt = now;
            effects.Add(Working());
            Status(record, "busy", now);
        }
        Settle(record, now);
        return effects;
    }

    /// <summary>
    /// Puts each subagent seen on disk under the call that started it: the oldest call still without
    /// one, since the agent starts them in the order it calls for them. A call that names a role is
    /// only given a subagent of that role, so one that is not this conversation's to show is never
    /// put under a call that did not start it.
    /// </summary>
    void Claim(DateTime now, List<Effect> effects)
    {
        _spawns.RemoveAll(s => now - s.At > SpawnWait);
        for (int i = 0; i < _unclaimed.Count; i++)
        {
            var (subagent, steps, seen) = _unclaimed[i];
            if (now - seen > SpawnWait)
            {
                _strangers.Add(subagent.Id);
                _unclaimed.RemoveAt(i--);
                continue;
            }
            int call = _spawns.FindIndex(s => _tools.TryGetValue(s.Call, out var tool) && tool.Shown && (RoleOf(tool).Length == 0 || subagent.Role.Length == 0 || RoleOf(tool) == subagent.Role));
            if (call < 0) continue;
            var helper = new Helper { Id = subagent.Id, Call = _spawns[call].Call, Tag = "sub-" + subagent.Id[..8], Role = subagent.Role.Length > 0 ? subagent.Role : NoRole, HeardAt = now };
            _spawns.RemoveAt(call);
            _unclaimed.RemoveAt(i--);
            _helpers[subagent.Id] = helper;
            foreach (var line in Walked(helper, steps, subagent.State, now, history: false)) effects.Add(new Effect(Kind.Durable, line));
        }
    }

    static string RoleOf(Tool tool) =>
        tool.Input.ValueKind == JsonValueKind.Object && tool.Input.TryGetProperty("Role", out var role) && role.ValueKind == JsonValueKind.String ? role.GetString() ?? "" : "";

    /// <summary>
    /// A subagent's steps as lines under the call that started it, with the task lines that say it
    /// is at work, how far along, and that it has stopped. Its thinking and the messages it is sent
    /// are not shown: what it says and does is.
    /// </summary>
    List<byte[]> Walked(Helper helper, IReadOnlyList<AgyStep> steps, string state, DateTime now, bool history)
    {
        var lines = new List<byte[]>();
        bool moved = false;
        foreach (var step in steps)
        {
            helper.HeardAt = now;
            byte[] At(byte[] line) => history && step.At is { } when ? ChatLines.Stamped(line, new DateTimeOffset(DateTime.SpecifyKind(when, DateTimeKind.Utc))) : line;
            if (step.Type == AgyStep.Planner)
            {
                bool said = step.Content.Trim().Length > 0;
                // The agent speaks again once every call of its last step has come back: one still open then never will.
                helper.Open.Clear();
                if (said) lines.Add(At(Assistant($"{helper.Tag}-{++helper.Made}", new { type = "text", text = step.Content }, history, helper.Call)));
                foreach (var made in step.Calls)
                {
                    // A call the pane has no reading for is told by the first thing it was given (a message's words).
                    string about = made.Args.Values.FirstOrDefault(v => v.Length > 0) ?? "";
                    var tool = new Tool { Id = $"{helper.Tag}-{++helper.Made}", Name = made.Name, Title = about.Length > 120 ? about[..120] : about, Kind = KindOf(made.Name), Input = JsonSerializer.SerializeToElement(made.Args) };
                    var input = Input(tool);
                    helper.Open.Enqueue(tool.Id);
                    helper.Uses++;
                    helper.Last = (NameOfTool(tool) + " " + Brief(input)).Trim();
                    lines.Add(At(Assistant(tool.Id, new { type = "tool_use", id = tool.Id, name = NameOfTool(tool), input }, history, helper.Call)));
                    moved = true;
                }
                helper.Over = said && step.Calls.Count == 0;
            }
            else
            {
                // Anything else is something it will answer: what a call came back with, or a message sent to it.
                helper.Over = false;
                if (step.Type != AgyStep.Outcome || helper.Open.Count == 0) continue;
                string id = helper.Open.Dequeue();
                lines.Add(At(Line(new Dictionary<string, object?>
                {
                    ["type"] = "user", ["uuid"] = id + ":r",
                    ["message"] = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = id, content = Unwrapped(step.Content), is_error = false } } },
                    ["parent_tool_use_id"] = helper.Call,
                }, history)));
                moved = true;
            }
        }
        if (state == Killed) helper.Over = true;
        // One that had already had its say when it was first seen is still shown as a task: begun, and over.
        if (!helper.Told && (!helper.Over || lines.Count > 0))
        {
            helper.Told = true;
            lines.Insert(0, TaskLine(helper, "task_started", history, new() { ["description"] = helper.Role, ["is_backgrounded"] = true }));
        }
        if (moved && helper.Told) lines.Add(TaskLine(helper, "task_progress", history, new() { ["description"] = helper.Last, ["usage"] = new { tool_uses = helper.Uses } }));
        if (helper.Over && helper.Told)
        {
            helper.Told = false;
            lines.Add(TaskLine(helper, "task_notification", history, new() { ["status"] = state == Killed ? "stopped" : "completed" }));
        }
        return lines;
    }

    static byte[] TaskLine(Helper helper, string subtype, bool history, Dictionary<string, object?> more)
    {
        var line = new Dictionary<string, object?> { ["type"] = "system", ["subtype"] = subtype, ["tool_use_id"] = helper.Call, ["task_id"] = helper.Id };
        foreach (var (name, value) in more) line[name] = value;
        return Line(line, history);
    }

    /// <summary>The names Antigravity's transcript gives its tools, as kinds the protocol would have said. Only the ones measured: any other keeps its own name.</summary>
    static string KindOf(string name) => name switch
    {
        "run_command" => "execute",
        "write_file" or "edit_file" => "edit",
        _ => "other",
    };

    /// <summary>The one thing a call was given that says what it is about.</summary>
    static string Brief(Dictionary<string, object?> input)
    {
        foreach (string name in new[] { "command", "file_path", "pattern", "url", "description" })
            if (input.TryGetValue(name, out var value) && value is string { Length: > 0 } said) return said.Length > 80 ? said[..80] : said;
        return "";
    }

    /// <summary>What a step came back with, without the lines Antigravity heads it with (when it began and ended, repeated).</summary>
    static string Unwrapped(string content)
    {
        const string output = "\nCommand output:\n";
        int at = content.StartsWith("Created At:", StringComparison.Ordinal) ? content.IndexOf(output, StringComparison.Ordinal) : -1;
        if (at >= 0) return content[(at + output.Length)..];
        int start = 0;
        while (start < content.Length)
        {
            int end = content.IndexOf('\n', start);
            string line = end < 0 ? content[start..] : content[start..end];
            if (!line.StartsWith("Created At:", StringComparison.Ordinal) && !line.StartsWith("Completed At:", StringComparison.Ordinal)) break;
            start = end < 0 ? content.Length : end + 1;
        }
        return content[start..];
    }

    /// <summary>
    /// A replayed conversation with what its subagents did put under the calls that started them,
    /// read from disk. Only when every such call has its subagent and each is of the role its call
    /// named: otherwise which belongs to which is a guess, and the replay stays as it came. They
    /// belong to a process that is gone, so none is shown as still at work.
    /// </summary>
    List<byte[]> Helped(List<byte[]> lines, string session)
    {
        if (_provider != "Agy" || lines.Count == 0) return lines;
        try
        {
            var subagents = _disk.Subagents(session);
            if (subagents.Count == 0) return lines;
            var calls = new List<(int Line, string Id, string Role)>();
            var ids = new List<string>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var root = doc.RootElement;
                ids.Add(Text(root, "uuid"));
                if (Text(root, "type") != "assistant" || !Get(root, "message", out var message) || !Get(message, "content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var block in content.EnumerateArray())
                    if (Text(block, "type") == "tool_use" && Text(block, "name") == "Agent" && Text(block, "id") is { Length: > 0 } id)
                        calls.Add((i, id, Get(block, "input", out var input) ? Text(input, "description") : ""));
            }
            if (calls.Count != subagents.Count) return lines;
            for (int k = 0; k < calls.Count; k++)
                if (subagents[k].Role.Length > 0 && calls[k].Role != NoRole && calls[k].Role != subagents[k].Role) return lines;

            var after = new Dictionary<int, int>();
            for (int k = 0; k < calls.Count; k++)
            {
                int at = calls[k].Line;
                for (int j = at + 1; j < lines.Count && j <= at + 64; j++)
                    if (ids[j] == calls[k].Id + ":r") { at = j; break; }
                after[at] = k;
            }
            var with = new List<byte[]>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                with.Add(lines[i]);
                if (!after.TryGetValue(i, out int which)) continue;
                var subagent = subagents[which];
                var helper = new Helper { Id = subagent.Id, Call = calls[which].Id, Tag = "sub-" + subagent.Id[..8], Role = subagent.Role.Length > 0 ? subagent.Role : NoRole };
                with.AddRange(Walked(helper, _disk.Steps(subagent.Id), subagent.State, DateTime.MinValue, history: true));
                if (helper.Told) with.Add(TaskLine(helper, "task_notification", true, new() { ["status"] = "stopped" }));
            }
            return with;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException) { return lines; }
    }

    // ---- tool calls ----

    Tool ToolOf(JsonElement call)
    {
        string id = Text(call, "toolCallId");
        if (!_tools.TryGetValue(id, out var tool)) _tools[id] = tool = new Tool { Id = id };
        if (Text(call, "title") is { Length: > 0 } title) tool.Title = title;
        if (Text(call, "kind") is { Length: > 0 } kind) tool.Kind = kind;
        if (Text(call, "name") is { Length: > 0 } name) tool.Name = name;
        if (Text(call, "status") is { Length: > 0 } status) tool.Status = status;
        // An update carries what changed: an empty input does not take back the one already given.
        if (Get(call, "rawInput", out var input) && input.ValueKind == JsonValueKind.Object && input.EnumerateObject().Any()) tool.Input = input.Clone();
        if (Get(call, "content", out var content) && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0) tool.Content = content.Clone();
        if (Get(call, "rawOutput", out var output) && output.ValueKind is JsonValueKind.Object or JsonValueKind.String) tool.Output = output.Clone();
        return tool;
    }

    void Tooled(JsonElement call, bool replay, HostRecord record, DateTime now, List<Effect> effects)
    {
        if (Text(call, "toolCallId").Length == 0) return;
        var tool = ToolOf(call);
        if (!replay) tool.Live = true;
        if (!replay && tool.Name == Spawn && !tool.Spawned)
        {
            tool.Spawned = true;
            _spawns.Add((tool.Id, now));
        }
        bool ended = tool.Status is "completed" or "failed";
        // OpenCode announces a call before it knows what the call is ("shell", no command yet): it is shown once it runs.
        if (!tool.Shown && (ended || tool.Status == "in_progress")) Show(tool, replay, record, effects);
        // A call that failed started nothing: the next subagent to show up is not its.
        if (ended && tool.Spawned && Failed(tool)) _spawns.RemoveAll(s => s.Call == tool.Id);
        if (!replay && tool.Spawned && _unclaimed.Count > 0) Claim(now, effects);
        if (!ended || tool.Done) return;
        tool.Done = true;
        // OpenCode runs a call it was waiting to ask about once an "always" given meanwhile covers it, and never
        // takes the question back: the card would stay up over a call that is over.
        foreach (var (key, asked) in _open.Where(o => o.Value.Tool == tool.Id).ToList())
        {
            _open.Remove(key);
            effects.Add(Answer(asked.Id, Failed(tool) ? new { outcome = new { outcome = "cancelled" } } : Chosen(asked.Options, true, false)));
            effects.Add(Durable(new { type = "control_cancel_request", request_id = key }));
            if (_open.Count == 0 && _prompting) Status(record, "busy", now);
        }
        Keep(Line(new Dictionary<string, object?>
        {
            // Not the call's own id: a viewer names the oldest line it shows to ask for the page before it.
            ["type"] = "user", ["uuid"] = tool.Id + ":r",
            ["message"] = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = tool.Id, content = Result(tool), is_error = Failed(tool) } } },
            ["parent_tool_use_id"] = null,
        }, replay), replay, effects);
    }

    void Show(Tool tool, bool replay, HostRecord record, List<Effect> effects)
    {
        tool.Shown = true;
        if (!replay) record.LastTool = NameOfTool(tool);
        Keep(Assistant(tool.Id, new { type = "tool_use", id = tool.Id, name = NameOfTool(tool), input = Input(tool) }, replay), replay, effects);
    }

    /// <summary>The agent's own name for a call is its implementation's ("run_command", "shell"); the protocol's kind says what it is to a reader.</summary>
    static string NameOfTool(Tool tool) => tool.Name == Spawn ? "Agent" : tool.Kind switch
    {
        "execute" => "Shell",
        "edit" => "Edit",
        "read" => "Read",
        "search" => "Search",
        "delete" => "Delete",
        "move" => "Move",
        "fetch" => "Fetch",
        "think" => "Think",
        // Its own name, as words: "send_message" is how the agent's code spells it.
        _ => tool.Name.Length > 0 ? char.ToUpperInvariant(tool.Name[0]) + tool.Name[1..].Replace('_', ' ') : tool.Title.Length > 0 ? tool.Title : "Tool",
    };

    /// <summary>
    /// What the call was given, under the names the pane reads. Each agent names its tools' inputs
    /// its own way (<c>CommandLine</c>, <c>command</c>; <c>Target</c>/<c>Replacement</c>,
    /// <c>oldString</c>/<c>newString</c>), so they are read by meaning, and an edit the agent
    /// describes only as a diff is read from the diff.
    /// </summary>
    static Dictionary<string, object?> Input(Tool tool)
    {
        string Raw(params string[] names)
        {
            if (tool.Input.ValueKind != JsonValueKind.Object) return "";
            foreach (var property in tool.Input.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String && names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return property.Value.GetString() ?? "";
            return "";
        }
        bool Has(params string[] names) => tool.Input.ValueKind == JsonValueKind.Object && tool.Input.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && names.Contains(p.Name, StringComparer.OrdinalIgnoreCase));

        // A subagent's call reads as Claude's does: who it is, and what it was asked.
        if (tool.Name == Spawn) return new() { ["description"] = Raw("Role") is { Length: > 0 } role ? role : NoRole, ["prompt"] = Raw("Prompt"), ["subagent_type"] = Raw("TypeName") };
        string command = Raw("command", "CommandLine", "cmd"), path = Raw("path", "file_path", "filePath", "AbsolutePath", "TargetFile", "file");
        if (command.Length > 0) return new() { ["command"] = command };
        if (Has("oldString", "old_string", "Target", "oldText") && Has("newString", "new_string", "Replacement", "newText"))
            return new() { ["file_path"] = path, ["old_string"] = Raw("oldString", "old_string", "Target", "oldText"), ["new_string"] = Raw("newString", "new_string", "Replacement", "newText") };
        if (path.Length > 0 && Has("content", "CodeContent", "text")) return new() { ["file_path"] = path, ["content"] = Raw("content", "CodeContent", "text") };
        if (tool.Content.ValueKind == JsonValueKind.Array)
            foreach (var part in tool.Content.EnumerateArray())
                if (Text(part, "type") == "diff")
                    return new() { ["file_path"] = Text(part, "path") is { Length: > 0 } changed ? changed : path, ["old_string"] = Text(part, "oldText"), ["new_string"] = Text(part, "newText") };
        if (path.Length > 0) return new() { ["file_path"] = path };
        if (Raw("pattern", "query", "Query", "SearchPattern") is { Length: > 0 } pattern) return new() { ["pattern"] = pattern };
        if (Raw("url", "Url") is { Length: > 0 } url) return new() { ["url"] = url };
        return new() { ["description"] = tool.Title };
    }

    /// <summary>What the call came back with: its text content, else whatever the agent calls its output.</summary>
    static string Result(Tool tool)
    {
        var parts = new List<string>();
        if (tool.Content.ValueKind == JsonValueKind.Array)
            foreach (var part in tool.Content.EnumerateArray())
                if (Text(part, "type") == "content" && Get(part, "content", out var inner) && Text(inner, "text") is { Length: > 0 } text) parts.Add(text);
        string output = parts.Count > 0 ? string.Join("\n", parts) : tool.Output.ValueKind == JsonValueKind.String ? tool.Output.GetString() ?? "" : Text(tool.Output, "output");
        // Antigravity wraps a command's output in a header of its own (when it started, when it ended, how it exited),
        // in the text it shows as much as in what it calls the output.
        int at = output.StartsWith("Created At:", StringComparison.Ordinal) ? output.IndexOf("\nCommand output:\n", StringComparison.Ordinal) : -1;
        return at >= 0 ? output[(at + "\nCommand output:\n".Length)..] : output;
    }

    /// <summary>A call the agent calls finished can still have failed: a command says so by how it exited.</summary>
    static bool Failed(Tool tool)
    {
        if (tool.Status == "failed") return true;
        if (tool.Output.ValueKind != JsonValueKind.Object) return false;
        foreach (string name in new[] { "exit_code", "exitCode" })
            if (Get(tool.Output, name, out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int exit)) return exit != 0;
        return Get(tool.Output, "metadata", out var meta) && Get(meta, "exit", out var status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int left) && left != 0;
    }

    void Asked(JsonElement id, JsonElement p, HostRecord record, DateTime now, List<Effect> effects)
    {
        if (!Get(p, "toolCall", out var call) || Text(call, "toolCallId").Length == 0) { effects.Add(Answer(id, new { outcome = new { outcome = "cancelled" } })); return; }
        Flush(false, effects);
        var tool = ToolOf(call);
        if (!tool.Shown) Show(tool, false, record, effects);
        var options = new List<(string Id, string Kind)>();
        bool always = false;
        if (Get(p, "options", out var offered) && offered.ValueKind == JsonValueKind.Array)
            foreach (var option in offered.EnumerateArray())
            {
                options.Add((Text(option, "optionId"), Text(option, "kind")));
                always |= Text(option, "kind") == "allow_always";
            }
        string key = RequestPrefix + id.GetRawText().Trim('"');
        _open[key] = (id.Clone(), options, tool.Id);
        // "Always" is whatever the agent means by it, and the protocol does not say what it covers: the rule is offered
        // without naming anything. OpenCode keeps it for the folder, past the session (measured, 2.2.6: a new session
        // there is not asked again).
        object[] remember = always
            ? new object[] { new { type = "addRules", rules = new[] { new { toolName = "" } }, behavior = "allow", destination = _provider == "OpenCode" ? "localSettings" : "session" } }
            : Array.Empty<object>();
        effects.Add(Durable(new
        {
            type = "control_request", request_id = key,
            request = new { subtype = "can_use_tool", tool_name = NameOfTool(tool), tool_use_id = tool.Id, description = "", input = Input(tool), permission_suggestions = remember },
        }));
        record.LastTool = NameOfTool(tool);
        record.LastEvent = "PermissionRequest";
        Status(record, "waiting", now);
    }

    // ---- answers ----

    void Answered(Pending pending, JsonElement msg, HostRecord record, DateTime now, List<Effect> effects)
    {
        bool ok = Get(msg, "result", out var result) && result.ValueKind == JsonValueKind.Object;
        string why = Get(msg, "error", out var error) ? Text(error, "message") : "";
        switch (pending.What)
        {
            case "initialize":
                if (ok && Get(result, "agentCapabilities", out var can))
                {
                    _canLoad = Get(can, "loadSession", out var load) && load.ValueKind == JsonValueKind.True;
                    _canList = Get(can, "sessionCapabilities", out var sessions) && Get(sessions, "list", out _);
                    _canResume = Get(can, "sessionCapabilities", out var again) && Get(again, "resume", out _);
                }
                effects.Add(Open());
                break;
            case "new":
                if (!ok)
                {
                    effects.Add(Durable(new { type = "host", subtype = "stderr", text = $"{NameOf(_provider)} could not start a conversation" + (why.Length > 0 ? ": " + why : ".") + " Send a message to try again." }));
                    _unopened = true;
                    break;
                }
                _session = Text(result, "sessionId");
                Opened(result, record, effects);
                record.SessionId = _session;
                Next(record, now, effects);
                break;
            case "load":
            {
                string session = pending.Session;
                bool startup = pending.Request.Length == 0 && pending.Tag.Length == 0;
                _loading = null;
                if (!ok)
                {
                    _replay.Clear();
                    (_tools, _held) = (_held ?? _tools, null);
                    EndRun();
                    string note = "Could not resume that conversation" + (why.Length > 0 ? ": " + why : ".");
                    if (!startup) { effects.Add(new Effect(Kind.Reply, ChatSessions.RefusedFrame(pending.Request, note), pending.Tag)); break; }
                    // The conversation this session was started on is gone: a new one, and a line that says so.
                    effects.Add(Durable(new { type = "host", subtype = "stderr", text = note + " A new one was started." }));
                    _resume = "";
                    effects.Add(Open());
                    break;
                }
                Flush(true, effects);
                _session = session;
                _open.Clear();
                _held = null;
                Alone();
                var lines = Helped(Timed(new List<byte[]>(_replay), session), session);
                _replay.Clear();
                _opening = new List<byte[]>(lines);
                int size = lines.Sum(l => l.Length + 1), drop = 0;
                while (drop < lines.Count - 1 && size > _budget) size -= lines[drop++].Length + 1;
                // What is shown begins at a message of the conversation itself: a subagent's lines stay with their
                // call, in the page before.
                while (drop < lines.Count - 1 && !Shape(lines[drop]).Top) drop++;
                lines.RemoveRange(0, drop);
                _given = _names.Of(session);
                string title = _given.Length > 0 ? _given : _titles.GetValueOrDefault(session, "");
                bool named = title.Length > 0;
                if (!named) title = Short(FirstSaid(lines));
                _titled = title.Length > 0;
                Opened(result, record, effects);
                record.SessionId = _session;
                // The conversation's own, or none: never the figure of the one that was open before.
                (record.ContextTokens, record.ContextWindow) = (_replayUsed, _replayWindow);
                record.Title = title;
                record.LastTool = "";
                record.LastEvent = "";
                Status(record, "idle", now);
                effects.Add(new Effect(Kind.Reset, Array.Empty<byte>(), Seed: new ChatSeed(_session, title, "", lines, drop > 0)));
                // A conversation is replayed without its name: the agent's list has it.
                if (!named && _canList) effects.Add(Call("session/list", new { cwd = _cwd }, new Pending("title", Session: _session)));
                Next(record, now, effects);
                break;
            }
            case "title":
            {
                if (!ok || _given.Length > 0 || !Get(result, "sessions", out var known) || known.ValueKind != JsonValueKind.Array) break;
                foreach (var entry in known.EnumerateArray())
                {
                    if (Text(entry, "sessionId") != _session || Text(entry, "title") is not { Length: > 0 } theirs) continue;
                    _titles[_session] = theirs;
                    _titled = true;
                    Title(theirs, record, effects);
                    break;
                }
                break;
            }
            case "prompt":
            {
                _prompting = false;
                Flush(false, effects);
                CancelAsked(effects);
                string stop = ok ? Text(result, "stopReason") : "";
                // OpenCode also ends a turn as cancelled when a call was declined: that one was not interrupted.
                if (stop == "cancelled" && _stopping) effects.Add(Durable(new { type = "user", message = new { role = "user", content = "[Request interrupted by user]" }, parent_tool_use_id = (string?)null }));
                _stopping = false;
                bool failed = !ok || stop == "refusal";
                string said = !ok ? (why.Length > 0 ? why : $"{NameOf(_provider)} did not finish the turn") : stop == "refusal" ? "The model declined to answer." : stop == "max_tokens" ? "The reply was cut off at the model's length limit." : "";
                effects.Add(Durable(new { type = "result", subtype = failed ? "error" : stop == "cancelled" ? "error_during_execution" : "success", is_error = failed, result = said }));
                if (!failed && said.Length > 0) effects.Add(Durable(new { type = "host", subtype = "note", text = said }));
                record.LastEvent = "Stop";
                Status(record, Rest(now), now);
                Next(record, now, effects);
                break;
            }
            case "commands":
                // The agent says its commands after this answer, and the asker is told then. Nothing in the answer is
                // taken: the conversation and its settings are the ones already here.
                _resuming = false;
                if (!ok && _reload is { } refused && refused.Request == pending.Request)
                {
                    _reload = null;
                    effects.Add(new Effect(Kind.Reply, ChatSessions.NoCommandsFrame(pending.Request, $"{NameOf(_provider)} could not reload its skills" + (why.Length > 0 ? ": " + why : ".")), pending.Tag));
                }
                Next(record, now, effects);
                break;
            case "config":
                if (ok && Get(result, "configOptions", out var options))
                {
                    Configure(options, record);
                    Catalog(effects);
                    effects.Add(Frame(SettingsFrame()));
                }
                if (pending.ViewerId.Length > 0)
                    effects.Add(Live(ok
                        ? new { type = "control_response", response = (object)new { subtype = "success", request_id = pending.ViewerId, response = new { mode = Current("mode") } } }
                        : new { type = "control_response", response = (object)new { subtype = "error", request_id = pending.ViewerId, error = why.Length > 0 ? why : "The setting was not changed" } }));
                break;
            case "sessions":
            {
                var entries = pending.Listed ?? new List<ChatSessions.Entry>();
                if (ok && Get(result, "sessions", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    // Antigravity answers with every folder's conversations whatever folder was asked for, writes a folder
                    // as /C:/…, and gives the ones it has no folder for its own (measured, 1.5.8: 250 of 469). Where an
                    // agent writes any folder that way, only those so written are known to be where they say.
                    bool placed = list.EnumerateArray().Any(s => Placed(Text(s, "cwd")));
                    foreach (var s in list.EnumerateArray())
                    {
                        string sid = Text(s, "sessionId");
                        if (!IsSessionId(sid)) continue;
                        string where = Text(s, "cwd");
                        if (where.Length > 0 && (!SameFolder(where, _cwd) || placed && !Placed(where))) continue;
                        _titles[sid] = Text(s, "title");
                        if (sid == _session || entries.Any(e => e.Id == sid)) continue;
                        long at = DateTimeOffset.TryParse(Text(s, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.ToUnixTimeMilliseconds() : 0;
                        entries.Add(new ChatSessions.Entry(sid, _names.Of(sid) is { Length: > 0 } given ? given : ChatSessions.OneLine(Text(s, "title")), at, false));
                    }
                }
                // OpenCode answers a hundred at a time, and names a next page even after the last.
                if (ok && Text(result, "nextCursor") is { Length: > 0 } cursor && pending.Page + 1 < ListPages && Get(result, "sessions", out var page) && page.ValueKind == JsonValueKind.Array && page.GetArrayLength() > 0)
                {
                    effects.Add(Call("session/list", new { cwd = _cwd, cursor }, pending with { Listed = entries, Page = pending.Page + 1 }));
                    break;
                }
                effects.Add(new Effect(Kind.Reply, ChatSessions.ListFrame(pending.Request, _session, entries.OrderByDescending(e => e.At).ToList()), pending.Tag));
                break;
            }
        }
    }

    /// <summary>A session is open: what the agent offers, and what a restarted session is put back to.</summary>
    void Opened(JsonElement result, HostRecord record, List<Effect> effects)
    {
        if (Get(result, "configOptions", out var options)) Configure(options, record);
        Catalog(effects);
        effects.Add(Frame(SettingsFrame()));
        foreach (var (category, want) in new[] { ("model", _wantModel), ("thought_level", _wantEffort), ("mode", _wantMode) })
            if (want.Length > 0 && OptionOf(category) is { } option && option.Current != want && option.Choices.Any(c => c.Value == want))
                effects.Add(Call("session/set_config_option", new { sessionId = _session, configId = option.Id, value = want }, new Pending("config")));
        // Wanted once, at the start: what a viewer picks later is the session's from then on.
        _wantModel = _wantEffort = _wantMode = "";
    }

    void Configure(JsonElement options, HostRecord record)
    {
        if (options.ValueKind != JsonValueKind.Array) return;
        var parsed = new List<Option>();
        foreach (var o in options.EnumerateArray())
        {
            var choices = new List<Choice>();
            if (Get(o, "options", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var c in list.EnumerateArray())
                    if (Text(c, "value") is { Length: > 0 } value) choices.Add(new Choice(value, Text(c, "name") is { Length: > 0 } name ? name : value));
            if (Text(o, "id").Length > 0) parsed.Add(new Option(Text(o, "id"), Text(o, "category"), Text(o, "currentValue"), choices));
        }
        _options = parsed;
        record.Model = Current("model");
        record.Effort = Current("thought_level");
        record.PermissionMode = Current("mode");
    }

    void Catalog(List<Effect> effects)
    {
        var efforts = OptionOf("thought_level")?.Choices.Select(c => c.Value).ToList() ?? new List<string>();
        var models = OptionOf("model")?.Choices.Select(c => (object)new { value = c.Value, resolved = c.Value, name = c.Name, description = "", efforts }).ToList() ?? new List<object>();
        var modes = OptionOf("mode")?.Choices.Select(c => new[] { c.Value, c.Name, c.Name }).ToList() ?? new List<string[]>();
        using var commands = JsonDocument.Parse(_commands);
        _catalog = JsonSerializer.Serialize(new { type = "catalog", agent = NameOf(_provider), models, commands = commands.RootElement, modes, reload = CanReload });
        effects.Add(Frame(_catalog));
    }

    /// <summary>What was typed while there was no turn to start: the next one, now that there is.</summary>
    void Next(HostRecord record, DateTime now, List<Effect> effects)
    {
        if (_session.Length == 0 || _prompting || _loading != null || _resuming || _waiting.Count == 0) return;
        var prompt = _waiting[0];
        _waiting.RemoveAt(0);
        Send(prompt, record, now, effects);
    }

    void Title(string title, HostRecord record, List<Effect> effects)
    {
        if (title.Length == 0 || record.Title == title) return;
        record.Title = title;
        effects.Add(Frame(JsonSerializer.Serialize(new { type = "title", title })));
    }

    static string Short(string text)
    {
        string line = ChatSessions.OneLine(text);
        return line.Length > 60 ? line[..59].TrimEnd() + "…" : line;
    }

    static string FirstText(JsonElement prompt)
    {
        foreach (var block in prompt.EnumerateArray())
            if (Text(block, "type") == "text" && Text(block, "text") is { Length: > 0 } text) return text;
        return "";
    }

    /// <summary>What was first said in replayed lines, for a conversation the agent gave no name.</summary>
    static string FirstSaid(List<byte[]> lines)
    {
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            if (Text(doc.RootElement, "type") != "user" || !Get(doc.RootElement, "message", out var message) || !Get(message, "content", out var content)) continue;
            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
            if (content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                    if (Text(block, "type") == "text" && Text(block, "text") is { Length: > 0 } text) return text;
        }
        return "";
    }

    // ---- lines ----

    /// <summary>What was said to the agent, as a viewer reads it: the words, and a picture as a picture that is not kept.</summary>
    static byte[] UserLine(string id, JsonElement prompt, bool history)
    {
        var blocks = new List<object>();
        string text = "";
        foreach (var block in prompt.EnumerateArray())
        {
            if (Text(block, "type") == "text") text += (text.Length > 0 ? "\n" : "") + Text(block, "text");
            else if (Text(block, "type") == "image") blocks.Add(new { type = "image", source = new { type = "base64", media_type = Text(block, "mimeType"), data = "", omitted = Text(block, "data").Length } });
        }
        object content = text;
        if (blocks.Count > 0)
        {
            if (text.Length > 0) blocks.Insert(0, new { type = "text", text });
            content = blocks;
        }
        return Line(new Dictionary<string, object?> { ["type"] = "user", ["uuid"] = id, ["isReplay"] = true, ["message"] = new { role = "user", content }, ["parent_tool_use_id"] = null }, history);
    }

    static byte[] Assistant(string id, object block, bool history, string? parent = null) =>
        Line(new Dictionary<string, object?> { ["type"] = "assistant", ["uuid"] = id, ["message"] = new { role = "assistant", content = new[] { block } }, ["parent_tool_use_id"] = parent }, history);

    static byte[] Line(Dictionary<string, object?> line, bool history)
    {
        if (history) line["history"] = true;
        return JsonSerializer.SerializeToUtf8Bytes(line);
    }

    static void Status(HostRecord record, string status, DateTime now)
    {
        if (record.AgentStatus == status) return;
        record.AgentStatus = status;
        record.StatusAt = now;
    }

    /// <summary>A property of an object, and false for anything that is not one: what a viewer or the agent sends is not trusted to have its shape.</summary>
    static bool Get(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value);
    }

    static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
