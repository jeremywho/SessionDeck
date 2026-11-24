using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class AcpChatTests
{
    const string Cwd = @"C:\Temp\acplab\ws";
    const string AgyId = "2b0cd842-17f7-4199-9be1-89dfd0161b95", OcId = "ses_e2ae77411ffe3oE5bZaI08qWmI", OcOther = "ses_e2ae90a72ffeXZVQSrnBMnRXK3", OcThird = "ses_e2ad56e49ffedOuDrd140fy5xs";
    static readonly DateTime T0 = new(2026, 11, 13, 13, 0, 0, DateTimeKind.Utc);

    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static JsonElement Of(ChatEffect e) => J(Encoding.UTF8.GetString(e.Data));
    static string Type(JsonElement e) => e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
    static List<JsonElement> Sent(List<ChatEffect> effects) => effects.Where(e => e.Kind == ChatEffectKind.ToCli).Select(Of).ToList();
    static List<JsonElement> Kept(List<ChatEffect> effects) => effects.Where(e => e.Kind == ChatEffectKind.Durable).Select(Of).ToList();
    static List<JsonElement> Passed(List<ChatEffect> effects) => effects.Where(e => e.Kind == ChatEffectKind.Live).Select(Of).ToList();
    static List<JsonElement> Frames(List<ChatEffect> effects) => effects.Where(e => e.Kind == ChatEffectKind.Frame).Select(Of).ToList();
    static string Method(JsonElement m) => m.TryGetProperty("method", out var x) ? x.GetString() ?? "" : "";
    static long Id(JsonElement m) => m.GetProperty("id").GetInt64();
    static JsonElement Params(JsonElement m) => m.GetProperty("params");
    static string Str(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";
    static JsonElement Block(JsonElement line) => line.GetProperty("message").GetProperty("content")[0];
    static string Said(JsonElement line) =>
        line.GetProperty("message").GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()! : Str(Block(line), "text");

    static string Response(long id, string result) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}";
    static string Failure(long id, string message) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32603,\"message\":\"" + message + "\"}}";
    static string Update(string session, string update) => "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"" + session + "\",\"update\":" + update + "}}";
    static string UserSays(string text) => ChatStream.UserLine(text);
    static string Answer(string key, string body) => "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"" + key + "\",\"response\":" + body + "}}";
    static string Set(string id, string body) => "{\"type\":\"control_request\",\"request_id\":\"" + id + "\",\"request\":" + body + "}";
    static string Interrupt(string id) => Set(id, "{\"subtype\":\"interrupt\"}");
    static string Stopped(string reason) => "{\"stopReason\":\"" + reason + "\",\"usage\":{\"inputTokens\":8175,\"outputTokens\":25,\"totalTokens\":8200}}";
    static string Text(string kind, string text, string messageId = "") =>
        "{\"sessionUpdate\":\"" + kind + "\"" + (messageId.Length > 0 ? ",\"messageId\":\"" + messageId + "\"" : "") + ",\"content\":{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}}";

    const string AgyInit = """{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"load_session":true,"promptCapabilities":{"image":true,"audio":false,"embeddedContext":true},"sessionCapabilities":{"resume":{},"close":{},"load":{},"list":{},"additionalDirectories":{}}},"agentInfo":{"name":"Antigravity ACP Server","version":"1.5.8"}}""";
    const string OcInit = """{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"mcpCapabilities":{"http":true,"sse":false},"promptCapabilities":{"embeddedContext":true,"image":true},"sessionCapabilities":{"additionalDirectories":{},"close":{},"delete":{},"fork":{},"list":{},"resume":{}}},"agentInfo":{"name":"OpenCode","version":"2.2.6"}}""";
    const string BareInit = """{"protocolVersion":1,"agentCapabilities":{"promptCapabilities":{"embeddedContext":true,"image":true},"sessionCapabilities":{"additionalDirectories":{},"close":{}}},"agentInfo":{"name":"OpenCode","version":"2.2.6"}}""";

    const string AgyOptions = """[{"id":"model","name":"Model","category":"model","type":"select","currentValue":"gemini-4-carbon","options":[{"value":"gemini-4-carbon","name":"Gemini 4 Carbon"},{"value":"gemini-4-argon","name":"Gemini 4 Argon"}]},{"id":"thought_level","name":"Thinking Level","category":"thought_level","type":"select","currentValue":"medium","options":[{"value":"low","name":"Low"},{"value":"medium","name":"Medium"},{"value":"high","name":"High"}]},{"id":"mode","name":"Mode","category":"mode","type":"select","currentValue":"default","options":[{"value":"default","name":"Default","description":"Ask before edits and commands, per your permission settings."},{"value":"accept-edits","name":"Accept edits","description":"Approve file edits in the workspace without asking."},{"value":"plan","name":"Plan","description":"Plan carefully before executing a task."}]}]""";
    const string AgyNew = "{\"sessionId\":\"" + AgyId + "\",\"configOptions\":" + AgyOptions + "}";

    static string OcOptions(string model = "opencode/gemini-4-hydrogen", string effort = "default", string mode = "build") =>
        """[{"id":"model","name":"Model","category":"model","type":"select","currentValue":"@MODEL@","options":[{"value":"opencode/big-pickle","name":"opencode/Big Pickle"},{"value":"opencode/deepseek-v4.1-flash-free","name":"opencode/DeepSeek V4.1 Flash Free"},{"value":"opencode/gemini-4-hydrogen","name":"opencode/Gemini 4 Hydrogen"}]},{"id":"effort","name":"Effort","description":"Available effort levels for this model","category":"thought_level","type":"select","currentValue":"@EFFORT@","options":[{"value":"low","name":"Low"},{"value":"high","name":"High"},{"value":"max","name":"Max"},{"value":"default","name":"Default"}]},{"id":"mode","name":"Session Mode","category":"mode","type":"select","currentValue":"@MODE@","options":[{"value":"build","name":"Build","description":"The default agent. Executes tools based on configured permissions."},{"value":"plan","name":"Plan","description":"Read-only agent for exploring the codebase and planning work before implementation."}]}]"""
            .Replace("@MODEL@", model).Replace("@EFFORT@", effort).Replace("@MODE@", mode);
    static readonly string OcNew = "{\"sessionId\":\"" + OcId + "\",\"configOptions\":" + OcOptions() + "}";
    static string OcConfig(string model = "opencode/gemini-4-hydrogen", string effort = "default", string mode = "build") => "{\"configOptions\":" + OcOptions(model, effort, mode) + "}";

    static (AcpChat Chat, HostRecord Record, List<ChatEffect> Opened) OpenWith(string provider, string model = "", string effort = "", string mode = "", int budget = 2 * 1024 * 1024)
    {
        var chat = new AcpChat(provider, Cwd, "", model, effort, mode, budget);
        var record = new HostRecord();
        var hello = Sent(chat.Start()).Single();
        var create = Sent(chat.FromCli(J(Response(Id(hello), provider == "Agy" ? AgyInit : OcInit)), record, T0)).Single();
        Assert.Equal("session/new", Method(create));
        var opened = chat.FromCli(J(Response(Id(create), provider == "Agy" ? AgyNew : OcNew)), record, T0);
        return (chat, record, opened);
    }

    static string SessionOf(string provider) => provider == "Agy" ? AgyId : OcId;

    static JsonElement Prompt(AcpChat chat, HostRecord record, string text) =>
        Sent(chat.FromViewer(J(UserSays(text)), record, T0)).Single(m => Method(m) == "session/prompt");

    [Fact]
    public void It_offers_the_agent_nothing_and_opens_a_new_session_in_the_folder()
    {
        var chat = new AcpChat("Agy", Cwd, "", "", "", "");
        var record = new HostRecord();
        var hello = Sent(chat.Start()).Single();
        var p = Params(hello);
        Assert.Equal(("initialize", "2.0", 1), (Method(hello), Str(hello, "jsonrpc"), p.GetProperty("protocolVersion").GetInt32()));
        var can = p.GetProperty("clientCapabilities");
        Assert.Equal((false, false, false), (can.GetProperty("fs").GetProperty("readTextFile").GetBoolean(), can.GetProperty("fs").GetProperty("writeTextFile").GetBoolean(), can.GetProperty("terminal").GetBoolean()));

        var create = Sent(chat.FromCli(J(Response(Id(hello), AgyInit)), record, T0)).Single();
        Assert.Equal(("session/new", Cwd, 0), (Method(create), Str(Params(create), "cwd"), Params(create).GetProperty("mcpServers").GetArrayLength()));
        Assert.NotEqual(Id(hello), Id(create));

        var opened = chat.FromCli(J(Response(Id(create), AgyNew)), record, T0);
        Assert.Empty(Sent(opened));
        Assert.Equal((AgyId, "gemini-4-carbon", "medium", "default"), (record.SessionId, record.Model, record.Effort, record.PermissionMode));
        var frames = Frames(opened);
        Assert.Equal(new[] { "catalog", "settings" }, frames.Select(Type));
        var catalog = frames[0];
        Assert.Equal("Antigravity", Str(catalog, "agent"));
        Assert.Equal(new[] { ("gemini-4-carbon", "Gemini 4 Carbon", "low,medium,high"), ("gemini-4-argon", "Gemini 4 Argon", "low,medium,high") },
            catalog.GetProperty("models").EnumerateArray().Select(m => (Str(m, "value"), Str(m, "name"), string.Join(",", m.GetProperty("efforts").EnumerateArray().Select(e => e.GetString())))));
        Assert.Equal(new[] { "default|Default|Default", "accept-edits|Accept edits|Accept edits", "plan|Plan|Plan" },
            catalog.GetProperty("modes").EnumerateArray().Select(m => string.Join("|", m.EnumerateArray().Select(x => x.GetString()))));
        Assert.Equal(catalog.GetRawText(), chat.CatalogFrame());
        var settings = frames[1];
        Assert.Equal(("default", "gemini-4-carbon", "medium"), (Str(settings, "mode"), Str(settings, "model"), Str(settings, "effort")));
        Assert.Equal(settings.GetRawText(), chat.SettingsFrame());
    }

    [Fact]
    public void OpenCodes_effort_option_has_its_own_id_and_is_still_read_as_the_effort()
    {
        var (chat, record, opened) = OpenWith("OpenCode");
        Assert.Equal((OcId, "opencode/gemini-4-hydrogen", "default", "build"), (record.SessionId, record.Model, record.Effort, record.PermissionMode));
        var catalog = Frames(opened).Single(f => Type(f) == "catalog");
        Assert.Equal("OpenCode", Str(catalog, "agent"));
        Assert.Equal("low,high,max,default", string.Join(",", catalog.GetProperty("models")[0].GetProperty("efforts").EnumerateArray().Select(e => e.GetString())));
        Assert.Equal(new[] { "build", "plan" }, catalog.GetProperty("modes").EnumerateArray().Select(m => m[0].GetString()));
        var settings = J(chat.SettingsFrame());
        Assert.Equal(("build", "opencode/gemini-4-hydrogen", "default"), (Str(settings, "mode"), Str(settings, "model"), Str(settings, "effort")));
    }

    [Fact]
    public void A_session_started_on_a_conversation_reopens_it_and_the_replay_becomes_the_scrollback()
    {
        var chat = new AcpChat("OpenCode", Cwd, OcId, "", "", "");
        var record = new HostRecord();
        var load = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), OcInit)), record, T0)).Single();
        Assert.Equal(("session/load", OcId, Cwd), (Method(load), Str(Params(load), "sessionId"), Str(Params(load), "cwd")));
        Assert.Empty(chat.FromViewer(J(UserSays("what next")), record, T0));

        foreach (string update in new[]
        {
            Text("user_message_chunk", "Do these in order, one tool call each.", "msg_1d5188c07001fdiBvEW46G1J9u"),
            Text("agent_message_chunk", "I'll do these three steps in order.", "msg_1d5188c24001bKuq4rxATpy9ex"),
            """{"sessionUpdate":"tool_call","toolCallId":"call_b9ca73159a9d4bf9862d79cc","name":"shell","title":"echo acp-lab-one","kind":"execute","status":"pending","locations":[{"path":"C:\\Temp\\acplab\\ws"}],"rawInput":{"command":"echo acp-lab-one","cwd":"C:\\Temp\\acplab\\ws"}}""",
            OcShellDone,
            Text("agent_message_chunk", "done", "msg_1d518aac6001wQC1v4NzTuijhq"),
        })
            Assert.Empty(chat.FromCli(J(Update(OcId, update)), record, T0));

        var loaded = chat.FromCli(J(Response(Id(load), OcConfig())), record, T0);
        var reset = loaded.Single(e => e.Kind == ChatEffectKind.Reset);
        var seed = reset.Seed!;
        Assert.Equal((OcId, "Do these in order, one tool call each.", "", false), (seed.Session, seed.Title, seed.Path, seed.Earlier));
        var lines = seed.Lines.Select(l => J(Encoding.UTF8.GetString(l))).ToList();
        Assert.Equal(new[] { ("user", "msg_1d5188c07001fdiBvEW46G1J9u"), ("assistant", "msg_1d5188c24001bKuq4rxATpy9ex"), ("assistant", "call_b9ca73159a9d4bf9862d79cc"), ("user", "call_b9ca73159a9d4bf9862d79cc:r"), ("assistant", "msg_1d518aac6001wQC1v4NzTuijhq") },
            lines.Select(l => (Type(l), Str(l, "uuid"))));
        Assert.All(lines, l => Assert.True(l.GetProperty("history").GetBoolean()));
        Assert.Equal(("Do these in order, one tool call each.", "I'll do these three steps in order.", "done"), (Said(lines[0]), Said(lines[1]), Said(lines[4])));
        Assert.Equal(("tool_use", "Shell", "echo acp-lab-one"), (Str(Block(lines[2]), "type"), Str(Block(lines[2]), "name"), Str(Block(lines[2]).GetProperty("input"), "command")));
        Assert.Equal(("tool_result", "acp-lab-one\n", false), (Str(Block(lines[3]), "type"), Str(Block(lines[3]), "content"), Block(lines[3]).GetProperty("is_error").GetBoolean()));
        Assert.Equal((OcId, "Do these in order, one tool call each."), (record.SessionId, record.Title));

        // The message held during the load is sent once the replay has been quiet for a moment (more of it can follow
        // the agent's answer), and so lands after the scrollback is reset.
        Assert.DoesNotContain(Sent(loaded), m => Method(m) == "session/prompt");
        Assert.Empty(chat.Tick(record, T0.AddMilliseconds(500)));
        var released = chat.Tick(record, T0.AddSeconds(1));
        var prompt = Sent(released).Single(m => Method(m) == "session/prompt");
        Assert.Equal(("session/prompt", OcId, "what next"), (Method(prompt), Str(Params(prompt), "sessionId"), Str(Params(prompt).GetProperty("prompt")[0], "text")));
        Assert.Equal("what next", Said(Kept(released).Single(l => Type(l) == "user")));
    }

    [Fact]
    public void A_conversation_reopened_at_the_start_takes_the_name_the_agent_lists_it_under()
    {
        var chat = new AcpChat("OpenCode", Cwd, OcId, "", "", "");
        var record = new HostRecord();
        var load = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), OcInit)), record, T0)).Single();
        chat.FromCli(J(Update(OcId, Text("user_message_chunk", "Do these in order, one tool call each.", "msg_1d5188c07001fdiBvEW46G1J9u"))), record, T0);
        var loaded = chat.FromCli(J(Response(Id(load), OcConfig())), record, T0);
        Assert.Equal("Do these in order, one tool call each.", record.Title);
        var lookup = Sent(loaded).Single();
        Assert.Equal(("session/list", Cwd), (Method(lookup), Str(Params(lookup), "cwd")));
        Assert.True(loaded.FindIndex(e => e.Kind == ChatEffectKind.Reset) < loaded.FindIndex(e => e.Kind == ChatEffectKind.ToCli), "the name is asked for once the scrollback is the conversation's");

        var named = chat.FromCli(J(Response(Id(lookup), Page("eyJvcmRlciI6ImRlc2MifQ",
            Listed(OcOther, @"C:\Temp\acplab\ws", "Exact reply instruction", "2026-11-13T13:26:56.168Z"),
            Listed(OcId, @"C:\Temp\acplab\ws", "Create and update note.txt", "2026-11-13T13:28:47.366Z")))), record, T0);
        var frame = Of(named.Single());
        Assert.Equal((ChatEffectKind.Frame, "title", "Create and update note.txt"), (named.Single().Kind, Type(frame), Str(frame, "title")));
        Assert.Equal("Create and update note.txt", record.Title);

        var again = Sent(chat.Sessions("s1", "viewer-a")).Single();
        chat.FromCli(J(Response(Id(again), Page(null, Listed(OcOther, @"C:\Temp\acplab\ws", "Exact reply instruction", "2026-11-13T13:26:56.168Z")))), record, T0);
        var moved = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        var there = chat.FromCli(J(Response(Id(moved), OcConfig())), record, T0);
        Assert.Equal("Exact reply instruction", there.Single(e => e.Kind == ChatEffectKind.Reset).Seed!.Title);
        Assert.Empty(Sent(there));
    }

    [Fact]
    public void A_name_that_arrives_for_a_conversation_since_left_is_not_given_to_the_one_open_now()
    {
        var chat = new AcpChat("OpenCode", Cwd, OcId, "", "", "");
        var record = new HostRecord();
        var load = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), OcInit)), record, T0)).Single();
        var lookup = Sent(chat.FromCli(J(Response(Id(load), OcConfig())), record, T0)).Single();
        var moved = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        chat.FromCli(J(Response(Id(moved), OcConfig())), record, T0);
        string before = record.Title;

        var late = chat.FromCli(J(Response(Id(lookup), Page(null, Listed(OcId, @"C:\Temp\acplab\ws", "Create and update note.txt", "2026-11-13T13:28:47.366Z")))), record, T0);
        Assert.DoesNotContain(late, e => e.Kind == ChatEffectKind.Frame);
        Assert.Equal(before, record.Title);
    }

    [Fact]
    public void A_name_said_during_the_replay_is_the_reopened_conversations_and_is_not_asked_for()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        string before = record.Title;
        var moved = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        Assert.Empty(chat.FromCli(J(Update(OcOther, """{"sessionUpdate":"session_info_update","title":"Exact reply instruction"}""")), record, T0));
        Assert.Equal(before, record.Title);
        var there = chat.FromCli(J(Response(Id(moved), OcConfig())), record, T0);
        Assert.Equal("Exact reply instruction", there.Single(e => e.Kind == ChatEffectKind.Reset).Seed!.Title);
        Assert.Equal("Exact reply instruction", record.Title);
        Assert.Empty(Sent(there));
    }

    sealed class NoRoomForNames : IChatNames
    {
        public string Of(string session) => "";
        public bool Set(string session, string name) => false;
    }

    [Theory]
    [InlineData("Agy")]
    [InlineData("OpenCode")]
    public void A_rename_is_kept_here_and_nothing_is_said_to_the_agent(string provider)
    {
        var names = new MemoryChatNames();
        var chat = new AcpChat(provider, Cwd, "", "", "", "", names: names);
        var record = new HostRecord();
        var create = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), provider == "Agy" ? AgyInit : OcInit)), record, T0)).Single();
        chat.FromCli(J(Response(Id(create), provider == "Agy" ? AgyNew : OcNew)), record, T0);
        string session = SessionOf(provider);

        var renamed = chat.Rename("n1", "ship the thing", "viewer-a", record);
        Assert.Empty(Sent(renamed));
        Assert.Empty(Kept(renamed));
        Assert.Empty(Passed(renamed));
        var title = Frames(renamed).Single();
        Assert.Equal(("title", "ship the thing"), (Type(title), Str(title, "title")));
        var reply = renamed.Single(e => e.Kind == ChatEffectKind.Reply);
        Assert.Equal(("viewer-a", """{"type":"renamed","request":"n1","ok":true,"name":"ship the thing"}"""), (reply.Tag, Encoding.UTF8.GetString(reply.Data)));
        Assert.Equal(("ship the thing", "ship the thing"), (record.Title, names.Of(session)));

        // Neither the first message nor what the agent calls the conversation takes the title back.
        var prompt = Prompt(chat, record, "what is left to do");
        Assert.Equal("ship the thing", record.Title);
        Assert.DoesNotContain(chat.FromCli(J(Update(session, """{"sessionUpdate":"session_info_update","title":"Remaining Work Summary"}""")), record, T0), e => e.Kind == ChatEffectKind.Frame);
        Assert.Equal("ship the thing", record.Title);

        // In the middle of a turn it is carried out at once: it is no message, and waits for nothing.
        var again = chat.Rename("n2", "ship it today", "viewer-a", record);
        Assert.Empty(Sent(again));
        Assert.Equal(("ship it today", "ship it today"), (record.Title, names.Of(session)));
        chat.FromCli(J(Response(Id(prompt), Stopped("end_turn"))), record, T0);
        Assert.Equal("ship it today", record.Title);
    }

    [Fact]
    public void A_rename_with_no_conversation_or_nowhere_to_keep_it_is_refused_and_changes_nothing()
    {
        var early = new AcpChat("OpenCode", Cwd, "", "", "", "");
        var record = new HostRecord();
        early.FromCli(J(Response(Id(Sent(early.Start()).Single()), OcInit)), record, T0);
        var none = early.Rename("n1", "too soon", "viewer-a", record).Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a", """{"type":"renamed","request":"n1","ok":false,"error":"There is no conversation to name yet."}"""), (none.Kind, none.Tag, Encoding.UTF8.GetString(none.Data)));

        var full = new AcpChat("OpenCode", Cwd, "", "", "", "", names: new NoRoomForNames());
        var create = Sent(full.FromCli(J(Response(Id(Sent(full.Start()).Single()), OcInit)), record, T0)).Single();
        full.FromCli(J(Response(Id(create), OcNew)), record, T0);
        var unsaved = full.Rename("n2", "nowhere to go", "viewer-b", record).Single();
        Assert.Equal((ChatEffectKind.Reply, """{"type":"renamed","request":"n2","ok":false,"error":"The name could not be saved."}"""), (unsaved.Kind, Encoding.UTF8.GetString(unsaved.Data)));
        Assert.Equal("", record.Title);

        var (moving, moved, _) = OpenWith("OpenCode");
        Sent(moving.Resume("r1", OcOther, "viewer-a")).Single();
        var changing = moving.Rename("n3", "mid move", "viewer-a", moved).Single();
        Assert.Equal("""{"type":"renamed","request":"n3","ok":false,"error":"The session is already changing."}""", Encoding.UTF8.GetString(changing.Data));
    }

    [Fact]
    public void A_given_name_stays_with_its_conversation_through_a_restart_a_move_and_the_list()
    {
        var names = new MemoryChatNames();
        names.Set(OcId, "ship the thing");

        // A restart: the conversation is reopened by a new translator, which is told the name was given.
        var chat = new AcpChat("OpenCode", Cwd, OcId, "", "", "", names: names);
        var record = new HostRecord();
        var load = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), OcInit)), record, T0)).Single();
        chat.FromCli(J(Update(OcId, Text("user_message_chunk", "Do these in order, one tool call each.", "msg_1d5188c07001fdiBvEW46G1J9u"))), record, T0);
        var loaded = chat.FromCli(J(Response(Id(load), OcConfig())), record, T0);
        Assert.Equal("ship the thing", loaded.Single(e => e.Kind == ChatEffectKind.Reset).Seed!.Title);
        Assert.Equal("ship the thing", record.Title);
        Assert.Empty(Sent(loaded));

        // The list shows it under the name given, and another conversation under the agent's.
        var list = Sent(chat.Sessions("s1", "viewer-a")).Single();
        names.Set(OcThird, "the ask lab");
        var listed = Of(chat.FromCli(J(Response(Id(list), Page(null,
            Listed(OcOther, @"C:\Temp\acplab\ws", "Exact reply instruction", "2026-11-13T13:26:56.168Z"),
            Listed(OcThird, @"C:\Temp\acplab\ws", "Create ask.txt with alpha", "2026-11-13T13:48:00.000Z")))), record, T0).Single());
        Assert.Equal(new[] { (OcThird, "the ask lab"), (OcOther, "Exact reply instruction") }, listed.GetProperty("sessions").EnumerateArray().Select(e => (Str(e, "id"), Str(e, "title"))));

        // A move to a conversation never named takes the agent's name; moving back brings the given one.
        var there = chat.FromCli(J(Response(Id(Sent(chat.Resume("r1", OcOther, "viewer-a")).Single()), OcConfig())), record, T0);
        Assert.Equal("Exact reply instruction", there.Single(e => e.Kind == ChatEffectKind.Reset).Seed!.Title);
        var said = chat.FromCli(J(Update(OcOther, """{"sessionUpdate":"session_info_update","title":"Exact Reply"}""")), record, T0);
        Assert.Equal("Exact Reply", Str(Frames(said).Single(), "title"));
        var back = chat.FromCli(J(Response(Id(Sent(chat.Resume("r2", OcId, "viewer-a")).Single()), OcConfig())), record, T0);
        Assert.Equal("ship the thing", back.Single(e => e.Kind == ChatEffectKind.Reset).Seed!.Title);
    }

    [Fact]
    public void A_name_looked_up_in_the_agents_list_does_not_replace_one_given_meanwhile()
    {
        var chat = new AcpChat("OpenCode", Cwd, OcId, "", "", "");
        var record = new HostRecord();
        var load = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), OcInit)), record, T0)).Single();
        var lookup = Sent(chat.FromCli(J(Response(Id(load), OcConfig())), record, T0)).Single();
        chat.Rename("n1", "ship the thing", "viewer-a", record);
        var late = chat.FromCli(J(Response(Id(lookup), Page(null, Listed(OcId, @"C:\Temp\acplab\ws", "Create and update note.txt", "2026-11-13T13:28:47.366Z")))), record, T0);
        Assert.DoesNotContain(late, e => e.Kind == ChatEffectKind.Frame);
        Assert.Equal("ship the thing", record.Title);
    }

    [Fact]
    public void An_agent_that_cannot_list_is_not_asked_for_the_name()
    {
        const string LoadsOnly = """{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"promptCapabilities":{"embeddedContext":true,"image":true},"sessionCapabilities":{"additionalDirectories":{},"close":{}}},"agentInfo":{"name":"OpenCode","version":"2.2.6"}}""";
        var chat = new AcpChat("OpenCode", Cwd, OcId, "", "", "");
        var record = new HostRecord();
        var load = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), LoadsOnly)), record, T0)).Single();
        Assert.Equal("session/load", Method(load));
        Assert.Empty(Sent(chat.FromCli(J(Response(Id(load), OcConfig())), record, T0)));
    }

    [Fact]
    public void The_conversation_it_was_started_on_being_gone_starts_a_new_one_and_says_so()
    {
        var chat = new AcpChat("OpenCode", Cwd, OcOther, "", "", "");
        var record = new HostRecord();
        var load = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), OcInit)), record, T0)).Single();
        Assert.Equal("session/load", Method(load));
        var failed = chat.FromCli(J(Failure(Id(load), "Session not found")), record, T0);
        Assert.Equal("session/new", Method(Sent(failed).Single()));
        var note = Kept(failed).Single();
        Assert.Equal(("host", "stderr", "Could not resume that conversation: Session not found A new one was started."), (Type(note), Str(note, "subtype"), Str(note, "text")));
        Assert.DoesNotContain(failed, e => e.Kind == ChatEffectKind.Reset);
        chat.FromCli(J(Response(Id(Sent(failed).Single()), OcNew)), record, T0);
        Assert.Equal(OcId, record.SessionId);
    }

    [Fact]
    public void A_session_the_agent_would_not_open_is_asked_for_again_by_the_next_message_and_nothing_typed_is_lost()
    {
        var chat = new AcpChat("OpenCode", Cwd, "", "", "", "");
        var record = new HostRecord();
        var create = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), OcInit)), record, T0)).Single();
        Assert.Empty(chat.FromViewer(J(UserSays("typed early")), record, T0));
        var failed = chat.FromCli(J(Failure(Id(create), "Internal error: Internal service failure")), record, T0);
        Assert.Empty(Sent(failed));
        var note = Kept(failed).Single();
        Assert.Equal(("host", "stderr", "OpenCode could not start a conversation: Internal error: Internal service failure Send a message to try again."), (Type(note), Str(note, "subtype"), Str(note, "text")));

        var again = Sent(chat.FromViewer(J(UserSays("hello?")), record, T0)).Single();
        Assert.Equal(("session/new", Cwd), (Method(again), Str(Params(again), "cwd")));
        Assert.NotEqual(Id(create), Id(again));
        Assert.Empty(Sent(chat.FromViewer(J(UserSays("still there?")), record, T0)));

        var opened = chat.FromCli(J(Response(Id(again), OcNew)), record, T0);
        Assert.Equal(OcId, record.SessionId);
        var first = Sent(opened).Single();
        Assert.Equal(("session/prompt", "typed early"), (Method(first), Str(Params(first).GetProperty("prompt")[0], "text")));
        var second = Sent(chat.FromCli(J(Response(Id(first), Stopped("end_turn"))), record, T0)).Single();
        Assert.Equal("hello?", Str(Params(second).GetProperty("prompt")[0], "text"));
        var third = Sent(chat.FromCli(J(Response(Id(second), Stopped("end_turn"))), record, T0)).Single();
        Assert.Equal("still there?", Str(Params(third).GetProperty("prompt")[0], "text"));
    }

    [Fact]
    public void An_agent_that_cannot_load_gets_a_new_session_and_a_move_is_refused()
    {
        var chat = new AcpChat("OpenCode", Cwd, OcOther, "", "", "");
        var record = new HostRecord();
        var create = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), BareInit)), record, T0)).Single();
        Assert.Equal("session/new", Method(create));
        chat.FromCli(J(Response(Id(create), OcNew)), record, T0);

        var refused = chat.Resume("r1", OcThird, "viewer-a").Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a"), (refused.Kind, refused.Tag));
        Assert.Equal(("resumed", false, "OpenCode cannot reopen a conversation."), (Type(Of(refused)), Of(refused).GetProperty("ok").GetBoolean(), Str(Of(refused), "error")));
        var listed = chat.Sessions("s1", "viewer-a").Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a", 0), (listed.Kind, listed.Tag, Of(listed).GetProperty("sessions").GetArrayLength()));
    }

    [Fact]
    public void What_a_restarted_session_had_is_put_back_once_and_only_when_the_agent_offers_it()
    {
        var (chat, record, opened) = OpenWith("OpenCode", model: "opencode/deepseek-v4.1-flash-free", effort: "high", mode: "plan");
        var sets = Sent(opened);
        Assert.All(sets, s => Assert.Equal(("session/set_config_option", OcId), (Method(s), Str(Params(s), "sessionId"))));
        Assert.Equal(new[] { ("model", "opencode/deepseek-v4.1-flash-free"), ("effort", "high"), ("mode", "plan") }, sets.Select(s => (Str(Params(s), "configId"), Str(Params(s), "value"))));

        var taken = chat.FromCli(J(Response(Id(sets[0]), OcConfig(model: "opencode/deepseek-v4.1-flash-free"))), record, T0);
        Assert.Empty(Passed(taken));
        Assert.Equal(new[] { "catalog", "settings" }, Frames(taken).Select(Type));
        Assert.Equal("opencode/deepseek-v4.1-flash-free", record.Model);

        var load = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        var loaded = chat.FromCli(J(Response(Id(load), OcConfig())), record, T0);
        Assert.DoesNotContain(Sent(loaded), m => Method(m) == "session/set_config_option");

        var (_, _, agy) = OpenWith("Agy", model: "gemini-4-carbon", effort: "high", mode: "yolo");
        var only = Sent(agy).Single();
        Assert.Equal(("thought_level", "high"), (Str(Params(only), "configId"), Str(Params(only), "value")));
    }

    [Fact]
    public void A_restarted_session_reopened_by_load_is_put_back_after_the_load_answers()
    {
        var chat = new AcpChat("OpenCode", Cwd, OcId, "opencode/deepseek-v4.1-flash-free", "default", "plan");
        var record = new HostRecord();
        var load = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), OcInit)), record, T0)).Single();
        Assert.Equal("session/load", Method(load));
        Assert.Empty(chat.FromCli(J(Update(OcId, Text("agent_message_chunk", "done", "msg_1d518aac6001wQC1v4NzTuijhq"))), record, T0));
        var loaded = chat.FromCli(J(Response(Id(load), OcConfig())), record, T0);
        var sets = Sent(loaded).Where(m => Method(m) != "session/list").ToList();
        Assert.All(sets, s => Assert.Equal(("session/set_config_option", OcId), (Method(s), Str(Params(s), "sessionId"))));
        Assert.Equal(new[] { ("model", "opencode/deepseek-v4.1-flash-free"), ("mode", "plan") }, sets.Select(s => (Str(Params(s), "configId"), Str(Params(s), "value"))));
        Assert.Contains(loaded, e => e.Kind == ChatEffectKind.Reset);
    }

    [Fact]
    public void A_setting_a_viewer_picks_goes_to_the_agent_by_its_option_id_and_the_viewer_hears_how_it_went()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var model = chat.FromViewer(J(Set("sd-set-1", """{"subtype":"set_model","model":"opencode/big-pickle"}""")), record, T0);
        Assert.Empty(Passed(model));
        var asked = Sent(model).Single();
        Assert.Equal(("session/set_config_option", OcId, "model", "opencode/big-pickle"), (Method(asked), Str(Params(asked), "sessionId"), Str(Params(asked), "configId"), Str(Params(asked), "value")));
        var done = chat.FromCli(J(Response(Id(asked), OcConfig(model: "opencode/big-pickle"))), record, T0);
        var told = Passed(done).Single().GetProperty("response");
        Assert.Equal(("success", "sd-set-1", "build"), (Str(told, "subtype"), Str(told, "request_id"), Str(told.GetProperty("response"), "mode")));
        Assert.Equal("opencode/big-pickle", Str(Frames(done).Single(f => Type(f) == "settings"), "model"));
        Assert.Equal("opencode/big-pickle", record.Model);

        var effort = Sent(chat.FromViewer(J(Set("sd-set-2", """{"subtype":"apply_flag_settings","settings":{"effortLevel":"max"}}""")), record, T0)).Single();
        Assert.Equal(("effort", "max"), (Str(Params(effort), "configId"), Str(Params(effort), "value")));
        chat.FromCli(J(Response(Id(effort), OcConfig(model: "opencode/big-pickle", effort: "max"))), record, T0);
        Assert.Equal("max", record.Effort);

        var mode = Sent(chat.FromViewer(J(Set("sd-set-3", """{"subtype":"set_permission_mode","mode":"plan"}""")), record, T0)).Single();
        Assert.Equal(("mode", "plan"), (Str(Params(mode), "configId"), Str(Params(mode), "value")));
        var failed = chat.FromCli(J(Failure(Id(mode), "agent plan is not available")), record, T0);
        var error = Passed(failed).Single().GetProperty("response");
        Assert.Equal(("error", "sd-set-3", "agent plan is not available"), (Str(error, "subtype"), Str(error, "request_id"), Str(error, "error")));
        Assert.Empty(Frames(failed));
        Assert.Equal("build", record.PermissionMode);

        foreach (string body in new[] { """{"subtype":"set_permission_mode","mode":"bypassPermissions"}""", """{"subtype":"set_max_thinking_tokens"}""", """{"subtype":"set_model","model":""}""" })
        {
            var refused = chat.FromViewer(J(Set("sd-set-9", body)), record, T0);
            Assert.Empty(Sent(refused));
            var no = Passed(refused).Single().GetProperty("response");
            Assert.Equal(("error", "sd-set-9", "OpenCode has no such setting"), (Str(no, "subtype"), Str(no, "request_id"), Str(no, "error")));
        }

        var early = new AcpChat("OpenCode", Cwd, "", "", "", "");
        early.Start();
        var before = early.FromViewer(J(Set("sd-set-4", """{"subtype":"set_model","model":"opencode/big-pickle"}""")), new HostRecord(), T0);
        Assert.Empty(Sent(before));
        Assert.Equal("error", Str(Passed(before).Single().GetProperty("response"), "subtype"));
    }

    [Fact]
    public void A_message_is_shown_as_sent_and_starts_a_turn_and_the_agents_echo_of_it_is_not_a_second_line()
    {
        var (chat, record, _) = OpenWith("Agy");
        var sent = chat.FromViewer(J(UserSays("Reply with exactly: ok")), record, T0.AddSeconds(1));
        var user = Kept(sent).Single();
        Assert.Equal(("user", "Reply with exactly: ok", true), (Type(user), Said(user), user.GetProperty("isReplay").GetBoolean()));
        Assert.StartsWith("u-", Str(user, "uuid"));
        var init = Passed(sent).Single();
        Assert.Equal(("system", "init", AgyId, "gemini-4-carbon", "default"), (Type(init), Str(init, "subtype"), Str(init, "session_id"), Str(init, "model"), Str(init, "permissionMode")));
        var prompt = Sent(sent).Single();
        Assert.Equal(("session/prompt", AgyId), (Method(prompt), Str(Params(prompt), "sessionId")));
        Assert.Equal(new[] { ("text", "Reply with exactly: ok") }, Params(prompt).GetProperty("prompt").EnumerateArray().Select(b => (Str(b, "type"), Str(b, "text"))));
        Assert.Equal(("busy", "UserPromptSubmit", T0.AddSeconds(1)), (record.AgentStatus, record.LastEvent, record.StatusAt));
        Assert.Equal(("title", "Reply with exactly: ok"), (Type(Frames(sent).Single()), Str(Frames(sent).Single(), "title")));

        Assert.Empty(chat.FromCli(J(Update(AgyId, Text("user_message_chunk", "Reply with exactly: ok"))), record, T0));
        var named = chat.FromCli(J(Update(AgyId, """{"sessionUpdate":"session_info_update","title":"Ok Reply Test"}""")), record, T0);
        Assert.Equal("Ok Reply Test", Str(Frames(named).Single(), "title"));
        Assert.Equal("Ok Reply Test", record.Title);

        var words = chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "ok"))), record, T0);
        Assert.Empty(Kept(words));
        Assert.Equal(new[] { "content_block_start", "content_block_delta" }, Passed(words).Select(l => Str(l.GetProperty("event"), "type")));
        Assert.Equal(("text", "text_delta", "ok"), (Str(Passed(words)[0].GetProperty("event").GetProperty("content_block"), "type"), Str(Passed(words)[1].GetProperty("event").GetProperty("delta"), "type"), Str(Passed(words)[1].GetProperty("event").GetProperty("delta"), "text")));

        var end = chat.FromCli(J(Response(Id(prompt), Stopped("end_turn"))), record, T0.AddSeconds(5));
        Assert.Equal(new[] { "assistant", "result" }, Kept(end).Select(Type));
        Assert.Equal("ok", Said(Kept(end)[0]));
        Assert.Equal(("success", false, ""), (Str(Kept(end)[1], "subtype"), Kept(end)[1].GetProperty("is_error").GetBoolean(), Str(Kept(end)[1], "result")));
        Assert.Equal(("idle", "Stop", T0.AddSeconds(5)), (record.AgentStatus, record.LastEvent, record.StatusAt));
    }

    [Fact]
    public void Messages_typed_before_the_session_opens_or_during_a_turn_wait_and_go_in_order()
    {
        var chat = new AcpChat("OpenCode", Cwd, "", "", "", "");
        var record = new HostRecord();
        var hello = Sent(chat.Start()).Single();
        Assert.Empty(chat.FromViewer(J(UserSays("early")), record, T0));
        var create = Sent(chat.FromCli(J(Response(Id(hello), OcInit)), record, T0)).Single();
        var opened = chat.FromCli(J(Response(Id(create), OcNew)), record, T0);
        var first = Sent(opened).Single();
        Assert.Equal(("session/prompt", "early"), (Method(first), Str(Params(first).GetProperty("prompt")[0], "text")));
        Assert.Equal("early", Said(Kept(opened).Single()));

        Assert.Empty(chat.FromViewer(J(UserSays("second")), record, T0));
        Assert.Empty(chat.FromViewer(J(UserSays("third")), record, T0));
        var ended = chat.FromCli(J(Response(Id(first), Stopped("end_turn"))), record, T0);
        Assert.Equal(new[] { "result", "user" }, Kept(ended).Select(Type));
        var next = Sent(ended).Single();
        Assert.Equal("second", Str(Params(next).GetProperty("prompt")[0], "text"));
        Assert.Equal("busy", record.AgentStatus);
        var last = Sent(chat.FromCli(J(Response(Id(next), Stopped("end_turn"))), record, T0)).Single();
        Assert.Equal("third", Str(Params(last).GetProperty("prompt")[0], "text"));
        Assert.Empty(Sent(chat.FromCli(J(Response(Id(last), Stopped("end_turn"))), record, T0)));
        Assert.Equal("idle", record.AgentStatus);
    }

    [Fact]
    public void A_picture_goes_to_the_agent_with_its_bytes_and_is_shown_without_them()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var line = J("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"what is this"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"QUJD"}}]}}""");
        var sent = chat.FromViewer(line, record, T0);
        var blocks = Params(Sent(sent).Single()).GetProperty("prompt");
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal(("text", "what is this"), (Str(blocks[0], "type"), Str(blocks[0], "text")));
        Assert.Equal(("image", "image/png", "QUJD"), (Str(blocks[1], "type"), Str(blocks[1], "mimeType"), Str(blocks[1], "data")));
        var shown = Kept(sent).Single().GetProperty("message").GetProperty("content");
        Assert.Equal(("text", "what is this"), (Str(shown[0], "type"), Str(shown[0], "text")));
        var picture = shown[1].GetProperty("source");
        Assert.Equal(("image", "image/png", "", 4), (Str(shown[1], "type"), Str(picture, "media_type"), Str(picture, "data"), picture.GetProperty("omitted").GetInt32()));

        chat.FromCli(J(Response(Id(Sent(sent).Single()), Stopped("end_turn"))), record, T0);
        var onlyPicture = J("""{"type":"user","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"/9j/"}}]}}""");
        var image = Params(Sent(chat.FromViewer(onlyPicture, record, T0)).Single()).GetProperty("prompt").EnumerateArray().Single();
        Assert.Equal(("image", "image/jpeg"), (Str(image, "type"), Str(image, "mimeType")));
        Assert.Empty(chat.FromViewer(J(UserSays("")), record, T0));
    }

    [Fact]
    public void Chunks_of_one_message_land_as_one_line_when_another_kind_or_message_follows_and_at_the_end_of_the_turn()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var turn = Prompt(chat, record, "Count from 1 to 400, one number per line, nothing else.");
        const string Thought = "msg_1d51989af001lMH5gFDtAlRZZH:reasoning:0", Reply = "msg_1d51989af001lMH5gFDtAlRZZH", Next = "msg_1d5199eeb001FD0QopL6Xm58tU";

        var first = chat.FromCli(J(Update(OcId, Text("agent_thought_chunk", "The user wants 1 to 400, one number per line. Just output it", Thought))), record, T0);
        Assert.Empty(Kept(first));
        Assert.Equal(new[] { "content_block_start", "content_block_delta" }, Passed(first).Select(l => Str(l.GetProperty("event"), "type")));
        Assert.Equal("thinking", Str(Passed(first)[0].GetProperty("event").GetProperty("content_block"), "type"));
        Assert.Equal(("thinking_delta", "The user wants 1 to 400, one number per line. Just output it"), (Str(Passed(first)[1].GetProperty("event").GetProperty("delta"), "type"), Str(Passed(first)[1].GetProperty("event").GetProperty("delta"), "thinking")));
        var more = chat.FromCli(J(Update(OcId, Text("agent_thought_chunk", ". That's a lot of tokens but fine.\n\n", Thought))), record, T0);
        Assert.Equal(new[] { "content_block_delta" }, Passed(more).Select(l => Str(l.GetProperty("event"), "type")));
        Assert.Empty(Kept(more));

        var said = chat.FromCli(J(Update(OcId, Text("agent_message_chunk", "1\n2\n3\n", Reply))), record, T0);
        var thinking = Kept(said).Single();
        Assert.Equal(("assistant", Thought + ":t", "thinking"), (Type(thinking), Str(thinking, "uuid"), Str(Block(thinking), "type")));
        Assert.Equal("The user wants 1 to 400, one number per line. Just output it. That's a lot of tokens but fine.", Str(Block(thinking), "thinking"));
        Assert.Equal("text", Str(Passed(said)[0].GetProperty("event").GetProperty("content_block"), "type"));
        Assert.Empty(Kept(chat.FromCli(J(Update(OcId, Text("agent_message_chunk", "4", Reply))), record, T0)));

        var another = Kept(chat.FromCli(J(Update(OcId, Text("agent_message_chunk", "note.txt", Next))), record, T0)).Single();
        Assert.Equal((Reply, "1\n2\n3\n4"), (Str(another, "uuid"), Said(another)));
        var end = Kept(chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, T0));
        Assert.Equal(new[] { "assistant", "result" }, end.Select(Type));
        Assert.Equal((Next, "note.txt"), (Str(end[0], "uuid"), Said(end[0])));

        var (agy, agyRecord, _) = OpenWith("Agy");
        var agyTurn = Prompt(agy, agyRecord, "Count");
        Assert.Empty(Kept(agy.FromCli(J(Update(AgyId, Text("agent_message_chunk", "1\n2\n"))), agyRecord, T0)));
        Assert.Empty(Kept(agy.FromCli(J(Update(AgyId, Text("agent_message_chunk", "3"))), agyRecord, T0)));
        var whole = Kept(agy.FromCli(J(Response(Id(agyTurn), Stopped("end_turn"))), agyRecord, T0));
        Assert.Equal(new[] { "assistant", "result" }, whole.Select(Type));
        Assert.Equal("1\n2\n3", Said(whole[0]));
    }

    const string OcShellPending = """{"sessionUpdate":"tool_call","toolCallId":"call_b9ca73159a9d4bf9862d79cc","name":"shell","title":"shell","kind":"execute","status":"pending","locations":[{"path":"C:/Temp/acplab/ws"}],"rawInput":{"cwd":"C:/Temp/acplab/ws"}}""";
    const string OcShellRunning = """{"sessionUpdate":"tool_call_update","toolCallId":"call_b9ca73159a9d4bf9862d79cc","status":"in_progress","kind":"execute","title":"echo acp-lab-one","locations":[{"path":"C:/Temp/acplab/ws"}],"rawInput":{"command":"echo acp-lab-one","cwd":"C:/Temp/acplab/ws"}}""";
    const string OcShellDone = """{"sessionUpdate":"tool_call_update","toolCallId":"call_b9ca73159a9d4bf9862d79cc","status":"completed","locations":[{"path":"C:/Temp/acplab/ws"}],"content":[{"type":"content","content":{"type":"text","text":"acp-lab-one\n"}}],"rawOutput":{"metadata":{"status":"completed","truncated":false,"exit":0}}}""";
    const string OcWritePending = """{"sessionUpdate":"tool_call","toolCallId":"call_05116a2429364bc4a072b87a","name":"write","title":"write","kind":"edit","status":"pending","locations":[],"rawInput":{}}""";
    const string OcWriteRunning = """{"sessionUpdate":"tool_call_update","toolCallId":"call_05116a2429364bc4a072b87a","status":"in_progress","kind":"edit","title":"write","locations":[{"path":"C:\\Temp\\acplab\\ws\\note.txt"}],"rawInput":{"path":"C:\\Temp\\acplab\\ws\\note.txt","content":"alpha\n"}}""";
    const string OcWriteDone = """{"sessionUpdate":"tool_call_update","toolCallId":"call_05116a2429364bc4a072b87a","status":"completed","locations":[{"path":"C:\\Temp\\acplab\\ws\\note.txt"}],"content":[{"type":"content","content":{"type":"text","text":"Created file successfully: note.txt"}}],"rawOutput":{"metadata":{"truncated":false}}}""";
    const string OcEditPending = """{"sessionUpdate":"tool_call","toolCallId":"call_300be7e6465443679e13cb7f","name":"edit","title":"edit","kind":"edit","status":"pending","locations":[],"rawInput":{}}""";
    const string OcEditRunning = """{"sessionUpdate":"tool_call_update","toolCallId":"call_300be7e6465443679e13cb7f","status":"in_progress","kind":"edit","title":"edit","locations":[{"path":"C:\\Temp\\acplab\\ws\\note.txt"}],"rawInput":{"path":"C:\\Temp\\acplab\\ws\\note.txt","oldString":"alpha","newString":"beta"}}""";
    const string OcEditDone = """{"sessionUpdate":"tool_call_update","toolCallId":"call_300be7e6465443679e13cb7f","status":"completed","locations":[{"path":"C:\\Temp\\acplab\\ws\\note.txt"}],"content":[{"type":"content","content":{"type":"text","text":"Edited note.txt (1 replacement)"}},{"type":"diff","path":"C:\\Temp\\acplab\\ws\\note.txt","oldText":"alpha\n","newText":"beta\n"}],"rawOutput":{"metadata":{"truncated":false}}}""";

    [Fact]
    public void OpenCodes_calls_show_once_they_run_and_each_result_lands_once()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        Prompt(chat, record, "Do these in order, one tool call each.");
        chat.FromCli(J(Update(OcId, Text("agent_message_chunk", "I'll do these three steps in order.", "msg_1d5188c24001bKuq4rxATpy9ex"))), record, T0);

        var announced = chat.FromCli(J(Update(OcId, OcShellPending)), record, T0);
        Assert.Equal(new[] { "assistant" }, Kept(announced).Select(Type));
        Assert.Equal("text", Str(Block(Kept(announced)[0]), "type"));
        var running = Kept(chat.FromCli(J(Update(OcId, OcShellRunning)), record, T0)).Single();
        Assert.Equal(("tool_use", "call_b9ca73159a9d4bf9862d79cc", "Shell", "echo acp-lab-one"), (Str(Block(running), "type"), Str(Block(running), "id"), Str(Block(running), "name"), Str(Block(running).GetProperty("input"), "command")));
        Assert.Equal("command", Assert.Single(Block(running).GetProperty("input").EnumerateObject()).Name);
        Assert.Equal("Shell", record.LastTool);
        Assert.Empty(chat.FromCli(J(Update(OcId, OcShellRunning)), record, T0));
        var result = Kept(chat.FromCli(J(Update(OcId, OcShellDone)), record, T0)).Single();
        Assert.Equal(("user", "call_b9ca73159a9d4bf9862d79cc:r"), (Type(result), Str(result, "uuid")));
        Assert.Equal(("tool_result", "call_b9ca73159a9d4bf9862d79cc", "acp-lab-one\n", false), (Str(Block(result), "type"), Str(Block(result), "tool_use_id"), Str(Block(result), "content"), Block(result).GetProperty("is_error").GetBoolean()));
        Assert.Empty(chat.FromCli(J(Update(OcId, OcShellDone)), record, T0));

        Assert.Empty(chat.FromCli(J(Update(OcId, OcWritePending)), record, T0));
        var write = Block(Kept(chat.FromCli(J(Update(OcId, OcWriteRunning)), record, T0)).Single());
        Assert.Equal(("Edit", @"C:\Temp\acplab\ws\note.txt", "alpha\n"), (Str(write, "name"), Str(write.GetProperty("input"), "file_path"), Str(write.GetProperty("input"), "content")));
        Assert.Equal("Created file successfully: note.txt", Str(Block(Kept(chat.FromCli(J(Update(OcId, OcWriteDone)), record, T0)).Single()), "content"));

        Assert.Empty(chat.FromCli(J(Update(OcId, OcEditPending)), record, T0));
        var edit = Block(Kept(chat.FromCli(J(Update(OcId, OcEditRunning)), record, T0)).Single()).GetProperty("input");
        Assert.Equal((@"C:\Temp\acplab\ws\note.txt", "alpha", "beta"), (Str(edit, "file_path"), Str(edit, "old_string"), Str(edit, "new_string")));
        var edited = Block(Kept(chat.FromCli(J(Update(OcId, OcEditDone)), record, T0)).Single());
        Assert.Equal(("Edited note.txt (1 replacement)", false), (Str(edited, "content"), edited.GetProperty("is_error").GetBoolean()));
    }

    [Fact]
    public void A_call_that_failed_or_exited_non_zero_is_an_error()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        Prompt(chat, record, "Run the shell command: echo ask-two. Then reply with exactly: ran");
        chat.FromCli(J(Update(OcId, """{"sessionUpdate":"tool_call_update","toolCallId":"call_a6d68bfc353142359b6fbb5f","status":"in_progress","kind":"execute","title":"echo ask-two","locations":[{"path":"C:/Temp/acplab/ws-ask"}],"rawInput":{"command":"echo ask-two","cwd":"C:/Temp/acplab/ws-ask"}}""")), record, T0);
        var declined = Block(Kept(chat.FromCli(J(Update(OcId, OcDeclined)), record, T0)).Single());
        Assert.Equal(("The user declined this tool call", true), (Str(declined, "content"), declined.GetProperty("is_error").GetBoolean()));

        chat.FromCli(J(Update(OcId, OcShellRunning)), record, T0);
        var exited = Block(Kept(chat.FromCli(J(Update(OcId, OcShellDone.Replace("\"exit\":0", "\"exit\":1"))), record, T0)).Single());
        Assert.True(exited.GetProperty("is_error").GetBoolean());

        var (agy, agyRecord, _) = OpenWith("Agy");
        Prompt(agy, agyRecord, "Run it");
        agy.FromCli(J(Update(AgyId, AgyRun)), agyRecord, T0);
        var agyExit = Block(Kept(agy.FromCli(J(Update(AgyId, AgyRunDone.Replace("\"exit_code\":0", "\"exit_code\":2"))), agyRecord, T0)).Single());
        Assert.True(agyExit.GetProperty("is_error").GetBoolean());
    }

    const string OcDeclined = """{"sessionUpdate":"tool_call_update","toolCallId":"call_a6d68bfc353142359b6fbb5f","status":"failed","kind":"execute","title":"echo ask-two","locations":[{"path":"C:/Temp/acplab/ws-ask"}],"rawInput":{"command":"echo ask-two","cwd":"C:/Temp/acplab/ws-ask"},"content":[{"type":"content","content":{"type":"text","text":"The user declined this tool call"}}],"rawOutput":{"metadata":{},"error":"The user declined this tool call"}}""";

    const string AgyRun = """{"sessionUpdate":"tool_call","toolCallId":"call_1797129992166531750","name":"run_command","args":{"CommandLine":"echo acp-lab-one","WaitMsBeforeAsync":5000,"toolSummary":"Echo acp-lab-one"},"status":"in_progress","kind":"execute","title":"echo acp-lab-one","rawInput":{"CommandLine":"echo acp-lab-one","WaitMsBeforeAsync":5000,"toolSummary":"Echo acp-lab-one"}}""";
    const string AgyRunDone = """{"sessionUpdate":"tool_call_update","toolCallId":"call_1797129992166531750","name":"run_command","args":{"CommandLine":"echo acp-lab-one","WaitMsBeforeAsync":5000,"toolSummary":"Echo acp-lab-one"},"status":"completed","kind":"execute","title":"echo acp-lab-one","rawInput":{"CommandLine":"echo acp-lab-one","WaitMsBeforeAsync":5000,"toolSummary":"Echo acp-lab-one"},"rawOutput":{"output":"Created At: 2026-11-13T07:27:48-06:00\nCompleted At: 2026-11-13T07:27:48-06:00\nCommand exited with code 0.\n\nCommand output:\nacp-lab-one\r\n","exit_code":0},"content":[{"type":"content","content":{"type":"text","text":"Created At: 2026-11-13T07:27:48-06:00\nCompleted At: 2026-11-13T07:27:48-06:00\nCommand exited with code 0.\n\nCommand output:\nacp-lab-one\r\n"}}]}""";
    const string AgyWrite = """{"sessionUpdate":"tool_call","toolCallId":"call_8020681131156347865","name":"write_file","args":{"Content":"alpha\n","Overwrite":true,"Path":"C:\\Temp\\acplab\\ws\\note.txt"},"status":"in_progress","kind":"edit","title":"File note.txt","locations":[{"path":"C:\\Temp\\acplab\\ws\\note.txt"}],"rawInput":{"Content":"alpha\n","Overwrite":true,"Path":"C:\\Temp\\acplab\\ws\\note.txt"}}""";
    const string AgyEdit = """{"sessionUpdate":"tool_call","toolCallId":"call_13553632996387484225","name":"edit_file","args":{"Path":"C:\\Temp\\acplab\\ws\\note.txt","Replacement":"beta","Target":"alpha"},"status":"in_progress","kind":"edit","title":"File note.txt","locations":[{"path":"C:\\Temp\\acplab\\ws\\note.txt"}],"rawInput":{"Path":"C:\\Temp\\acplab\\ws\\note.txt","Replacement":"beta","Target":"alpha"}}""";

    [Theory]
    [InlineData("\"toolSummary\":\"\"")]
    [InlineData("\"toolSummary\":\"   \"")]
    [InlineData("\"other\":1")]
    public void A_command_with_nothing_said_about_it_is_shown_by_the_command_alone(string summary)
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "Run it.");
        var run = Block(Kept(chat.FromCli(J(Update(AgyId, AgyRun.Replace("\"toolSummary\":\"Echo acp-lab-one\"", summary))), record, T0)).Single());
        Assert.Equal("echo acp-lab-one", Str(run.GetProperty("input"), "command"));
        Assert.False(run.GetProperty("input").TryGetProperty("summary", out _));
    }

    [Fact]
    public void Antigravitys_calls_are_read_by_meaning_from_its_own_input_names()
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "Do these in order, one tool call each.");
        var run = Block(Kept(chat.FromCli(J(Update(AgyId, AgyRun)), record, T0)).Single());
        Assert.Equal(("Shell", "echo acp-lab-one"), (Str(run, "name"), Str(run.GetProperty("input"), "command")));
        // What the command is for, in Antigravity's own words: the pane shows that on the row, and the command under it.
        Assert.Equal("Echo acp-lab-one", Str(run.GetProperty("input"), "summary"));
        Assert.Equal("tool_result", Str(Block(Assert.Single(Kept(chat.FromCli(J(Update(AgyId, AgyRunDone)), record, T0)))), "type"));
        var write = Block(Kept(chat.FromCli(J(Update(AgyId, AgyWrite)), record, T0)).Single());
        Assert.Equal(("Edit", @"C:\Temp\acplab\ws\note.txt", "alpha\n"), (Str(write, "name"), Str(write.GetProperty("input"), "file_path"), Str(write.GetProperty("input"), "content")));
        var edit = Block(Kept(chat.FromCli(J(Update(AgyId, AgyEdit)), record, T0)).Single()).GetProperty("input");
        Assert.Equal((@"C:\Temp\acplab\ws\note.txt", "alpha", "beta"), (Str(edit, "file_path"), Str(edit, "old_string"), Str(edit, "new_string")));
    }

    [Fact]
    public void Antigravitys_command_output_is_shown_without_the_header_it_wraps_it_in()
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "Run the shell command: echo acp-lab-one");
        chat.FromCli(J(Update(AgyId, AgyRun)), record, T0);
        var result = Block(Kept(chat.FromCli(J(Update(AgyId, AgyRunDone)), record, T0)).Single());
        Assert.Equal(("acp-lab-one\r\n", false), (Str(result, "content"), result.GetProperty("is_error").GetBoolean()));
    }

    const string OcAskWrite = """{"jsonrpc":"2.0","id":1,"method":"session/request_permission","params":{"sessionId":"@SID@","toolCall":{"toolCallId":"call_776d054d20db47908f36315f","name":"write","title":"C:\\Temp\\acplab\\ws-ask\\ask.txt","kind":"edit","status":"pending","locations":[{"path":"C:\\Temp\\acplab\\ws-ask\\ask.txt"}],"rawInput":{"path":"C:\\Temp\\acplab\\ws-ask\\ask.txt","content":"alpha\n"},"content":[{"type":"diff","path":"C:\\Temp\\acplab\\ws-ask\\ask.txt","oldText":null,"newText":"alpha\n"}]},"options":[{"optionId":"once","kind":"allow_once","name":"Allow once"},{"optionId":"always","kind":"allow_always","name":"Always allow"},{"optionId":"reject","kind":"reject_once","name":"Reject"}]}}""";

    [Fact]
    public void An_edit_known_only_by_its_diff_is_read_from_the_diff_and_a_new_file_has_no_old_text()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        Prompt(chat, record, "Create ask.txt containing alpha");
        var recorded = Kept(chat.FromCli(J(OcAskWrite.Replace("@SID@", OcId)), record, T0));
        var shown = Block(recorded.First(l => Type(l) == "assistant"));
        Assert.Equal(("Edit", @"C:\Temp\acplab\ws-ask\ask.txt", "alpha\n"), (Str(shown, "name"), Str(shown.GetProperty("input"), "file_path"), Str(shown.GetProperty("input"), "content")));

        var (diffOnly, diffRecord, _) = OpenWith("OpenCode");
        Prompt(diffOnly, diffRecord, "Create ask.txt containing alpha");
        string withoutInput = OcAskWrite.Replace("@SID@", OcId).Replace("\"rawInput\":{\"path\":\"C:\\\\Temp\\\\acplab\\\\ws-ask\\\\ask.txt\",\"content\":\"alpha\\n\"},", "");
        Assert.DoesNotContain("rawInput", withoutInput);
        var asked = Kept(diffOnly.FromCli(J(withoutInput), diffRecord, T0));
        var input = Block(asked.First(l => Type(l) == "assistant")).GetProperty("input");
        Assert.Equal((@"C:\Temp\acplab\ws-ask\ask.txt", "", "alpha\n"), (Str(input, "file_path"), Str(input, "old_string"), Str(input, "new_string")));
        Assert.Equal("Edit", Str(asked.Single(l => Type(l) == "control_request").GetProperty("request"), "tool_name"));
    }

    [Theory]
    [InlineData("read", "read", "read", "Read")]
    [InlineData("search", "grep", "grep", "Search")]
    [InlineData("fetch", "webfetch", "webfetch", "Fetch")]
    [InlineData("think", "", "", "Think")]
    [InlineData("delete", "", "", "Delete")]
    [InlineData("move", "", "", "Move")]
    [InlineData("other", "glob", "glob", "Glob")]
    [InlineData("other", "", "Find the notes", "Find the notes")]
    [InlineData("", "", "", "Tool")]
    public void A_call_is_named_by_its_kind_else_by_the_agents_name_for_it(string kind, string name, string title, string shown)
    {
        var (chat, record, _) = OpenWith("OpenCode");
        Prompt(chat, record, "go");
        string call = JsonSerializer.Serialize(new { sessionUpdate = "tool_call", toolCallId = "call_kind", name, title, kind, status = "in_progress", rawInput = new { } });
        Assert.Equal(shown, Str(Block(Kept(chat.FromCli(J(Update(OcId, call)), record, T0)).Single()), "name"));
    }

    const string AgyAsk = """{"jsonrpc":"2.0","method":"session/request_permission","params":{"sessionId":"@SID@","toolCall":{"sessionUpdate":"tool_call","toolCallId":"call_1797129992166531750","name":"run_command","args":{"CommandLine":"echo acp-lab-one","WaitMsBeforeAsync":5000,"toolSummary":"Echo acp-lab-one"},"status":"pending","kind":"execute","title":"echo acp-lab-one","rawInput":{"CommandLine":"echo acp-lab-one","WaitMsBeforeAsync":5000,"toolSummary":"Echo acp-lab-one"}},"options":[{"optionId":"allow","name":"Allow","kind":"allow_once"},{"optionId":"deny","name":"Deny","kind":"reject_once"}]},"id":"@ID@"}""";
    const string OcAsk = """{"jsonrpc":"2.0","id":@ID@,"method":"session/request_permission","params":{"sessionId":"@SID@","toolCall":{"toolCallId":"@CALL@","name":"shell","title":"@CMD@","kind":"execute","status":"pending","locations":[{"path":"C:/Temp/acplab/ws-ask"}],"rawInput":{"command":"@CMD@","cwd":"C:/Temp/acplab/ws-ask"}},"options":[{"optionId":"once","kind":"allow_once","name":"Allow once"},{"optionId":"always","kind":"allow_always","name":"Always allow"},{"optionId":"reject","kind":"reject_once","name":"Reject"}]}}""";
    const string OcRunning = """{"sessionUpdate":"tool_call_update","toolCallId":"@CALL@","status":"in_progress","kind":"execute","title":"@CMD@","locations":[{"path":"C:/Temp/acplab/ws-ask"}],"rawInput":{"command":"@CMD@","cwd":"C:/Temp/acplab/ws-ask"}}""";
    const string OcDone = """{"sessionUpdate":"tool_call_update","toolCallId":"@CALL@","status":"completed","locations":[{"path":"C:/Temp/acplab/ws-ask"}],"content":[{"type":"content","content":{"type":"text","text":"@OUT@\n"}}],"rawOutput":{"metadata":{"status":"completed","truncated":false,"exit":0}}}""";

    static string AgyAsking(string id) => AgyAsk.Replace("@SID@", AgyId).Replace("@ID@", id);
    static string OcAsking(int id, string call, string command, string session = OcId) => OcAsk.Replace("@SID@", session).Replace("@ID@", id.ToString()).Replace("@CALL@", call).Replace("@CMD@", command);
    static string OcRun(string call, string command) => OcRunning.Replace("@CALL@", call).Replace("@CMD@", command);
    static string OcRan(string call, string output) => OcDone.Replace("@CALL@", call).Replace("@OUT@", output);

    [Fact]
    public void Antigravitys_permission_request_reaches_the_viewers_and_their_answer_names_its_own_option()
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "Run the shell command: echo acp-lab-one");
        chat.FromCli(J(Update(AgyId, AgyRun)), record, T0);
        chat.FromCli(J(Update(AgyId, AgyRun.Replace("\"status\":\"in_progress\"", "\"status\":\"pending\"").Replace("\"sessionUpdate\":\"tool_call\"", "\"sessionUpdate\":\"tool_call_update\""))), record, T0);

        var asked = chat.FromCli(J(AgyAsking("1")), record, T0.AddSeconds(2));
        Assert.Empty(Sent(asked));
        var request = Kept(asked).Single();
        var r = request.GetProperty("request");
        Assert.Equal(("control_request", "acp-1", "can_use_tool", "Shell", "call_1797129992166531750", "echo acp-lab-one"), (Type(request), Str(request, "request_id"), Str(r, "subtype"), Str(r, "tool_name"), Str(r, "tool_use_id"), Str(r.GetProperty("input"), "command")));
        Assert.Equal(0, r.GetProperty("permission_suggestions").GetArrayLength());
        Assert.Equal(("waiting", "PermissionRequest", "Shell", T0.AddSeconds(2)), (record.AgentStatus, record.LastEvent, record.LastTool, record.StatusAt));

        var allowed = chat.FromViewer(J(Answer("acp-1", """{"behavior":"allow","updatedInput":{}}""")), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":"1","result":{"outcome":{"outcome":"selected","optionId":"allow"}}}""", Sent(allowed).Single().GetRawText());
        Assert.Equal(("control_response", "acp-1"), (Type(Kept(allowed).Single()), Str(Kept(allowed).Single().GetProperty("response"), "request_id")));
        Assert.Equal("busy", record.AgentStatus);
        Assert.Empty(chat.FromViewer(J(Answer("acp-1", """{"behavior":"allow"}""")), record, T0));
        Assert.Empty(chat.FromViewer(J(Answer("acp-99", """{"behavior":"allow"}""")), record, T0));

        chat.FromCli(J(AgyAsking("2")), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":"2","result":{"outcome":{"outcome":"selected","optionId":"deny"}}}""", Sent(chat.FromViewer(J(Answer("acp-2", """{"behavior":"deny","message":"no"}""")), record, T0)).Single().GetRawText());
        chat.FromCli(J(AgyAsking("3")), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":"3","result":{"outcome":{"outcome":"selected","optionId":"allow"}}}""", Sent(chat.FromViewer(J(Answer("acp-3", """{"behavior":"allow","updatedPermissions":[{"type":"addRules"}]}""")), record, T0)).Single().GetRawText());
    }

    [Fact]
    public void An_always_from_an_agent_not_known_to_keep_it_is_offered_for_the_session()
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "Do these in order, one tool call each.");
        var asked = Kept(chat.FromCli(J(OcAsking(0, "call_23c1050025b94e628e200a89", "echo ask-lab-one", AgyId)), record, T0)).Single(l => Type(l) == "control_request");
        Assert.Equal("session", Str(asked.GetProperty("request").GetProperty("permission_suggestions").EnumerateArray().Single(), "destination"));
    }

    [Fact]
    public void OpenCodes_permission_request_offers_always_for_the_folder_and_each_answer_picks_the_option_of_its_kind()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        Prompt(chat, record, "Do these in order, one tool call each.");
        const string Call = "call_23c1050025b94e628e200a89";
        chat.FromCli(J(Update(OcId, OcRun(Call, "echo ask-lab-one"))), record, T0);
        var asked = Kept(chat.FromCli(J(OcAsking(0, Call, "echo ask-lab-one")), record, T0)).Single();
        Assert.Equal("acp-0", Str(asked, "request_id"));
        var remember = asked.GetProperty("request").GetProperty("permission_suggestions").EnumerateArray().Single();
        Assert.Equal(("addRules", "allow", "localSettings", ""), (Str(remember, "type"), Str(remember, "behavior"), Str(remember, "destination"), Str(remember.GetProperty("rules").EnumerateArray().Single(), "toolName")));
        Assert.Equal("""{"jsonrpc":"2.0","id":0,"result":{"outcome":{"outcome":"selected","optionId":"once"}}}""", Sent(chat.FromViewer(J(Answer("acp-0", """{"behavior":"allow","updatedInput":{}}""")), record, T0)).Single().GetRawText());

        chat.FromCli(J(OcAsking(1, "call_00_EdU22LcwIgSzhONPHAUF6900", "echo ask-four")), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{"outcome":{"outcome":"selected","optionId":"always"}}}""", Sent(chat.FromViewer(J(Answer("acp-1", """{"behavior":"allow","updatedPermissions":[{"type":"addRules","rules":[{"toolName":""}],"behavior":"allow","destination":"localSettings"}]}""")), record, T0)).Single().GetRawText());

        chat.FromCli(J(OcAsking(2, "call_a6d68bfc353142359b6fbb5f", "echo ask-two")), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":2,"result":{"outcome":{"outcome":"selected","optionId":"reject"}}}""", Sent(chat.FromViewer(J(Answer("acp-2", """{"behavior":"deny","message":"no"}""")), record, T0)).Single().GetRawText());
    }

    [Fact]
    public void A_permission_request_without_a_call_is_called_off_and_anything_else_asked_is_not_handled()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        Prompt(chat, record, "go");
        var bare = chat.FromCli(J("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"" + OcId + "\",\"options\":[{\"optionId\":\"once\",\"kind\":\"allow_once\",\"name\":\"Allow once\"}]}}"), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":5,"result":{"outcome":{"outcome":"cancelled"}}}""", Sent(bare).Single().GetRawText());
        Assert.Empty(Kept(bare));
        Assert.Equal("busy", record.AgentStatus);

        var other = chat.FromCli(J("{\"jsonrpc\":\"2.0\",\"id\":\"rd-1\",\"method\":\"fs/read_text_file\",\"params\":{\"sessionId\":\"" + OcId + "\",\"path\":\"C:\\\\Temp\\\\acplab\\\\ws\\\\note.txt\"}}"), record, T0);
        var no = Sent(other).Single();
        Assert.Equal(("rd-1", -32601), (Str(no, "id"), no.GetProperty("error").GetProperty("code").GetInt32()));
        Assert.Contains("fs/read_text_file", Str(no.GetProperty("error"), "message"));
        Assert.Empty(Kept(other));
    }

    [Fact]
    public void A_permission_request_for_another_session_is_called_off()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        Prompt(chat, record, "go");
        var elsewhere = chat.FromCli(J(OcAsking(6, "call_23c1050025b94e628e200a89", "echo ask-lab-one", session: OcOther)), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":6,"result":{"outcome":{"outcome":"cancelled"}}}""", Sent(elsewhere).Single().GetRawText());
        Assert.Empty(Kept(elsewhere));
        Assert.Equal("busy", record.AgentStatus);
    }

    [Fact]
    public void A_call_that_runs_while_its_question_is_open_closes_the_question()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        Prompt(chat, record, "Run the shell command: echo ask-four. Then, as a separate tool call, run the shell command: echo ask-five.");
        const string Call = "call_00_xkxvj92386fr6thcbdq1lpak";
        chat.FromCli(J(Update(OcId, OcRun(Call, "echo ask-five"))), record, T0);
        chat.FromCli(J(OcAsking(1, Call, "echo ask-five")), record, T0);
        Assert.Equal("waiting", record.AgentStatus);

        var ran = chat.FromCli(J(Update(OcId, OcRan(Call, "ask-five"))), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{"outcome":{"outcome":"selected","optionId":"once"}}}""", Sent(ran).Single().GetRawText());
        Assert.Equal(new[] { "control_cancel_request", "user" }, Kept(ran).Select(Type));
        Assert.Equal("acp-1", Str(Kept(ran)[0], "request_id"));
        Assert.Equal("ask-five\n", Str(Block(Kept(ran)[1]), "content"));
        Assert.Equal("busy", record.AgentStatus);
        Assert.Empty(chat.FromViewer(J(Answer("acp-1", """{"behavior":"allow"}""")), record, T0));

        const string Declined = "call_a6d68bfc353142359b6fbb5f";
        chat.FromCli(J(OcAsking(2, Declined, "echo ask-two")), record, T0);
        var failed = chat.FromCli(J(Update(OcId, OcDeclined)), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":2,"result":{"outcome":{"outcome":"cancelled"}}}""", Sent(failed).Single().GetRawText());
        Assert.Equal(new[] { "control_cancel_request", "user" }, Kept(failed).Select(Type));
    }

    [Fact]
    public void A_stop_a_viewer_asks_for_cancels_the_turn_and_its_questions_and_says_it_was_interrupted()
    {
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "Count from 1 to 400, one number per line, nothing else.");
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n1"))), record, T0);
        var stop = chat.FromViewer(J(Interrupt("interrupt-1")), record, T0);
        var cancel = Sent(stop).Single();
        Assert.Equal(("session/cancel", AgyId, false), (Method(cancel), Str(Params(cancel), "sessionId"), cancel.TryGetProperty("id", out _)));
        Assert.Equal(("control_response", "success", "interrupt-1"), (Type(Passed(stop).Single()), Str(Passed(stop).Single().GetProperty("response"), "subtype"), Str(Passed(stop).Single().GetProperty("response"), "request_id")));

        var ended = chat.FromCli(J(Response(Id(turn), Stopped("cancelled"))), record, T0);
        Assert.Equal(new[] { "assistant", "user", "result" }, Kept(ended).Select(Type));
        Assert.Equal("[Request interrupted by user]", Said(Kept(ended)[1]));
        Assert.Equal(("error_during_execution", false), (Str(Kept(ended)[2], "subtype"), Kept(ended)[2].GetProperty("is_error").GetBoolean()));
        Assert.Equal("idle", record.AgentStatus);

        var idle = chat.FromViewer(J(Interrupt("interrupt-2")), record, T0);
        Assert.Empty(Sent(idle));
        Assert.Equal("success", Str(Passed(idle).Single().GetProperty("response"), "subtype"));

        var again = Prompt(chat, record, "Run the shell command: echo acp-lab-one");
        chat.FromCli(J(Update(AgyId, AgyRun)), record, T0);
        chat.FromCli(J(AgyAsking("4")), record, T0);
        var stopAsked = chat.FromViewer(J(Interrupt("interrupt-3")), record, T0);
        Assert.Equal(new[] { "{\"jsonrpc\":\"2.0\",\"id\":\"4\",\"result\":{\"outcome\":{\"outcome\":\"cancelled\"}}}", "session/cancel" },
            Sent(stopAsked).Select(m => Method(m).Length > 0 ? Method(m) : m.GetRawText()));
        Assert.Equal(("control_cancel_request", "acp-4"), (Type(Kept(stopAsked).Single()), Str(Kept(stopAsked).Single(), "request_id")));
        Assert.Empty(chat.FromViewer(J(Answer("acp-4", """{"behavior":"allow"}""")), record, T0));
        Assert.Equal("[Request interrupted by user]", Said(Kept(chat.FromCli(J(Response(Id(again), Stopped("cancelled"))), record, T0)).Single(l => Type(l) == "user")));
    }

    [Fact]
    public void A_turn_ended_as_cancelled_after_a_declined_call_was_not_interrupted()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var turn = Prompt(chat, record, "Run the shell command: echo ask-two. Then reply with exactly: ran");
        const string Call = "call_a6d68bfc353142359b6fbb5f";
        chat.FromCli(J(Update(OcId, Text("agent_thought_chunk", "The user wants me to run a shell command and reply exactly \"ran\".", "msg_1d52c9f6b001CfZ8V6swf62K0e:reasoning:0"))), record, T0);
        chat.FromCli(J(Update(OcId, """{"sessionUpdate":"tool_call","toolCallId":"call_a6d68bfc353142359b6fbb5f","name":"shell","title":"shell","kind":"execute","status":"pending","locations":[{"path":"C:/Temp/acplab/ws-ask"}],"rawInput":{"cwd":"C:/Temp/acplab/ws-ask"}}""")), record, T0);
        chat.FromCli(J(Update(OcId, OcRun(Call, "echo ask-two"))), record, T0);
        chat.FromCli(J(OcAsking(0, Call, "echo ask-two")), record, T0);
        Assert.Equal("""{"jsonrpc":"2.0","id":0,"result":{"outcome":{"outcome":"selected","optionId":"reject"}}}""", Sent(chat.FromViewer(J(Answer("acp-0", """{"behavior":"deny","message":"no"}""")), record, T0)).Single().GetRawText());
        Assert.True(Block(Kept(chat.FromCli(J(Update(OcId, OcDeclined)), record, T0)).Single()).GetProperty("is_error").GetBoolean());

        var ended = Kept(chat.FromCli(J(Response(Id(turn), """{"stopReason":"cancelled","usage":{"inputTokens":14323,"outputTokens":39,"totalTokens":15933},"_meta":{}}""")), record, T0));
        Assert.Equal(new[] { "result" }, ended.Select(Type));
        Assert.Equal(("error_during_execution", false), (Str(ended[0], "subtype"), ended[0].GetProperty("is_error").GetBoolean()));
    }

    [Fact]
    public void How_a_turn_ended_is_said_and_a_held_message_goes_next()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        List<JsonElement> End(string text, string answer, bool error = false)
        {
            var turn = Prompt(chat, record, text);
            return Kept(chat.FromCli(J(error ? "{\"jsonrpc\":\"2.0\",\"id\":" + Id(turn) + ",\"error\":" + answer + "}" : Response(Id(turn), answer)), record, T0));
        }

        var refused = End("one", Stopped("refusal"));
        Assert.Equal(new[] { "result" }, refused.Select(Type));
        Assert.Equal(("error", true, "The model declined to answer."), (Str(refused[0], "subtype"), refused[0].GetProperty("is_error").GetBoolean(), Str(refused[0], "result")));

        var cut = End("two", Stopped("max_tokens"));
        Assert.Equal(new[] { "result", "host" }, cut.Select(Type));
        Assert.Equal(("success", false, "The reply was cut off at the model's length limit."), (Str(cut[0], "subtype"), cut[0].GetProperty("is_error").GetBoolean(), Str(cut[0], "result")));
        Assert.Equal(("note", "The reply was cut off at the model's length limit."), (Str(cut[1], "subtype"), Str(cut[1], "text")));

        var failed = End("three", """{"code":-32603,"message":"Internal error: model unavailable"}""", error: true);
        Assert.Equal(("error", true, "Internal error: model unavailable"), (Str(failed.Single(), "subtype"), failed.Single().GetProperty("is_error").GetBoolean(), Str(failed.Single(), "result")));
        var mute = End("four", """{"code":-32603}""", error: true);
        Assert.Equal("OpenCode did not finish the turn", Str(mute.Single(), "result"));
        Assert.Equal(("idle", "Stop"), (record.AgentStatus, record.LastEvent));

        var turn = Prompt(chat, record, "five");
        Assert.Empty(chat.FromViewer(J(UserSays("six")), record, T0));
        var next = Sent(chat.FromCli(J(Failure(Id(turn), "boom")), record, T0)).Single();
        Assert.Equal(("session/prompt", "six"), (Method(next), Str(Params(next).GetProperty("prompt")[0], "text")));
    }

    static string Listed(string id, string? cwd, string title, string at)
    {
        var entry = new Dictionary<string, string> { ["sessionId"] = id };
        if (cwd != null) entry["cwd"] = cwd;
        entry["title"] = title;
        entry["updatedAt"] = at;
        return JsonSerializer.Serialize(entry);
    }

    static string Page(string? cursor, params string[] entries) => "{\"sessions\":[" + string.Join(",", entries) + "]" + (cursor != null ? ",\"nextCursor\":\"" + cursor + "\"" : "") + "}";
    static long Ms(string at) => DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    [Fact]
    public void Only_this_folders_conversations_are_listed_newest_first_without_the_open_one()
    {
        var (chat, record, _) = OpenWith("Agy");
        var list = Sent(chat.Sessions("s1", "viewer-a")).Single();
        Assert.Equal(("session/list", Cwd, false), (Method(list), Str(Params(list), "cwd"), Params(list).TryGetProperty("cursor", out _)));
        var answered = chat.FromCli(J(Response(Id(list), Page(null,
            Listed("cb8b2f78-96ca-4832-bebf-4edd3db028ea", "/C:/Temp/acplab/ws", "Sequential Tool Execution Tasks", "2026-11-13T13:32:14Z"),
            Listed(AgyId, "/C:/Temp/acplab/ws", "Sequential File And Shell Tasks", "2026-11-13T13:29:25Z"),
            Listed("11111111-1111-4111-8111-111111111111", @"C:\temp\acplab\ws", "Has no folder of its own", "2026-08-14T15:47:54Z"),
            Listed("22222222-2222-4222-8222-222222222222", @"C:\Temp\acplab\ws", "Nor has this one", "2026-08-14T15:45:49Z"),
            Listed("9a1e3c55-0d6b-4c51-a1f4-5b7e7a0c2d11", "/C:/Repos/elsewhere", "Elsewhere", "2026-11-13T13:40:00Z"),
            Listed("8b2f4d66-1e7c-4d62-b2a5-6c8f8b1d3e22", @"C:\temp\acplab\ws-ask", "Next door", "2026-11-13T13:41:00Z"),
            Listed("44444444-4444-4444-8444-444444444444", null, "No folder given", "2026-09-01T00:00:00Z"),
            Listed("../escape", "/C:/Temp/acplab/ws", "Not an id", "2026-11-13T13:50:00Z")))), record, T0);
        var reply = answered.Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a"), (reply.Kind, reply.Tag));
        var frame = Of(reply);
        Assert.Equal(("sessions", "s1", AgyId), (Type(frame), Str(frame, "request"), Str(frame, "current")));
        Assert.Equal(new[]
        {
            ("cb8b2f78-96ca-4832-bebf-4edd3db028ea", "Sequential Tool Execution Tasks", Ms("2026-11-13T13:32:14Z")),
            ("44444444-4444-4444-8444-444444444444", "No folder given", Ms("2026-09-01T00:00:00Z")),
        }, frame.GetProperty("sessions").EnumerateArray().Select(s => (Str(s, "id"), Str(s, "title"), s.GetProperty("at").GetInt64())));
        Assert.All(frame.GetProperty("sessions").EnumerateArray(), s => Assert.False(s.GetProperty("live").GetBoolean()));
    }

    [Fact]
    public void Where_no_folder_is_written_as_a_url_path_a_folder_written_plainly_is_where_it_says()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var list = Sent(chat.Sessions("s1", "viewer-a")).Single();
        var frame = Of(chat.FromCli(J(Response(Id(list), Page(null,
            Listed(OcOther, @"c:\temp\acplab\ws\", "Exact reply instruction", "2026-11-13T13:26:56.168Z"),
            Listed(OcThird, @"C:\Temp\acplab\ws-ask", "Ask lab", "2026-11-13T13:48:00.000Z")))), record, T0).Single());
        Assert.Equal(new[] { OcOther }, frame.GetProperty("sessions").EnumerateArray().Select(s => Str(s, "id")));
    }

    [Fact]
    public void A_list_in_pages_is_followed_to_an_empty_page()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var first = Sent(chat.Sessions("s1", "viewer-a")).Single();
        var second = Sent(chat.FromCli(J(Response(Id(first), Page("eyJvcmRlciI6ImRlc2MifQ",
            Listed(OcId, @"C:\Temp\acplab\ws", "Create and update note.txt", "2026-11-13T13:28:47.366Z"),
            Listed(OcOther, @"C:\Temp\acplab\ws", "Exact reply instruction", "2026-11-13T13:26:56.168Z")))), record, T0)).Single();
        Assert.Equal(("session/list", Cwd, "eyJvcmRlciI6ImRlc2MifQ"), (Method(second), Str(Params(second), "cwd"), Str(Params(second), "cursor")));
        var third = Sent(chat.FromCli(J(Response(Id(second), Page("eyJvcmRlciI6ImRlc2MiLCJwIjoyfQ",
            Listed(OcOther, @"C:\Temp\acplab\ws", "Exact reply instruction", "2026-11-13T13:26:56.168Z"),
            Listed(OcThird, @"C:\Temp\acplab\ws", "Ask lab", "2026-11-13T13:48:00.000Z")))), record, T0)).Single();
        Assert.Equal("eyJvcmRlciI6ImRlc2MiLCJwIjoyfQ", Str(Params(third), "cursor"));
        var reply = chat.FromCli(J(Response(Id(third), Page("eyJvcmRlciI6ImRlc2MiLCJwIjozfQ"))), record, T0).Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a"), (reply.Kind, reply.Tag));
        Assert.Equal(new[] { OcThird, OcOther }, Of(reply).GetProperty("sessions").EnumerateArray().Select(s => Str(s, "id")));
    }

    [Fact]
    public void A_list_that_never_ends_is_shown_as_it_stands_after_the_last_page_asked_for()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var ask = Sent(chat.Sessions("s1", "viewer-a")).Single();
        int asked = 1;
        List<ChatEffect> answered;
        while (true)
        {
            answered = chat.FromCli(J(Response(Id(ask), Page("cursor-" + asked, Listed($"ses_page{asked:D2}", @"C:\Temp\acplab\ws", "Page " + asked, $"2026-11-{asked:D2}T00:00:00Z")))), record, T0);
            if (Sent(answered).Count == 0) break;
            ask = Sent(answered).Single();
            asked++;
            Assert.True(asked <= AcpChat.ListPages, "asked for more pages than the limit");
        }
        Assert.Equal(AcpChat.ListPages, asked);
        Assert.Equal(AcpChat.ListPages, Of(answered.Single(e => e.Kind == ChatEffectKind.Reply)).GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public void Nothing_is_listed_before_a_session_opens_or_when_the_agent_cannot_list()
    {
        var chat = new AcpChat("OpenCode", Cwd, "", "", "", "");
        var record = new HostRecord();
        var hello = Sent(chat.Start()).Single();
        chat.FromCli(J(Response(Id(hello), OcInit)), record, T0);
        var early = chat.Sessions("s1", "viewer-a").Single();
        Assert.Equal((ChatEffectKind.Reply, 0), (early.Kind, Of(early).GetProperty("sessions").GetArrayLength()));

        var bare = new AcpChat("OpenCode", Cwd, "", "", "", "");
        var create = Sent(bare.FromCli(J(Response(Id(Sent(bare.Start()).Single()), BareInit)), record, T0)).Single();
        bare.FromCli(J(Response(Id(create), OcNew)), record, T0);
        var none = bare.Sessions("s2", "viewer-b").Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-b", OcId, 0), (none.Kind, none.Tag, Str(Of(none), "current"), Of(none).GetProperty("sessions").GetArrayLength()));
    }

    [Theory]
    [InlineData(@"C:\temp\acplab\ws", true)]
    [InlineData("/C:/Temp/acplab/ws", true)]
    [InlineData("/C:/Temp/acplab/ws/", true)]
    [InlineData(@"C:\Temp\acplab\ws\", true)]
    [InlineData("C:/Temp/acplab/ws", true)]
    [InlineData(@"C:\Temp\acplab\ws-ask", false)]
    [InlineData(@"C:\Temp\acplab", false)]
    [InlineData("/C:/Repos/elsewhere", false)]
    public void One_folder_is_one_folder_however_an_agent_spells_it(string spelled, bool same) =>
        Assert.Equal(same, AcpChat.SameFolder(spelled, Cwd));

    [Fact]
    public void The_root_of_a_drive_is_itself_however_spelled() =>
        Assert.True(AcpChat.SameFolder("/C:/", @"C:\") && AcpChat.SameFolder("C:/", @"c:\"));

    [Theory]
    [InlineData(AgyId, true)]
    [InlineData(OcId, true)]
    [InlineData("a.b_c-1", true)]
    [InlineData("", false)]
    [InlineData("../escape", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("ses\n", false)]
    [InlineData("\"quoted\"", false)]
    public void A_session_id_is_short_and_plain(string id, bool ok) => Assert.Equal(ok, AcpChat.IsSessionId(id));

    [Fact]
    public void A_session_id_is_at_most_128_characters()
    {
        Assert.True(AcpChat.IsSessionId(new string('a', 128)));
        Assert.False(AcpChat.IsSessionId(new string('a', 129)));
    }

    // ---- how full the context is ----

    [Theory]
    [InlineData("Agy", """{"sessionUpdate":"usage_update","used":9703,"size":1048576}""")]
    [InlineData("OpenCode", """{"sessionUpdate":"usage_update","used":9703,"size":1048576,"cost":{"amount":0,"currency":"USD"}}""")]
    public void The_agents_own_count_of_its_context_is_kept_on_the_record(string provider, string usage)
    {
        var (chat, record, _) = OpenWith(provider);
        Assert.Equal((0L, 0L), (record.ContextTokens, record.ContextWindow));
        string before = ChatStream.Signature(record);
        Assert.Empty(chat.FromCli(J(Update(SessionOf(provider), usage)), record, T0));
        Assert.Equal((9703L, 1048576L), (record.ContextTokens, record.ContextWindow));
        // It is a change to the record, so the host writes it out for the app to read.
        Assert.NotEqual(before, ChatStream.Signature(record));
    }

    [Theory]
    [InlineData("""{"sessionUpdate":"usage_update"}""")]
    [InlineData("""{"sessionUpdate":"usage_update","used":"many","size":1048576}""")]
    [InlineData("""{"sessionUpdate":"usage_update","used":-5,"size":1048576}""")]
    [InlineData("""{"sessionUpdate":"usage_update","used":1.5,"size":1048576}""")]
    public void A_count_that_is_not_one_changes_nothing(string usage)
    {
        var (chat, record, _) = OpenWith("Agy");
        chat.FromCli(J(Update(AgyId, """{"sessionUpdate":"usage_update","used":500,"size":1000}""")), record, T0);
        chat.FromCli(J(Update(AgyId, usage)), record, T0);
        Assert.Equal((500L, 1000L), (record.ContextTokens, record.ContextWindow));
    }

    [Theory]
    [InlineData("""{"sessionUpdate":"usage_update","used":700}""")]
    [InlineData("""{"sessionUpdate":"usage_update","used":700,"size":-1}""")]
    [InlineData("""{"sessionUpdate":"usage_update","used":700,"size":"big"}""")]
    public void A_count_with_no_size_keeps_the_count_and_says_no_size(string usage)
    {
        var (chat, record, _) = OpenWith("Agy");
        chat.FromCli(J(Update(AgyId, """{"sessionUpdate":"usage_update","used":500,"size":1000}""")), record, T0);
        chat.FromCli(J(Update(AgyId, usage)), record, T0);
        Assert.Equal((700L, 0L), (record.ContextTokens, record.ContextWindow));
    }

    [Fact]
    public void Moving_to_another_conversation_takes_its_count_or_none_and_never_keeps_the_old_one()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        chat.FromCli(J(Update(OcId, """{"sessionUpdate":"usage_update","used":90000,"size":262144}""")), record, T0);

        // The one moved to says its count while it is replayed: the record still shows the one open until the move is made.
        var load = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        chat.FromCli(J(Update(OcOther, """{"sessionUpdate":"usage_update","used":1200,"size":262144}""")), record, T0);
        Assert.Equal(90000L, record.ContextTokens);
        chat.FromCli(J(Response(Id(load), OcConfig())), record, T0);
        Assert.Equal((OcOther, 1200L, 262144L), (record.SessionId, record.ContextTokens, record.ContextWindow));

        // One that says none has none.
        var next = Sent(chat.Resume("r2", OcThird, "viewer-a")).Single();
        chat.FromCli(J(Response(Id(next), OcConfig())), record, T0);
        Assert.Equal((OcThird, 0L, 0L), (record.SessionId, record.ContextTokens, record.ContextWindow));
    }

    // ---- an agent that goes on after its turn ----

    static (AcpChat Chat, HostRecord Record) AfterATurn(string provider = "Agy")
    {
        var (chat, record, _) = OpenWith(provider);
        var turn = Prompt(chat, record, "make the diagrams");
        chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, T0);
        Assert.Equal("idle", record.AgentStatus);
        return (chat, record);
    }

    static DateTime After(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void What_an_agent_says_after_its_turn_ended_is_kept_when_it_pauses_and_not_left_for_the_next_message()
    {
        var (chat, record) = AfterATurn();

        // It goes on working: the session is busy again, and viewers watching are told a turn's worth of work began.
        var began = chat.FromCli(J(Update(AgyId, AgyRun)), record, After(1));
        Assert.Equal("busy", record.AgentStatus);
        Assert.Equal(("system", "init"), (Type(Passed(began)[0]), Str(Passed(began)[0], "subtype")));
        Assert.Equal("tool_use", Str(Block(Kept(began).Single()), "type"));
        Assert.Equal("tool_result", Str(Block(Kept(chat.FromCli(J(Update(AgyId, AgyRunDone)), record, After(3))).Single()), "type"));

        // Then it says its closing words. They are shown as they are typed, and are not yet a line of the conversation.
        var typed = chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "Done, "))), record, After(20));
        typed.AddRange(chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "the diagrams are ready."))), record, After(21)));
        Assert.Empty(Kept(typed));
        Assert.DoesNotContain(Passed(typed), l => Type(l) == "system");
        Assert.Empty(chat.Tick(record, After(22.9)));

        // Two seconds with nothing more said, and it is one: a viewer that was not watching reads it too.
        var kept = Kept(chat.Tick(record, After(23))).Single();
        Assert.Equal(("assistant", "Done, the diagrams are ready."), (Type(kept), Said(kept)));
        Assert.Empty(chat.Tick(record, After(24)));
        Assert.Equal("busy", record.AgentStatus);

        // Half a minute with nothing heard and no call running: the work is over, said as the end of a turn is.
        Assert.Empty(chat.Tick(record, After(50.9)));
        var over = Kept(chat.Tick(record, After(51))).Single();
        Assert.Equal(("result", "success", false), (Type(over), Str(over, "subtype"), over.GetProperty("is_error").GetBoolean()));
        Assert.Equal(("idle", "Stop"), (record.AgentStatus, record.LastEvent));
        Assert.Empty(chat.Tick(record, After(500)));
    }

    [Fact]
    public void A_message_sent_while_the_agent_was_still_saying_something_unasked_comes_after_it()
    {
        var (chat, record) = AfterATurn();
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "Done, the diagrams are ready."))), record, After(20));
        var sent = chat.FromViewer(J(UserSays("Are you still working?")), record, After(20.5));
        Assert.Equal(new[] { "assistant", "user" }, Kept(sent).Select(Type));
        Assert.Equal(("Done, the diagrams are ready.", "Are you still working?"), (Said(Kept(sent)[0]), Said(Kept(sent)[1])));
        Assert.Equal("session/prompt", Method(Sent(sent).Single()));
        // The turn that message started ends the work: no second end is said for the part nobody asked for.
        chat.FromCli(J(Response(Id(Sent(sent).Single()), Stopped("end_turn"))), record, After(30));
        Assert.Empty(chat.Tick(record, After(600)));
    }

    [Fact]
    public void Inside_a_turn_a_pause_ends_nothing()
    {
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "think hard");
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "Half a sentence"))), record, After(1));
        Assert.Empty(chat.Tick(record, After(10)));
        Assert.Empty(chat.Tick(record, After(1000)));
        var ended = Kept(chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(1001)));
        Assert.Equal(new[] { "assistant", "result" }, ended.Select(Type));
        Assert.Equal("Half a sentence", Said(ended[0]));
    }

    [Fact]
    public void Unasked_work_is_not_over_while_a_call_is_running_until_nothing_has_been_heard_for_ten_minutes()
    {
        var (chat, record) = AfterATurn();
        chat.FromCli(J(Update(AgyId, AgyRun)), record, After(1));
        Assert.Empty(chat.Tick(record, After(40)));
        Assert.Empty(chat.Tick(record, After(600)));
        Assert.Equal("busy", record.AgentStatus);
        // The work is over; the call that never reported is a command left running, and is waited on as one.
        var over = Kept(chat.Tick(record, After(601))).Single();
        Assert.Equal(("result", "call_1797129992166531750"), (Type(over), StillRunning(over).Single()));
        Assert.Equal("scheduled", record.AgentStatus);
    }

    [Fact]
    public void A_call_that_reports_its_end_after_the_turn_and_news_that_is_not_work_start_nothing()
    {
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "run it");
        chat.FromCli(J(Update(AgyId, AgyRun)), record, After(1));
        chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(2));
        // The call's end arrives late, after the turn's.
        var late = chat.FromCli(J(Update(AgyId, AgyRunDone)), record, After(3));
        Assert.Empty(Passed(late));
        Assert.Equal("idle", record.AgentStatus);
        Assert.Empty(Passed(chat.FromCli(J(Update(AgyId, ThreeCommands)), record, After(4))));
        Assert.DoesNotContain(Passed(chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", ""))), record, After(5))), l => Type(l) == "system");
        Assert.Equal("idle", record.AgentStatus);
        // A chunk with nothing in it is not something said. Viewers were still told a message began, so it is ended
        // for them by a line that says nothing (which a pane shows as nothing), and no work is ended, since none began.
        var ended = chat.Tick(record, After(60));
        Assert.Equal("", Said(Kept(ended).Single()));
        Assert.Equal("idle", record.AgentStatus);
    }

    [Fact]
    public void Codex_waits_on_no_pause()
    {
        var record = new HostRecord();
        Assert.Empty(new CodexChat(Cwd, "", "", "", CodexChat.Full).Tick(record, After(1000)));
    }

    // ---- a session that will come back by itself ----

    static string Schedule(string id, string status, bool update = false) =>
        "{\"sessionUpdate\":\"" + (update ? "tool_call_update" : "tool_call") + "\",\"toolCallId\":\"" + id + "\",\"name\":\"schedule\",\"args\":{\"DurationSeconds\":60,\"Prompt\":\"say it\",\"TimerCondition\":\"any\"},\"status\":\"" + status + "\",\"kind\":\"other\",\"title\":\"Run schedule\",\"rawInput\":{\"DurationSeconds\":60,\"Prompt\":\"say it\"}}";

    [Fact]
    public void A_turn_that_ends_with_a_timer_running_leaves_the_session_scheduled_until_the_timer_has_fired_and_its_work_is_done()
    {
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "/schedule in a minute, say it");
        chat.FromCli(J(Update(AgyId, Schedule("call_s1", "in_progress"))), record, After(3));
        chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(6));
        Assert.Equal(("scheduled", "Stop"), (record.AgentStatus, record.LastEvent));
        Assert.Empty(chat.Tick(record, After(40)));
        Assert.Equal("scheduled", record.AgentStatus);

        // It fires: the call reports its end, which is the agent starting on what the timer was for; then it says it.
        var fired = chat.FromCli(J(Update(AgyId, Schedule("call_s1", "completed", update: true))), record, After(63));
        Assert.Equal("busy", record.AgentStatus);
        Assert.Contains(Passed(fired), l => Type(l) == "system" && Str(l, "subtype") == "init");
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "TIMER-FIRED"))), record, After(66));
        Assert.Equal("busy", record.AgentStatus);
        Assert.Equal("TIMER-FIRED", Said(Kept(chat.Tick(record, After(68))).Single()));
        Assert.Equal("result", Type(Kept(chat.Tick(record, After(96))).Single()));
        Assert.Equal("idle", record.AgentStatus);

        // Said to be over a second time, it starts nothing.
        Assert.Empty(Passed(chat.FromCli(J(Update(AgyId, Schedule("call_s1", "completed", update: true))), record, After(100))));
        Assert.Equal("idle", record.AgentStatus);
    }

    [Fact]
    public void A_call_read_back_unfinished_does_not_keep_work_nobody_asked_for_open()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var load = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        chat.FromCli(J(Update(OcOther, AgyRun)), record, After(1));
        chat.FromCli(J(Response(Id(load), OcConfig())), record, After(2));

        chat.FromCli(J(Update(OcOther, Text("agent_message_chunk", "by the way"))), record, After(10));
        Assert.Equal("busy", record.AgentStatus);
        Assert.Equal("by the way", Said(Kept(chat.Tick(record, After(12))).Single()));
        // The call belongs to a process that is gone: nothing will ever report its end.
        Assert.Equal("result", Type(Kept(chat.Tick(record, After(40))).Single()));
        Assert.Equal("idle", record.AgentStatus);
    }

    [Fact]
    public void While_another_timer_is_still_running_the_session_goes_back_to_scheduled_and_a_timer_that_fires_silently_ends_it()
    {
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "two timers");
        chat.FromCli(J(Update(AgyId, Schedule("call_s1", "in_progress"))), record, After(1));
        chat.FromCli(J(Update(AgyId, Schedule("call_s2", "in_progress"))), record, After(2));
        chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(3));
        Assert.Equal("scheduled", record.AgentStatus);

        chat.FromCli(J(Update(AgyId, Schedule("call_s1", "completed", update: true))), record, After(60));
        Assert.Equal("busy", record.AgentStatus);
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "first one"))), record, After(61));
        chat.Tick(record, After(63));
        // The other timer is not work going on: this one's work is over after the usual quiet, and the session waits on the other.
        chat.Tick(record, After(91));
        Assert.Equal("scheduled", record.AgentStatus);

        // The second ends with nothing said (cancelled, or nothing to do): busy for the usual quiet, then waiting for the person.
        chat.FromCli(J(Update(AgyId, Schedule("call_s2", "failed", update: true))), record, After(120));
        Assert.Equal("busy", record.AgentStatus);
        Assert.Empty(chat.Tick(record, After(149)));
        Assert.Equal("result", Type(Kept(chat.Tick(record, After(150))).Single()));
        Assert.Equal("idle", record.AgentStatus);
    }

    [Fact]
    public void Only_a_timer_of_the_process_running_now_counts()
    {
        // A command left open at the end of a turn is waited on too (it was sent to the background), as a timer is.
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "run it");
        chat.FromCli(J(Update(AgyId, AgyRun)), record, After(1));
        chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(2));
        Assert.Equal("scheduled", record.AgentStatus);

        // A timer read back from an earlier process died with it: the conversation it is replayed into is not waiting on it.
        var (moved, movedRecord, _) = OpenWith("OpenCode");
        var load = Sent(moved.Resume("r1", OcOther, "viewer-a")).Single();
        moved.FromCli(J(Update(OcOther, Schedule("call_old", "in_progress"))), movedRecord, After(1));
        moved.FromCli(J(Response(Id(load), OcConfig())), movedRecord, After(2));
        Assert.Equal("idle", movedRecord.AgentStatus);
        moved.Tick(movedRecord, After(4));
        var next = Prompt(moved, movedRecord, "hello");
        moved.FromCli(J(Response(Id(next), Stopped("end_turn"))), movedRecord, After(5));
        Assert.Equal("idle", movedRecord.AgentStatus);
    }

    // ---- the agent's skills ----

    const string ThreeCommands = """{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"goal","description":"Run until done"},{"name":"plan","description":"Plan first"},{"name":"old-skill","description":"On its way out"}]}""";
    const string ReloadedCommands = """{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"goal","description":"Run until done"},{"name":"plan","description":"Plan carefully first"},{"name":"fresh-skill","description":"Just written"}]}""";

    static (ChatEffectKind Kind, string Tag, string Type, string Request, bool Ok) Head(ChatEffect e) =>
        (e.Kind, e.Tag, Type(Of(e)), Str(Of(e), "request"), Of(e).GetProperty("ok").GetBoolean());
    static string[] Names(JsonElement frame, string list) => frame.GetProperty(list).EnumerateArray().Select(n => n.GetString() ?? "").ToArray();

    [Fact]
    public void Only_an_agent_measured_to_read_its_skills_again_when_reattached_offers_a_reload()
    {
        var (_, _, agy) = OpenWith("Agy");
        Assert.True(Frames(agy).Single(f => Type(f) == "catalog").GetProperty("reload").GetBoolean());
        Assert.True(AcpChat.RereadsCommands("Agy"));
        Assert.False(AcpChat.RereadsCommands("OpenCode"));

        // OpenCode can be reattached too, and answers with the list it had: it is not offered there, and a request is refused.
        var (opencode, _, oc) = OpenWith("OpenCode");
        Assert.False(Frames(oc).Single(f => Type(f) == "catalog").GetProperty("reload").GetBoolean());
        var refused = opencode.Commands("c1", "viewer-a").Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a", "commands", "c1", false), Head(refused));
        Assert.Equal("OpenCode cannot reload its skills in a chat.", Str(Of(refused), "error"));

        // An Antigravity that does not say it can be reattached is not asked to be.
        var bare = new AcpChat("Agy", Cwd, "", "", "", "");
        var record = new HostRecord();
        var create = Sent(bare.FromCli(J(Response(Id(Sent(bare.Start()).Single()), BareInit)), record, T0)).Single();
        var opened = bare.FromCli(J(Response(Id(create), AgyNew)), record, T0);
        Assert.False(Frames(opened).Single(f => Type(f) == "catalog").GetProperty("reload").GetBoolean());
        Assert.Equal("Antigravity cannot reload its skills in a chat.", Str(Of(bare.Commands("c2", "viewer-a").Single()), "error"));
    }

    [Fact]
    public void A_reload_reattaches_to_the_open_conversation_and_answers_when_the_agent_says_its_commands_again()
    {
        var (chat, record, _) = OpenWith("Agy");
        chat.FromCli(J(Update(AgyId, ThreeCommands)), record, T0);
        string settings = chat.SettingsFrame();

        var asked = chat.Commands("c1", "viewer-a");
        var resume = Assert.Single(asked);
        Assert.Equal(ChatEffectKind.ToCli, resume.Kind);
        Assert.Equal(("session/resume", AgyId, Cwd, 0), (Method(Of(resume)), Str(Params(Of(resume)), "sessionId"), Str(Params(Of(resume)), "cwd"), Params(Of(resume)).GetProperty("mcpServers").GetArrayLength()));

        // The agent answers with the session's settings as it has them. Nothing of the answer is taken, and nobody is told anything yet.
        string otherSettings = "{\"sessionId\":\"" + AgyId + "\",\"configOptions\":" + AgyOptions.Replace("\"currentValue\":\"gemini-4-carbon\"", "\"currentValue\":\"gemini-4-argon\"") + "}";
        Assert.Contains("gemini-4-argon", otherSettings);
        Assert.Empty(chat.FromCli(J(Response(Id(Of(resume)), otherSettings)), record, T0));
        Assert.Equal(settings, chat.SettingsFrame());

        // Then it says its commands: every viewer gets the list, and the one that asked is told what is different.
        var told = chat.FromCli(J(Update(AgyId, ReloadedCommands)), record, T0);
        Assert.Equal(new[] { ChatEffectKind.Frame, ChatEffectKind.Reply }, told.Select(e => e.Kind));
        Assert.Equal(new[] { "goal", "plan", "fresh-skill" }, Of(told[0]).GetProperty("commands").EnumerateArray().Select(c => Str(c, "name")));
        Assert.Equal((ChatEffectKind.Reply, "viewer-a", "commands", "c1", true), Head(told[1]));
        var answer = Of(told[1]);
        Assert.Equal(3, answer.GetProperty("count").GetInt32());
        Assert.Equal(new[] { "fresh-skill" }, Names(answer, "added"));
        Assert.Equal(new[] { "old-skill" }, Names(answer, "removed"));
        Assert.Equal(new[] { "plan" }, Names(answer, "changed"));

        // Its commands said again later, unasked, are for everyone and an answer to nobody.
        Assert.Equal(new[] { ChatEffectKind.Frame }, chat.FromCli(J(Update(AgyId, ThreeCommands)), record, T0).Select(e => e.Kind));
    }

    [Fact]
    public void A_reload_that_finds_nothing_different_says_so()
    {
        var (chat, record, _) = OpenWith("Agy");
        chat.FromCli(J(Update(AgyId, ThreeCommands)), record, T0);
        var resume = Sent(chat.Commands("c1", "viewer-a")).Single();
        chat.FromCli(J(Response(Id(resume), AgyNew)), record, T0);
        var answer = Of(chat.FromCli(J(Update(AgyId, ThreeCommands)), record, T0).Single(e => e.Kind == ChatEffectKind.Reply));
        Assert.Equal((3, 0, 0, 0), (answer.GetProperty("count").GetInt32(), Names(answer, "added").Length, Names(answer, "removed").Length, Names(answer, "changed").Length));
    }

    [Fact]
    public void A_reload_waits_for_the_turn_to_end_and_what_is_typed_while_it_runs_waits_for_it()
    {
        var early = new AcpChat("Agy", Cwd, "", "", "", "");
        early.FromCli(J(Response(Id(Sent(early.Start()).Single()), AgyInit)), new HostRecord(), T0);
        Assert.Equal("The session is still opening.", Str(Of(early.Commands("c0", "viewer-a").Single()), "error"));

        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "busy now");
        var refused = chat.Commands("c1", "viewer-a").Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a", "commands", "c1", false), Head(refused));
        Assert.Equal("Reload when the turn has finished.", Str(Of(refused), "error"));
        chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, T0);

        var resume = Sent(chat.Commands("c2", "viewer-a")).Single();
        Assert.Equal("session/resume", Method(resume));
        Assert.Equal("The skills are already being reloaded.", Str(Of(chat.Commands("c3", "viewer-b").Single()), "error"));
        Assert.Equal("The session is already changing.", Str(Of(chat.Resume("r1", "11111111-1111-4111-8111-111111111111", "viewer-a").Single()), "error"));
        // A message typed before the agent has answered is not sent into the middle of that: it is the next turn.
        Assert.Empty(Sent(chat.FromViewer(J(UserSays("typed meanwhile")), record, T0)));
        var after = Sent(chat.FromCli(J(Response(Id(resume), AgyNew)), record, T0)).Single();
        Assert.Equal(("session/prompt", "typed meanwhile"), (Method(after), Str(Params(after).GetProperty("prompt")[0], "text")));
    }

    [Fact]
    public void A_reload_the_agent_refuses_is_told_to_the_asker_and_another_can_be_asked()
    {
        var (chat, record, _) = OpenWith("Agy");
        chat.FromCli(J(Update(AgyId, ThreeCommands)), record, T0);
        var first = Sent(chat.Commands("c1", "viewer-a")).Single();
        var failed = chat.FromCli(J(Failure(Id(first), "not now")), record, T0).Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a", "commands", "c1", false), Head(failed));
        Assert.Equal("Antigravity could not reload its skills: not now", Str(Of(failed), "error"));
        // Its commands said afterwards answer nobody: that request is over.
        Assert.DoesNotContain(chat.FromCli(J(Update(AgyId, ThreeCommands)), record, T0), e => e.Kind == ChatEffectKind.Reply);

        // An agent that answers and then never says its commands does not block the next request, which is the one answered.
        var second = Sent(chat.Commands("c2", "viewer-a")).Single();
        Assert.Empty(chat.FromCli(J(Response(Id(second), AgyNew)), record, T0));
        var third = Sent(chat.Commands("c3", "viewer-b")).Single();
        Assert.Equal("session/resume", Method(third));
        chat.FromCli(J(Response(Id(third), AgyNew)), record, T0);
        var answer = chat.FromCli(J(Update(AgyId, ReloadedCommands)), record, T0).Single(e => e.Kind == ChatEffectKind.Reply);
        Assert.Equal((ChatEffectKind.Reply, "viewer-b", "commands", "c3", true), Head(answer));
    }

    [Fact]
    public void Moving_to_another_conversation_ends_a_reload_that_was_waiting_for_the_commands()
    {
        var (chat, record, _) = OpenWith("Agy");
        var resume = Sent(chat.Commands("c1", "viewer-a")).Single();
        chat.FromCli(J(Response(Id(resume), AgyNew)), record, T0);
        Assert.Equal("session/load", Method(Sent(chat.Resume("r1", "11111111-1111-4111-8111-111111111111", "viewer-a")).Single()));
        // The conversation being loaded says its commands: that is its opening, not the answer to the reload.
        Assert.DoesNotContain(chat.FromCli(J(Update("11111111-1111-4111-8111-111111111111", ReloadedCommands)), record, T0), e => e.Kind == ChatEffectKind.Reply);
    }

    [Fact]
    public void A_move_is_refused_with_the_reason_when_it_cannot_be_made()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        foreach (var (session, why) in new[] { ("../elsewhere", "That is not a conversation."), ("", "That is not a conversation."), (OcId, "That conversation is the one open here.") })
        {
            var refused = chat.Resume("r0", session, "viewer-a").Single();
            Assert.Equal((ChatEffectKind.Reply, "viewer-a", "resumed", "r0", false, why), (refused.Kind, refused.Tag, Type(Of(refused)), Str(Of(refused), "request"), Of(refused).GetProperty("ok").GetBoolean(), Str(Of(refused), "error")));
        }
        var turn = Prompt(chat, record, "busy now");
        Assert.Equal("Stop the current turn first.", Str(Of(chat.Resume("r1", OcOther, "viewer-a").Single()), "error"));
        chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, T0);

        Assert.Equal("session/load", Method(Sent(chat.Resume("r2", OcOther, "viewer-a")).Single()));
        Assert.Equal("The session is already changing.", Str(Of(chat.Resume("r3", OcThird, "viewer-b").Single()), "error"));

        var early = new AcpChat("OpenCode", Cwd, "", "", "", "");
        early.FromCli(J(Response(Id(Sent(early.Start()).Single()), OcInit)), new HostRecord(), T0);
        Assert.Equal("The session is already changing.", Str(Of(early.Resume("r4", OcOther, "viewer-a").Single()), "error"));
    }

    [Fact]
    public void A_move_replays_the_conversation_into_a_fresh_scrollback_named_as_the_list_named_it()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var list = Sent(chat.Sessions("s1", "viewer-a")).Single();
        chat.FromCli(J(Response(Id(list), Page(null,
            Listed(OcId, @"C:\Temp\acplab\ws", "Create and update note.txt", "2026-11-13T13:28:47.366Z"),
            Listed(OcOther, @"C:\Temp\acplab\ws", "Exact reply instruction", "2026-11-13T13:26:56.168Z")))), record, T0);

        var load = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        Assert.Equal(("session/load", OcOther, Cwd), (Method(load), Str(Params(load), "sessionId"), Str(Params(load), "cwd")));
        Assert.Empty(chat.FromViewer(J(UserSays("after the move")), record, T0));
        Assert.Empty(chat.FromCli(J(Update(OcOther, Text("user_message_chunk", "Reply with exactly: ok", "msg_1d516f5b9001user0000000001"))), record, T0));
        Assert.Empty(chat.FromCli(J(Update(OcOther, Text("agent_message_chunk", "ok", "msg_1d516f5c0001cknepk9K9XP7Km"))), record, T0));
        Assert.Equal(OcId, record.SessionId);

        var loaded = chat.FromCli(J(Response(Id(load), OcConfig())), record, T0.AddSeconds(3));
        var seed = loaded.Single(e => e.Kind == ChatEffectKind.Reset).Seed!;
        Assert.Equal((OcOther, "Exact reply instruction", false), (seed.Session, seed.Title, seed.Earlier));
        var lines = seed.Lines.Select(l => J(Encoding.UTF8.GetString(l))).ToList();
        Assert.Equal(new[] { ("user", "Reply with exactly: ok"), ("assistant", "ok") }, lines.Select(l => (Type(l), Said(l))));
        Assert.All(lines, l => Assert.True(l.GetProperty("history").GetBoolean()));
        Assert.Equal((OcOther, "Exact reply instruction"), (record.SessionId, record.Title));
        Assert.Contains(Frames(loaded), f => Type(f) == "catalog");
        Assert.Empty(Sent(loaded));
        var next = Sent(chat.Tick(record, T0.AddSeconds(5))).Single();
        Assert.Equal(("session/prompt", OcOther, "after the move"), (Method(next), Str(Params(next), "sessionId"), Str(Params(next).GetProperty("prompt")[0], "text")));

        Assert.Empty(chat.FromCli(J(Update(OcId, Text("agent_message_chunk", "from the old one", "msg_old"))), record, T0));
    }

    [Fact]
    public void A_move_that_fails_tells_the_asker_and_the_session_stays()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var load = Sent(chat.Resume("r1", OcOther, "viewer-b")).Single();
        var told = chat.FromCli(J(Failure(Id(load), "Session not found")), record, T0).Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-b", false, "Could not resume that conversation: Session not found"), (told.Kind, told.Tag, Of(told).GetProperty("ok").GetBoolean(), Str(Of(told), "error")));
        Assert.Equal(OcId, record.SessionId);
        Assert.Equal(OcId, Str(Params(Prompt(chat, record, "still here")), "sessionId"));
    }

    [Fact]
    public void A_replay_larger_than_the_scrollback_keeps_the_newest_lines_and_is_named_by_what_was_first_said_in_them()
    {
        var (chat, record, _) = OpenWith("OpenCode", budget: 600);
        var load = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        foreach (string update in new[]
        {
            Text("user_message_chunk", new string('a', 300), "msg_u1"),
            Text("agent_message_chunk", new string('b', 300), "msg_a1"),
            Text("user_message_chunk", "short question", "msg_u2"),
            Text("agent_message_chunk", "tail", "msg_a2"),
        })
            chat.FromCli(J(Update(OcOther, update)), record, T0);
        var seed = chat.FromCli(J(Response(Id(load), OcConfig())), record, T0).Single(e => e.Kind == ChatEffectKind.Reset).Seed!;
        Assert.Equal(new[] { "msg_u2", "msg_a2" }, seed.Lines.Select(l => Str(J(Encoding.UTF8.GetString(l)), "uuid")));
        Assert.True(seed.Lines.Sum(l => l.Length + 1) <= 600);
        Assert.Equal("short question", seed.Title);
    }

    [Fact]
    public void There_is_never_an_earlier_page_to_give()
    {
        var (chat, _, _) = OpenWith("OpenCode");
        var page = chat.History("h1", "msg_1d5188c07001fdiBvEW46G1J9u", "viewer-a").Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a"), (page.Kind, page.Tag));
        Assert.Equal(("history", "h1", 0, JsonValueKind.Null), (Type(Of(page)), Str(Of(page), "request"), Of(page).GetProperty("lines").GetArrayLength(), Of(page).GetProperty("next").ValueKind));
    }

    [Fact]
    public void The_agents_commands_go_into_the_catalog()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        var told = chat.FromCli(J(Update(OcId, """{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"init","description":"guided AGENTS.md setup"},{"name":"review","description":"review changes [commit|branch|pr], defaults to uncommitted"},{"name":"","description":"nameless"}]}""")), record, T0);
        var catalog = Frames(told).Single();
        Assert.Equal("catalog", Type(catalog));
        Assert.Equal(new[] { ("init", "guided AGENTS.md setup", ""), ("review", "review changes [commit|branch|pr], defaults to uncommitted", "") },
            catalog.GetProperty("commands").EnumerateArray().Select(c => (Str(c, "name"), Str(c, "description"), Str(c, "hint"))));
        Assert.Equal(catalog.GetRawText(), chat.CatalogFrame());
        Assert.Equal(3, catalog.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public void What_is_not_this_sessions_or_not_an_object_is_let_go()
    {
        var (chat, record, _) = OpenWith("OpenCode");
        Assert.Empty(chat.FromCli(J(Update(OcOther, Text("agent_message_chunk", "not here", "msg_x"))), record, T0));
        foreach (string odd in new[] { "[]", "7", "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\"}", "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":[]}", "{\"jsonrpc\":\"2.0\",\"id\":999,\"result\":{}}", "{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"result\":{}}" })
            Assert.Empty(chat.FromCli(J(odd), record, T0));
        foreach (string odd in new[] { "{\"type\":\"user\"}", "{\"type\":\"user\",\"message\":7}", "{\"type\":\"control_response\",\"response\":5}", "{\"type\":\"control_request\",\"request\":[]}", "{}" })
            Assert.Empty(Sent(chat.FromViewer(J(odd), record, T0)));
    }

    // ---- the conversation before what the scrollback holds, and when it was said ----

    sealed class Disk : IAgentDisk
    {
        public readonly Dictionary<string, List<AgyStep>> Kept = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<AgySubagent>> Helpers = new(StringComparer.Ordinal);
        public IReadOnlyList<AgyStep> Steps(string conversation) => Kept.TryGetValue(conversation, out var steps) ? steps : new List<AgyStep>();
        public IReadOnlyList<AgySubagent> Subagents(string conversation) => Helpers.TryGetValue(conversation, out var found) ? found : new List<AgySubagent>();
    }

    const string AgyOther = "7c1d2e3f-4a5b-4c6d-8e7f-0a1b2c3d4e5f";
    static readonly string AgyLoaded = "{\"configOptions\":" + AgyOptions + "}";

    static JsonElement L(byte[] line) => J(Encoding.UTF8.GetString(line));
    static ChatSeed Seeded(List<ChatEffect> effects) => effects.Single(e => e.Kind == ChatEffectKind.Reset).Seed!;
    static JsonElement Earlier(AcpChat chat, string before) => Of(chat.History("h1", before, "viewer-a").Single());
    static string? When(JsonElement line) => line.TryGetProperty("timestamp", out var t) ? t.GetString() : null;

    /// <summary>A conversation of <paramref name="turns"/> short exchanges, replayed into a chat that then serves it.</summary>
    static (AcpChat Chat, HostRecord Record, ChatSeed Seed) Reopened(string provider, int turns, int budget, IAgentDisk? disk = null)
    {
        var chat = new AcpChat(provider, Cwd, "", "", "", "", budget, disk: disk);
        var record = new HostRecord();
        var create = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), provider == "Agy" ? AgyInit : OcInit)), record, T0)).Single();
        chat.FromCli(J(Response(Id(create), provider == "Agy" ? AgyNew : OcNew)), record, T0);
        string other = provider == "Agy" ? AgyOther : OcOther;
        var load = Sent(chat.Resume("r1", other, "viewer-a")).Single();
        for (int i = 1; i <= turns; i++)
        {
            chat.FromCli(J(Update(other, Text("user_message_chunk", $"question {i}"))), record, T0);
            chat.FromCli(J(Update(other, Text("agent_message_chunk", $"answer {i}"))), record, T0);
        }
        var loaded = chat.FromCli(J(Response(Id(load), provider == "Agy" ? AgyLoaded : OcConfig())), record, T0);
        return (chat, record, Seeded(loaded));
    }

    [Fact]
    public void A_conversation_too_long_for_the_scrollback_says_there_is_more_and_hands_it_over_a_page_at_a_time()
    {
        // 1,000 lines of about 140 bytes: the scrollback takes the end of them, and the rest is two pages and a bit.
        var (chat, _, seed) = Reopened("OpenCode", 500, budget: 6_000);
        Assert.True(seed.Earlier);
        var shown = seed.Lines.Select(L).ToList();
        Assert.InRange(shown.Count, 20, 60);
        Assert.Equal("answer 500", Said(shown[^1]));

        var page = Earlier(chat, Str(shown[0], "uuid"));
        Assert.Equal(("history", "h1"), (Type(page), Str(page, "request")));
        var lines = page.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(AcpChat.PageLines, lines.Count);
        Assert.Equal(JsonValueKind.Number, page.GetProperty("next").ValueKind);
        // It ends where what is shown begins, and is in order.
        int first = shown.Count;
        string Nth(int fromEnd) { int line = 1000 - fromEnd; return (line % 2 == 0 ? "question " : "answer ") + (line / 2 + 1); }
        Assert.Equal(Nth(first + 1), Said(lines[^1]));
        Assert.Equal(Nth(first + 2), Said(lines[^2]));
        Assert.Equal(Nth(first + AcpChat.PageLines), Said(lines[0]));

        // The page before that one, and then the one that reaches the start.
        var second = Earlier(chat, Str(lines[0], "uuid")).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(AcpChat.PageLines, second.Count);
        Assert.Equal(Nth(first + AcpChat.PageLines + 1), Said(second[^1]));
        var last = Earlier(chat, Str(second[0], "uuid"));
        Assert.Equal(JsonValueKind.Null, last.GetProperty("next").ValueKind);
        var start = last.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(("question 1", 1000 - first - 2 * AcpChat.PageLines), (Said(start[0]), start.Count));

        // Before the first line there is nothing, and a message nobody has is not a place.
        foreach (string none in new[] { Str(start[0], "uuid"), "no-such-message", "" })
        {
            var empty = Earlier(chat, none);
            Assert.Equal((0, JsonValueKind.Null), (empty.GetProperty("lines").GetArrayLength(), empty.GetProperty("next").ValueKind));
        }
        var asked = chat.History("h7", Str(shown[0], "uuid"), "viewer-b").Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-b", "h7"), (asked.Kind, asked.Tag, Str(Of(asked), "request")));
    }

    [Fact]
    public void A_page_is_as_many_lines_as_fit_and_never_none_while_there_are_some()
    {
        var chat = new AcpChat("OpenCode", Cwd, "", "", "", "", budget: 2_000);
        var record = new HostRecord();
        var create = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), OcInit)), record, T0)).Single();
        chat.FromCli(J(Response(Id(create), OcNew)), record, T0);
        var load = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        string big = new('x', AcpChat.PageBytes * 3 / 5);
        foreach (string said in new[] { "start", big, big, big, "end" })
        {
            chat.FromCli(J(Update(OcOther, Text("user_message_chunk", "q"))), record, T0);
            chat.FromCli(J(Update(OcOther, Text("agent_message_chunk", said))), record, T0);
        }
        var shown = Seeded(chat.FromCli(J(Response(Id(load), OcConfig())), record, T0)).Lines.Select(L).ToList();
        Assert.Equal("end", Said(shown[^1]));
        Assert.Equal("q", Said(shown[0]));

        // One big answer and the question after it fit; a second big answer does not.
        var page = Earlier(chat, Str(shown[0], "uuid")).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, big.Length }, page.Select(l => Said(l).Length));
        // A line bigger than a page is still a page.
        var huge = new AcpChat("OpenCode", Cwd, "", "", "", "", budget: 2_000);
        create = Sent(huge.FromCli(J(Response(Id(Sent(huge.Start()).Single()), OcInit)), record, T0)).Single();
        huge.FromCli(J(Response(Id(create), OcNew)), record, T0);
        load = Sent(huge.Resume("r1", OcOther, "viewer-a")).Single();
        huge.FromCli(J(Update(OcOther, Text("agent_message_chunk", new string('y', AcpChat.PageBytes * 2)))), record, T0);
        huge.FromCli(J(Update(OcOther, Text("user_message_chunk", "after it"))), record, T0);
        huge.FromCli(J(Update(OcOther, Text("agent_message_chunk", new string('z', 3_000)))), record, T0);
        shown = Seeded(huge.FromCli(J(Response(Id(load), OcConfig())), record, T0)).Lines.Select(L).ToList();
        Assert.Single(shown);
        var two = Earlier(huge, Str(shown[0], "uuid")).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal("after it", Said(two.Single()));
        Assert.Equal(AcpChat.PageBytes * 2, Said(Earlier(huge, Str(two[0], "uuid")).GetProperty("lines").EnumerateArray().Single()).Length);
    }

    [Fact]
    public void What_is_said_after_a_conversation_is_opened_is_remembered_with_its_time_for_the_pages_to_come()
    {
        var (chat, record, seed) = Reopened("OpenCode", 2, budget: 1_000_000);
        Assert.False(seed.Earlier);
        var turn = Sent(chat.FromViewer(J(UserSays("and now?")), record, After(60))).Single(m => Method(m) == "session/prompt");
        chat.FromCli(J(Update(OcOther, Text("agent_message_chunk", "Now this."))), record, After(62));
        chat.FromCli(J(Update(OcOther, AgyRun)), record, After(63));
        chat.FromCli(J(Update(OcOther, AgyRunDone)), record, After(64));
        chat.FromCli(J(Update(OcOther, Text("agent_message_chunk", "All done."))), record, After(65));
        var ended = Kept(chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(66)));
        Assert.Equal("All done.", Said(ended[0]));

        // The whole conversation before its last message: the replay, then what was said here, with the times it was said.
        var page = Earlier(chat, Str(ended[0], "uuid")).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(new[] { "question 1", "answer 1", "question 2", "answer 2", "and now?", "Now this." }, page.Take(6).Select(Said));
        Assert.Equal(new[] { "assistant", "user" }, page.Skip(6).Select(Type));
        // (What the agent says is whole, and so kept, when the next thing follows it: the call a second later.)
        Assert.Equal(new string?[] { null, null, null, null, "2026-11-13T13:01:00.000Z", "2026-11-13T13:01:03.000Z", "2026-11-13T13:01:03.000Z", "2026-11-13T13:01:04.000Z" }, page.Select(When));
        // A call and what it came back with are two lines a viewer can tell apart.
        Assert.Equal(Str(page[6], "uuid") + ":r", Str(page[7], "uuid"));
        // And what a viewer is handed as it happens carries the time a page will give it.
        Assert.Equal("2026-11-13T13:01:06.000Z", When(ended[0]));    }

    [Fact]
    public void Moving_to_another_conversation_or_starting_one_leaves_nothing_of_the_last_ones_past()
    {
        var (chat, record, seed) = Reopened("OpenCode", 30, budget: 1_500);
        Assert.True(seed.Earlier);
        string oldest = Str(L(seed.Lines[0]), "uuid");
        Assert.NotEqual(0, Earlier(chat, oldest).GetProperty("lines").GetArrayLength());

        var load = Sent(chat.Resume("r2", OcThird, "viewer-a")).Single();
        chat.FromCli(J(Update(OcThird, Text("user_message_chunk", "another one"))), record, T0);
        // Until it is the one served, the pages are still the open conversation's.
        Assert.NotEqual(0, Earlier(chat, oldest).GetProperty("lines").GetArrayLength());
        var next = Seeded(chat.FromCli(J(Response(Id(load), OcConfig())), record, T0));
        Assert.False(next.Earlier);
        Assert.Equal(0, Earlier(chat, oldest).GetProperty("lines").GetArrayLength());
        Assert.Equal(0, Earlier(chat, Str(L(next.Lines[0]), "uuid")).GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public void The_past_is_kept_up_to_a_size_and_the_oldest_of_it_goes_first()
    {
        var (chat, record, _) = Reopened("OpenCode", 0, budget: 1_000_000);
        chat.LogMost = 1_200;
        string last = "";
        for (int i = 1; i <= 20; i++)
        {
            var turn = Sent(chat.FromViewer(J(UserSays($"question {i}")), record, After(i))).Single(m => Method(m) == "session/prompt");
            chat.FromCli(J(Update(OcOther, Text("agent_message_chunk", $"answer {i}"))), record, After(i));
            last = Str(Kept(chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(i)))[0], "uuid");
        }
        var page = Earlier(chat, last);
        var lines = page.GetProperty("lines").EnumerateArray().Where(l => Type(l) != "result").ToList();
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next").ValueKind);
        Assert.Equal("question 20", Said(lines[^1]));
        Assert.InRange(lines.Count, 2, 12);
        Assert.DoesNotContain(lines, l => Said(l) is "question 1" or "answer 1" or "question 10");
    }

    [Fact]
    public void A_replayed_Antigravity_conversation_gets_the_times_its_transcript_on_disk_has()
    {
        var disk = new Disk();
        disk.Kept[AgyOther] = new[]
        {
            AgyDiskTests.Asked(0, "2026-03-04T10:00:05Z", "question 1"), AgyDiskTests.Planned(1, "2026-03-04T10:00:07Z", "answer 1"),
            AgyDiskTests.Asked(2, "2026-03-04T18:30:00Z", "question 2"), AgyDiskTests.Planned(3, "2026-03-04T18:30:09Z", "answer 2"),
        }.Select(l => AgyStep.Parse(Encoding.UTF8.GetBytes(l))!).ToList();

        var (chat, _, seed) = Reopened("Agy", 3, budget: 1_000_000, disk);
        var lines = seed.Lines.Select(L).ToList();
        // The third exchange is not on disk: it has no time, and is not given one.
        Assert.Equal(new string?[] { "2026-03-04T10:00:05.000Z", "2026-03-04T10:00:07.000Z", "2026-03-04T18:30:00.000Z", "2026-03-04T18:30:09.000Z", null, null }, lines.Select(When));
        Assert.All(lines, l => Assert.True(l.GetProperty("history").GetBoolean()));
        // An earlier page has them too.
        Assert.Equal("2026-03-04T10:00:05.000Z", When(Earlier(chat, Str(lines[1], "uuid")).GetProperty("lines")[0]));

        // The same steps under a conversation of another agent are not its transcript.
        var elsewhere = new Disk();
        elsewhere.Kept[OcOther] = disk.Kept[AgyOther];
        Assert.All(Reopened("OpenCode", 2, budget: 1_000_000, elsewhere).Seed.Lines.Select(L), l => Assert.Null(When(l)));
        // And with nothing on disk the replay is as it came.
        Assert.All(Reopened("Agy", 2, budget: 1_000_000, new Disk()).Seed.Lines.Select(L), l => Assert.Null(When(l)));
    }

    // ---- subagents ----

    const string Sub1 = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", Sub2 = "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff";

    static string Spawning(string id, string status, string role, bool update = false) =>
        "{\"sessionUpdate\":\"" + (update ? "tool_call_update" : "tool_call") + "\",\"toolCallId\":\"" + id + "\",\"name\":\"invoke_subagent\",\"args\":{\"Prompt\":\"List the files.\",\"Role\":\"" + role + "\",\"TypeName\":\"self\"},\"status\":\"" + status + "\",\"kind\":\"other\",\"title\":\"Run invoke_subagent\",\"rawInput\":{\"Prompt\":\"List the files.\",\"Role\":\"" + role + "\",\"TypeName\":\"self\"}}";

    static List<AgyStep> StepsOf(params string[] lines) => lines.Select(l => AgyStep.Parse(Encoding.UTF8.GetBytes(l))!).ToList();
    static AgyNews News(string conversation, params (string Id, string Role, string State, List<AgyStep> Steps)[] subagents) =>
        new(conversation, subagents.Select((s, i) => new AgySubagentNews(new AgySubagent(s.Id, s.Role, "self", s.State, i + 2), s.Steps)).ToList());
    const string Alive = "SUBAGENT_STATE_ALIVE";
    static string? Parent(JsonElement line) => line.GetProperty("parent_tool_use_id").GetString();

    /// <summary>A turn in which the agent starts one subagent and then ends its turn, as it was measured to: the call is over at once.</summary>
    static (AcpChat Chat, HostRecord Record, long Turn) Delegated(string role = "File Lister", bool ended = true)
    {
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "have a subagent list the files");
        chat.FromCli(J(Update(AgyId, Spawning("call_sp1", "in_progress", role))), record, After(4));
        chat.FromCli(J(Update(AgyId, Spawning("call_sp1", "completed", role, update: true))), record, After(5));
        if (ended) chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(9));
        return (chat, record, Id(turn));
    }

    [Fact]
    public void The_call_that_starts_a_subagent_reads_as_an_agent_with_its_role_and_what_it_was_asked()
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "have a subagent list the files");
        var shown = Kept(chat.FromCli(J(Update(AgyId, Spawning("call_sp1", "in_progress", "File Lister"))), record, After(4))).Single();
        var block = Block(shown);
        Assert.Equal(("tool_use", "call_sp1", "Agent"), (Str(block, "type"), Str(block, "id"), Str(block, "name")));
        Assert.Equal(("File Lister", "List the files.", "self"), (Str(block.GetProperty("input"), "description"), Str(block.GetProperty("input"), "prompt"), Str(block.GetProperty("input"), "subagent_type")));
        // One that names no role is still a subagent.
        var bare = Kept(chat.FromCli(J(Update(AgyId, Spawning("call_sp2", "in_progress", ""))), record, After(5))).Single();
        Assert.Equal("Subagent", Str(Block(bare).GetProperty("input"), "description"));
    }

    [Fact]
    public void How_far_along_a_subagent_is_reads_by_what_its_command_is_for()
    {
        var (chat, record, _) = Delegated(ended: false);
        chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Notice(0, "2026-03-04T10:00:00Z", "your task")))), record, After(6));
        var ran = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Calling(1, "2026-03-04T10:00:02Z", "run_command", ("CommandLine", "\"dir /b\""), ("toolSummary", "\"List the files\""))))), record, After(10)));
        Assert.Equal(("dir /b", "List the files"), (Str(Block(ran[0]).GetProperty("input"), "command"), Str(Block(ran[0]).GetProperty("input"), "summary")));
        Assert.Equal("Shell List the files", Str(ran[1], "description"));
    }

    [Fact]
    public void What_a_subagent_does_is_shown_under_its_call_as_it_writes_it_and_the_session_waits_on_it()
    {
        var (chat, record, turn) = Delegated(ended: false);
        // It shows up on disk while the turn that started it is still running.
        var seen = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Notice(0, "2026-03-04T10:00:00Z", "your task")))), record, After(6)));
        var started = seen.Single();
        Assert.Equal(("system", "task_started", "call_sp1", Sub1, "File Lister", true), (Type(started), Str(started, "subtype"), Str(started, "tool_use_id"), Str(started, "task_id"), Str(started, "description"), started.GetProperty("is_backgrounded").GetBoolean()));
        Assert.Equal("busy", record.AgentStatus);

        // The turn ends with the subagent at work: the session is waiting on something of its own.
        chat.FromCli(J(Response(turn, Stopped("end_turn"))), record, After(9));
        Assert.Equal(("scheduled", "Stop"), (record.AgentStatus, record.LastEvent));

        // It runs a command: the call, under the call that started it, and how far along it is.
        var ran = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", thinking: "I will list them.", calls: new[] { ("run_command", "CommandLine", "\"dir /b\"") })))), record, After(10)));
        Assert.Equal(new[] { "assistant", "system" }, ran.Select(Type));
        Assert.Equal(("call_sp1", "tool_use", "Shell", "dir /b"), (Parent(ran[0]), Str(Block(ran[0]), "type"), Str(Block(ran[0]), "name"), Str(Block(ran[0]).GetProperty("input"), "command")));
        Assert.Equal(("task_progress", "call_sp1", "Shell dir /b", 1), (Str(ran[1], "subtype"), Str(ran[1], "tool_use_id"), Str(ran[1], "description"), ran[1].GetProperty("usage").GetProperty("tool_uses").GetInt32()));
        Assert.Equal("2026-11-13T13:00:10.000Z", When(ran[0]));

        // What the command came back with, without the header Antigravity puts on it.
        var back = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Outcome(2, "2026-03-04T10:00:09Z", "Created At: then\nCompleted At: later\nCreated At: then\nCompleted At: later\na.txt\nb.txt")))), record, After(17)));
        Assert.Equal(("user", "call_sp1", "tool_result", Str(Block(ran[0]), "id"), "a.txt\nb.txt"), (Type(back[0]), Parent(back[0]), Str(Block(back[0]), "type"), Str(Block(back[0]), "tool_use_id"), Str(Block(back[0]), "content")));
        Assert.Equal("scheduled", record.AgentStatus);

        // It says its last words and makes no call: its turn is over, and so is the wait.
        var reported = chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Planned(3, "2026-03-04T10:00:12Z", "Two files: a.txt and b.txt.")))), record, After(20));
        var done = Kept(reported);
        Assert.Equal(("call_sp1", "Two files: a.txt and b.txt."), (Parent(done[0]), Said(done[0])));
        Assert.Equal(("task_notification", "call_sp1", "completed"), (Str(done[^1], "subtype"), Str(done[^1], "tool_use_id"), Str(done[^1], "status")));
        Assert.NotEqual(Str(ran[0], "uuid"), Str(done[0], "uuid"));
        // It has reported to the agent, which goes on from there: the session is at work, until that has gone quiet.
        Assert.Equal("busy", record.AgentStatus);
        Assert.Contains(Passed(reported), l => Type(l) == "system" && Str(l, "subtype") == "init");
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "The subagent found two files."))), record, After(22));
        Assert.Equal("The subagent found two files.", Said(Kept(chat.Tick(record, After(24))).Single()));
        Assert.Equal("result", Type(Kept(chat.Tick(record, After(52))).Single()));
        Assert.Equal("idle", record.AgentStatus);
        // Looked at again with nothing new, nothing is said and nothing changes.
        Assert.Empty(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, new List<AgyStep>())), record, After(60)));
        Assert.Equal("idle", record.AgentStatus);

        // Spoken to again it goes back to work, and is said to.
        var again = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Notice(4, "2026-03-04T10:05:00Z", "one more thing")))), record, After(300)));
        Assert.Equal("task_started", Str(again.Single(), "subtype"));
        Assert.Equal("scheduled", record.AgentStatus);
    }

    [Fact]
    public void A_subagent_that_ends_with_nothing_said_by_anyone_stops_being_waited_on()
    {
        var (chat, record, _) = Delegated();
        Assert.Equal("idle", record.AgentStatus);
        chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", calls: new[] { ("run_command", "CommandLine", "\"build\"") })))), record, After(10));
        Assert.Equal("scheduled", record.AgentStatus);

        // Killed: its file says so, and nothing more is written.
        var stopped = chat.Outside(News(AgyId, (Sub1, "File Lister", "SUBAGENT_STATE_KILLED", new List<AgyStep>())), record, After(40));
        var killed = Kept(stopped);
        Assert.Equal(("task_notification", "stopped"), (Str(killed.Single(), "subtype"), Str(killed.Single(), "status")));
        // Killed is not a report: nothing follows from it.
        Assert.Empty(Passed(stopped));
        Assert.Equal("idle", record.AgentStatus);

        // Silent: a step is written when it ends, so a long command is silence, and so is a subagent that died. The
        // wait lasts as long as a long command might, and no longer.
        var (quiet, waiting, _) = Delegated();
        quiet.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", calls: new[] { ("run_command", "CommandLine", "\"build\"") })))), waiting, After(10));
        quiet.Tick(waiting, After(10 + 29 * 60));
        Assert.Equal("scheduled", waiting.AgentStatus);
        quiet.Tick(waiting, After(10 + 30 * 60));
        Assert.Equal("idle", waiting.AgentStatus);
    }

    [Fact]
    public void Each_subagent_goes_under_the_call_that_started_it_by_order_and_by_role()
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "two helpers");
        chat.FromCli(J(Update(AgyId, Spawning("call_a", "in_progress", "Reader"))), record, After(1));
        chat.FromCli(J(Update(AgyId, Spawning("call_a", "completed", "Reader", update: true))), record, After(1));
        chat.FromCli(J(Update(AgyId, Spawning("call_b", "in_progress", "Writer"))), record, After(2));
        chat.FromCli(J(Update(AgyId, Spawning("call_c", "in_progress", "Reader"))), record, After(3));
        // The writer is seen first; then two readers, which are told apart by which was started first.
        var first = Kept(chat.Outside(News(AgyId, (Sub2, "Writer", Alive, new List<AgyStep>())), record, After(4)));
        Assert.Equal(("call_b", Sub2), (Str(first.Single(), "tool_use_id"), Str(first.Single(), "task_id")));
        const string Sub3 = "cccccccc-dddd-4eee-8fff-000000000000";
        var readers = Kept(chat.Outside(News(AgyId, (Sub1, "Reader", Alive, new List<AgyStep>()), (Sub3, "Reader", Alive, new List<AgyStep>())), record, After(5)));
        Assert.Equal(new[] { ("call_a", Sub1), ("call_c", Sub3) }, readers.Select(l => (Str(l, "tool_use_id"), Str(l, "task_id"))));
        // Told again of ones already placed, nothing is said twice.
        Assert.Empty(Kept(chat.Outside(News(AgyId, (Sub1, "Reader", Alive, new List<AgyStep>())), record, After(6))));
        // A call made later starts with nothing of theirs.
        Assert.Equal("Agent", Str(Block(Kept(chat.FromCli(J(Update(AgyId, Spawning("call_d", "in_progress", "Writer"))), record, After(7))).Single()), "name"));
    }

    [Fact]
    public void A_subagents_turn_ends_only_with_words_and_no_call()
    {
        var (chat, record, turn) = Delegated(ended: false);
        // Words with a call, and a step that is only thought: it goes on.
        var lines = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(
            AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", "First I will write the note.", calls: new[] { ("write_file", "Path", "\"C:\\\\x\\\\note.txt\"") })))), record, After(6)));
        Assert.Equal(new[] { "task_started", null, null, "task_progress" }, lines.Select(l => Type(l) == "system" ? Str(l, "subtype") : null));
        // What the call came back with, and a step that is only thought: it goes on, and no end is said.
        var next = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(
            AgyDiskTests.Outcome(2, "2026-03-04T10:00:03Z", "Created At: then\nCommand: whatever\nCommand output:\nwritten"),
            AgyDiskTests.Planned(3, "2026-03-04T10:00:04Z", thinking: "What next?")))), record, After(6)));
        Assert.Equal(new[] { null, "task_progress" }, next.Select(l => Type(l) == "system" ? Str(l, "subtype") : null));
        Assert.Equal(("First I will write the note.", "Edit", @"C:\x\note.txt", "written"), (Said(lines[1]), Str(Block(lines[2]), "name"), Str(Block(lines[2]).GetProperty("input"), "file_path"), Str(Block(next[0]), "content")));
        // A call the pane has no reading for is its own name in plain words, with the first thing it was given.
        var sent = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Planned(4, "2026-03-04T10:00:05Z", calls: new[] { ("send_message", "Message", "\"All written.\"") })))), record, After(7)));
        Assert.Equal(("Send message", "All written."), (Str(Block(sent[0]), "name"), Str(Block(sent[0]).GetProperty("input"), "description")));

        // It finishes while the turn that started it is still running: nothing is waited on when the turn ends, and
        // nothing more comes of it.
        var over = chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Outcome(5, "2026-03-04T10:00:06Z", "sent"), AgyDiskTests.Planned(6, "2026-03-04T10:00:07Z", "Done.")))), record, After(8));
        Assert.Equal("task_notification", Str(Kept(over)[^1], "subtype"));
        Assert.Empty(Passed(over));
        Assert.Equal("busy", record.AgentStatus);
        chat.FromCli(J(Response(turn, Stopped("end_turn"))), record, After(9));
        Assert.Equal("idle", record.AgentStatus);
        Assert.Empty(chat.Tick(record, After(60)));
        Assert.Equal("idle", record.AgentStatus);
    }

    [Fact]
    public void A_subagent_waits_for_its_call_to_be_shown_with_all_it_has_done_meanwhile()
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "a helper");
        // Announced, not yet shown: there is nothing to put the subagent under.
        Assert.Empty(Kept(chat.FromCli(J(Update(AgyId, Spawning("call_a", "pending", "Reader"))), record, After(1))));
        Assert.Empty(Kept(chat.Outside(News(AgyId, (Sub1, "Reader", Alive, StepsOf(AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", "On it.", calls: new[] { ("run_command", "CommandLine", "\"dir\"") })))), record, After(2))));
        Assert.Empty(Kept(chat.Outside(News(AgyId, (Sub1, "Reader", Alive, StepsOf(AgyDiskTests.Outcome(2, "2026-03-04T10:00:03Z", "a.txt")))), record, After(3))));
        var shown = Kept(chat.FromCli(J(Update(AgyId, Spawning("call_a", "in_progress", "Reader", update: true))), record, After(4)));
        Assert.Equal(new[] { "assistant", "system", "assistant", "assistant", "user", "system" }, shown.Select(Type));
        Assert.Equal(("Agent", "task_started", "On it.", "Shell", "a.txt", "task_progress"), (Str(Block(shown[0]), "name"), Str(shown[1], "subtype"), Said(shown[2]), Str(Block(shown[3]), "name"), Str(Block(shown[4]), "content"), Str(shown[5], "subtype")));
    }

    [Fact]
    public void A_subagent_no_call_of_this_conversation_started_is_never_put_under_one()
    {
        // Seen before its call: it waits for it, with what it has done meanwhile.
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "a helper");
        Assert.Empty(Kept(chat.Outside(News(AgyId, (Sub1, "Reader", Alive, StepsOf(AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", "On it.")))), record, After(1))));
        var late = Kept(chat.FromCli(J(Update(AgyId, Spawning("call_a", "in_progress", "Reader"))), record, After(2)));
        Assert.Equal(new[] { "assistant", "system", "assistant", "system" }, late.Select(Type));
        Assert.Equal(("tool_use", "task_started", "call_a", "task_notification"), (Str(Block(late[0]), "type"), Str(late[1], "subtype"), Parent(late[2]), Str(late[3], "subtype")));

        // Of another role than any call asked for: not this call's, and shown nowhere.
        var (other, otherRecord, _) = Delegated("File Lister");
        Assert.Empty(Kept(other.Outside(News(AgyId, (Sub1, "Somebody Else", Alive, StepsOf(AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", "Hello.")))), otherRecord, After(10))));
        Assert.Equal("idle", otherRecord.AgentStatus);
        // About another conversation: nothing.
        Assert.Empty(other.Outside(News(AgyOther, (Sub1, "File Lister", Alive, new List<AgyStep>())), otherRecord, After(11)));
        // And a call read back from an earlier process starts nothing here.
        var (moved, movedRecord, _) = OpenWith("Agy");
        var load = Sent(moved.Resume("r1", AgyOther, "viewer-a")).Single();
        moved.FromCli(J(Update(AgyOther, Spawning("call_old", "completed", "File Lister"))), movedRecord, After(1));
        moved.FromCli(J(Response(Id(load), AgyLoaded)), movedRecord, After(2));
        Assert.Empty(Kept(moved.Outside(News(AgyOther, (Sub1, "File Lister", Alive, new List<AgyStep>())), movedRecord, After(3))));
        Assert.Equal("idle", movedRecord.AgentStatus);
    }

    [Fact]
    public void Moving_to_another_conversation_leaves_the_last_ones_subagents_behind()
    {
        var (chat, record, _) = Delegated();
        chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, new List<AgyStep>())), record, After(10));
        Assert.Equal("scheduled", record.AgentStatus);
        var load = Sent(chat.Resume("r1", AgyOther, "viewer-a")).Single();
        chat.FromCli(J(Response(Id(load), AgyLoaded)), record, After(12));
        Assert.Equal("idle", record.AgentStatus);
        Assert.Empty(chat.Outside(News(AgyOther, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", "Still here.")))), record, After(13)));
        chat.Tick(record, After(14));
        Assert.Equal("idle", record.AgentStatus);
    }

    [Fact]
    public void A_reopened_conversation_shows_what_its_subagents_did_and_none_of_them_as_at_work()
    {
        var disk = new Disk();
        disk.Helpers[AgyOther] = new() { new AgySubagent(Sub1, "File Lister", "self", Alive, 2) };
        disk.Kept[Sub1] = StepsOf(
            AgyDiskTests.Notice(0, "2026-03-04T10:00:00Z", "your task"),
            AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", calls: new[] { ("run_command", "CommandLine", "\"dir /b\"") }),
            AgyDiskTests.Outcome(2, "2026-03-04T10:00:09Z", "a.txt"),
            AgyDiskTests.Planned(3, "2026-03-04T10:00:12Z", "One file."));

        List<JsonElement> Reopen(IAgentDisk from, string role = "File Lister", int calls = 1)
        {
            var chat = new AcpChat("Agy", Cwd, "", "", "", "", disk: from);
            var record = new HostRecord();
            var create = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), AgyInit)), record, T0)).Single();
            chat.FromCli(J(Response(Id(create), AgyNew)), record, T0);
            var load = Sent(chat.Resume("r1", AgyOther, "viewer-a")).Single();
            chat.FromCli(J(Update(AgyOther, Text("user_message_chunk", "have a subagent list the files"))), record, T0);
            for (int i = 1; i <= calls; i++) chat.FromCli(J(Update(AgyOther, Spawning("call_old" + i, "completed", role))), record, T0);
            chat.FromCli(J(Update(AgyOther, Text("agent_message_chunk", "It found one file."))), record, T0);
            var lines = Seeded(chat.FromCli(J(Response(Id(load), AgyLoaded)), record, T0)).Lines.Select(L).ToList();
            Assert.Equal("idle", record.AgentStatus);
            return lines;
        }

        var lines = Reopen(disk);
        Assert.Equal(new[] { "user", "assistant", "user", "system", "assistant", "user", "assistant", "system", "system", "assistant" }, lines.Select(Type));
        Assert.All(lines, l => Assert.True(l.GetProperty("history").GetBoolean()));
        Assert.Equal(("task_started", "call_old1", "File Lister"), (Str(lines[3], "subtype"), Str(lines[3], "tool_use_id"), Str(lines[3], "description")));
        Assert.Equal(("call_old1", "Shell", "2026-03-04T10:00:02.000Z"), (Parent(lines[4]), Str(Block(lines[4]), "name"), When(lines[4])));
        Assert.Equal(("call_old1", "a.txt"), (Parent(lines[5]), Str(Block(lines[5]), "content")));
        Assert.Equal(("call_old1", "One file."), (Parent(lines[6]), Said(lines[6])));
        Assert.Equal(("task_progress", 1), (Str(lines[7], "subtype"), lines[7].GetProperty("usage").GetProperty("tool_uses").GetInt32()));
        Assert.Equal(("task_notification", "completed"), (Str(lines[8], "subtype"), Str(lines[8], "status")));
        Assert.Equal("It found one file.", Said(lines[9]));

        // One that never ended its turn belonged to a process that is gone: stopped, not at work.
        disk.Kept[Sub1] = disk.Kept[Sub1].Take(2).ToList();
        var cut = Reopen(disk);
        Assert.Equal(("task_notification", "stopped"), (Str(cut[^2], "subtype"), Str(cut[^2], "status")));

        // Which subagent belongs to which call is not guessed: a call too many, or a role that is not the call's, and the replay is as it came.
        Assert.Equal(new[] { "user", "assistant", "user", "assistant", "user", "assistant" }, Reopen(disk, calls: 2).Select(Type));
        Assert.Equal(new[] { "user", "assistant", "user", "assistant" }, Reopen(disk, role: "Somebody Else").Select(Type));
        Assert.Equal(new[] { "user", "assistant", "user", "assistant" }, Reopen(new Disk()).Select(Type));
        // One whose file names no role goes under the one call there is.
        var plain = new Disk();
        plain.Helpers[AgyOther] = new() { new AgySubagent(Sub1, "", "self", Alive, 2) };
        plain.Kept[Sub1] = disk.Kept[Sub1];
        Assert.Contains(Reopen(plain), l => Type(l) == "system" && Str(l, "subtype") == "task_started");
        // Nor a subagent too many.
        disk.Helpers[AgyOther].Add(new AgySubagent(Sub2, "File Lister", "self", Alive, 5));
        Assert.Equal(new[] { "user", "assistant", "user", "assistant" }, Reopen(disk).Select(Type));
    }

    // ---- pages, calls and subagents at the edges ----

    static string? Under(JsonElement line) => line.TryGetProperty("parent_tool_use_id", out var under) && under.ValueKind == JsonValueKind.String ? under.GetString() : null;
    static bool Top(JsonElement line) => Type(line) is "user" or "assistant" && Under(line) == null;

    [Fact]
    public void A_page_begins_at_a_message_of_the_conversation_and_holds_the_call_of_every_subagent_line_in_it()
    {
        // A subagent that does more than a page holds, then the agent's word on it. A viewer moves on from a page by
        // the first message of the conversation itself in it, and shows a subagent's line only under a call it has.
        var (chat, record, _) = Delegated();
        var steps = new List<string>();
        for (int i = 0; i < 260; i++)
        {
            steps.Add(AgyDiskTests.Planned(2 * i + 1, "2026-03-04T10:00:02Z", calls: new[] { ("run_command", "CommandLine", "\"step " + i + "\"") }));
            steps.Add(AgyDiskTests.Outcome(2 * i + 2, "2026-03-04T10:00:03Z", "ok"));
        }
        chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(steps.ToArray()))), record, After(20));
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "It ran them all."))), record, After(30));
        var said = Kept(chat.Tick(record, After(33))).Single();

        var page = Earlier(chat, Str(said, "uuid"));
        var lines = page.GetProperty("lines").EnumerateArray().ToList();
        Assert.True(lines.Count > AcpChat.PageLines, "the subagent's lines are more than a page");
        Assert.True(Top(lines[0]));
        Assert.Equal(("call_sp1", "Agent"), (Str(lines[0], "uuid"), Str(Block(lines[0]), "name")));
        Assert.All(lines.Where(l => Type(l) != "system" && Under(l) != null), l => Assert.Equal("call_sp1", Under(l)));
        Assert.Equal(520, lines.Count(l => Under(l) == "call_sp1"));
        // And the page before it is the rest, to the start.
        var first = Earlier(chat, Str(lines[0], "uuid"));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("next").ValueKind);
        Assert.Equal("have a subagent list the files", Said(first.GetProperty("lines").EnumerateArray().First(l => Type(l) == "user")));
    }

    [Fact]
    public void A_page_of_subagent_lines_whose_call_is_no_longer_kept_reaches_back_to_the_start_and_ends_there()
    {
        // The past is kept up to a size. With the call gone from it, its subagent's lines are under nothing a viewer
        // can be given: the page takes all of them and says there is no more, so it is not asked for for ever.
        var (chat, record, _) = Delegated();
        chat.LogMost = 40_000;
        var steps = new List<string>();
        for (int i = 0; i < 300; i++)
        {
            steps.Add(AgyDiskTests.Planned(2 * i + 1, "2026-03-04T10:00:02Z", calls: new[] { ("run_command", "CommandLine", "\"step " + i + "\"") }));
            steps.Add(AgyDiskTests.Outcome(2 * i + 2, "2026-03-04T10:00:03Z", "ok"));
        }
        chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(steps.ToArray()))), record, After(20));
        chat.LogMost = 64L * 1024 * 1024;
        for (int i = 300; i < 520; i++)
            chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Planned(2 * i + 1, "2026-03-04T10:00:02Z", calls: new[] { ("run_command", "CommandLine", "\"step " + i + "\"") }), AgyDiskTests.Outcome(2 * i + 2, "2026-03-04T10:00:03Z", "ok")))), record, After(21));
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "It ran them all."))), record, After(30));
        var said = Kept(chat.Tick(record, After(33))).Single();

        var page = Earlier(chat, Str(said, "uuid"));
        var lines = page.GetProperty("lines").EnumerateArray().ToList();
        Assert.DoesNotContain(lines, l => Type(l) == "assistant" && Under(l) == null);
        Assert.True(lines.Count > AcpChat.PageLines);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next").ValueKind);
    }

    [Fact]
    public void A_reopened_conversation_never_begins_in_the_middle_of_what_a_subagent_did()
    {
        var disk = new Disk();
        disk.Helpers[AgyOther] = new() { new AgySubagent(Sub1, "File Lister", "self", Alive, 2) };
        var steps = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            steps.Add(AgyDiskTests.Planned(2 * i + 1, "2026-03-04T10:00:02Z", calls: new[] { ("run_command", "CommandLine", "\"step " + i + "\"") }));
            steps.Add(AgyDiskTests.Outcome(2 * i + 2, "2026-03-04T10:00:03Z", "ok"));
        }
        disk.Kept[Sub1] = StepsOf(steps.ToArray());

        var chat = new AcpChat("Agy", Cwd, "", "", "", "", budget: 3_000, disk: disk);
        var record = new HostRecord();
        var create = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), AgyInit)), record, T0)).Single();
        chat.FromCli(J(Response(Id(create), AgyNew)), record, T0);
        var load = Sent(chat.Resume("r1", AgyOther, "viewer-a")).Single();
        chat.FromCli(J(Update(AgyOther, Text("user_message_chunk", "have a subagent list the files"))), record, T0);
        chat.FromCli(J(Update(AgyOther, Spawning("call_old", "completed", "File Lister"))), record, T0);
        chat.FromCli(J(Update(AgyOther, Text("agent_message_chunk", "It ran them all."))), record, T0);
        var seed = Seeded(chat.FromCli(J(Response(Id(load), AgyLoaded)), record, T0));
        var shown = seed.Lines.Select(L).ToList();
        Assert.True(seed.Earlier);
        Assert.True(Top(shown[0]), "what is shown begins at a message of the conversation itself");
        Assert.Equal("It ran them all.", Said(shown[^1]));

        var page = Earlier(chat, Str(shown[0], "uuid")).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal("call_old", Str(page.First(l => Type(l) == "assistant"), "uuid"));
        Assert.Equal(60, page.Count(l => Type(l) != "system" && Under(l) == "call_old"));
    }

    [Fact]
    public void A_call_that_started_no_subagent_is_not_given_the_next_ones()
    {
        // It failed: the subagent that shows up next was started by the call after it.
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "a helper");
        chat.FromCli(J(Update(AgyId, Spawning("call_a", "in_progress", "Reader"))), record, After(1));
        chat.FromCli(J(Update(AgyId, Spawning("call_a", "failed", "Reader", update: true))), record, After(2));
        chat.FromCli(J(Update(AgyId, Spawning("call_b", "in_progress", "Reader"))), record, After(3));
        Assert.Equal("call_b", Str(Kept(chat.Outside(News(AgyId, (Sub1, "Reader", Alive, new List<AgyStep>())), record, After(4))).Single(), "tool_use_id"));

        // It ended well and nothing ever showed up for it (the file is there within two seconds of the call, measured):
        // a subagent seen minutes later is the next call's.
        var (late, lateRecord, _) = OpenWith("Agy");
        Prompt(late, lateRecord, "a helper");
        late.FromCli(J(Update(AgyId, Spawning("call_a", "in_progress", "Reader"))), lateRecord, After(1));
        late.FromCli(J(Update(AgyId, Spawning("call_a", "completed", "Reader", update: true))), lateRecord, After(2));
        late.FromCli(J(Update(AgyId, Spawning("call_b", "in_progress", "Reader"))), lateRecord, After(200));
        Assert.Equal("call_b", Str(Kept(late.Outside(News(AgyId, (Sub1, "Reader", Alive, new List<AgyStep>())), lateRecord, After(201))).Single(), "tool_use_id"));
    }

    [Fact]
    public void A_subagent_that_was_there_long_before_a_call_was_not_started_by_it()
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "a helper");
        // Nobody's: no call of this conversation asked for it.
        Assert.Empty(Kept(chat.Outside(News(AgyId, (Sub1, "Reader", Alive, StepsOf(AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", "Hello.")))), record, After(10))));
        // It goes on writing: how long it has been there is counted from when it was first seen.
        Assert.Empty(Kept(chat.Outside(News(AgyId, (Sub1, "Reader", Alive, StepsOf(AgyDiskTests.Planned(2, "2026-03-04T10:02:00Z", "Still nobody's.")))), record, After(150))));
        // Minutes later a call asks for one of that role, or for one of no role in particular: the old one is not it.
        Assert.Equal("Agent", Str(Block(Kept(chat.FromCli(J(Update(AgyId, Spawning("call_a", "in_progress", ""))), record, After(200))).Single()), "name"));
        Assert.Empty(Kept(chat.Outside(News(AgyId, (Sub1, "Reader", Alive, StepsOf(AgyDiskTests.Planned(3, "2026-03-04T10:03:00Z", "Still here.")))), record, After(201))));
        // Its own shows up, and is.
        var own = Kept(chat.Outside(News(AgyId, (Sub2, "", Alive, new List<AgyStep>())), record, After(202))).Single();
        Assert.Equal(("task_started", "call_a", Sub2), (Str(own, "subtype"), Str(own, "tool_use_id"), Str(own, "task_id")));
    }

    [Fact]
    public void A_move_that_fails_leaves_the_open_conversation_its_subagents_and_its_timers()
    {
        var (chat, record, _) = Delegated();
        chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, new List<AgyStep>())), record, After(10));
        Assert.Equal("scheduled", record.AgentStatus);
        var load = Sent(chat.Resume("r1", AgyOther, "viewer-a")).Single();
        // While the other conversation is being read, what the subagent does is still this one's.
        var during = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", calls: new[] { ("run_command", "CommandLine", "\"dir\"") })))), record, After(11)));
        Assert.Equal("call_sp1", Under(during[0]));
        var refused = chat.FromCli(J(Failure(Id(load), "no such conversation")), record, After(12));
        Assert.Contains(refused, e => e.Kind == ChatEffectKind.Reply);
        chat.Tick(record, After(13));
        Assert.Equal("scheduled", record.AgentStatus);
        var after = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(AgyDiskTests.Outcome(2, "2026-03-04T10:00:09Z", "a.txt")))), record, After(14)));
        Assert.Equal(("call_sp1", "a.txt"), (Under(after[0]), Str(Block(after[0]), "content")));

        // A timer the same.
        var (timed, timedRecord, _) = OpenWith("Agy");
        var turn = Prompt(timed, timedRecord, "in a minute, say it");
        timed.FromCli(J(Update(AgyId, Schedule("call_s1", "in_progress"))), timedRecord, After(3));
        timed.FromCli(J(Response(Id(turn), Stopped("end_turn"))), timedRecord, After(6));
        Assert.Equal("scheduled", timedRecord.AgentStatus);
        var move = Sent(timed.Resume("r1", AgyOther, "viewer-a")).Single();
        timed.FromCli(J(Update(AgyOther, Schedule("call_other", "in_progress"))), timedRecord, After(7));
        timed.FromCli(J(Failure(Id(move), "no such conversation")), timedRecord, After(8));
        timed.Tick(timedRecord, After(9));
        Assert.Equal("scheduled", timedRecord.AgentStatus);
        // And it still ends the wait when it fires.
        timed.FromCli(J(Update(AgyId, Schedule("call_s1", "completed", update: true))), timedRecord, After(60));
        Assert.Equal("busy", timedRecord.AgentStatus);
        Assert.Equal("result", Type(Kept(timed.Tick(timedRecord, After(91))).Single()));
        Assert.Equal("idle", timedRecord.AgentStatus);
    }

    [Fact]
    public void A_timer_of_the_conversation_moved_away_from_is_not_waited_on_in_the_one_moved_to()
    {
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "in a minute, say it");
        chat.FromCli(J(Update(AgyId, Schedule("call_s1", "in_progress"))), record, After(3));
        chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(6));
        Assert.Equal("scheduled", record.AgentStatus);
        var load = Sent(chat.Resume("r1", AgyOther, "viewer-a")).Single();
        chat.FromCli(J(Response(Id(load), AgyLoaded)), record, After(8));
        Assert.Equal("idle", record.AgentStatus);
        chat.Tick(record, After(9));
        Assert.Equal("idle", record.AgentStatus);
    }

    [Fact]
    public void A_message_that_came_to_nothing_is_still_ended_for_the_viewers_that_saw_it_begin()
    {
        // Antigravity can start a reply with a blank line and go on seconds later. Viewers were told a message began;
        // with nothing to end it, the words that follow were shown in its place and again in their own.
        var (chat, record) = AfterATurn();
        var begun = Passed(chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "\n\n"))), record, After(40)));
        Assert.Contains(begun, l => Type(l) == "stream_event" && Str(l.GetProperty("event"), "type") == "content_block_start");
        var ended = Kept(chat.Tick(record, After(43))).Single();
        Assert.Equal(("assistant", "text", "\n\n"), (Type(ended), Str(Block(ended), "type"), Str(Block(ended), "text")));
        var next = chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "The answer."))), record, After(46));
        Assert.Contains(Passed(next), l => Type(l) == "stream_event" && Str(l.GetProperty("event"), "type") == "content_block_start");
        Assert.Equal("The answer.", Said(Kept(chat.Tick(record, After(49))).Single()));

        // A thought that is only white space, ended by what follows it.
        var (thinking, thinker, _) = OpenWith("Agy");
        Prompt(thinking, thinker, "go");
        thinking.FromCli(J(Update(AgyId, Text("agent_thought_chunk", " "))), thinker, After(1));
        var closed = Kept(thinking.FromCli(J(Update(AgyId, Text("agent_message_chunk", "Done."))), thinker, After(2))).Single();
        Assert.Equal(("thinking", ""), (Str(Block(closed), "type"), Str(Block(closed), "thinking")));
    }

    [Fact]
    public void A_blank_message_read_back_from_an_earlier_process_is_left_out()
    {
        // Nobody was told it began, so nothing has to end it, and a pane would show nothing for it.
        var (chat, record, _) = OpenWith("OpenCode");
        var load = Sent(chat.Resume("r1", OcOther, "viewer-a")).Single();
        foreach (string update in new[] { Text("user_message_chunk", "first"), Text("agent_message_chunk", " \n"), Text("agent_thought_chunk", "  "), Text("user_message_chunk", "second"), Text("agent_message_chunk", "an answer") })
            chat.FromCli(J(Update(OcOther, update)), record, T0);
        var lines = Seeded(chat.FromCli(J(Response(Id(load), OcConfig())), record, T0)).Lines.Select(L).ToList();
        Assert.Equal(new[] { "first", "second", "an answer" }, lines.Select(Said));
    }

    [Fact]
    public void A_replay_that_goes_on_after_the_agent_said_the_conversation_was_open_is_still_the_past()
    {
        // Measured on Antigravity 1.5.13 with a conversation of 2,374 messages: the answer to session/load came with
        // 1,073 of them replayed, and the other 1,301 followed over the next half second. They are not being said now.
        var disk = new Disk();
        disk.Kept[AgyOther] = StepsOf(
            AgyDiskTests.Asked(0, "2026-03-04T10:00:05Z", "question 1"), AgyDiskTests.Planned(1, "2026-03-04T10:00:07Z", "answer 1"),
            AgyDiskTests.Asked(2, "2026-03-04T18:30:00Z", "question 2"), AgyDiskTests.Planned(3, "2026-03-04T18:30:09Z", "answer 2", calls: new[] { ("run_command", "CommandLine", "\"dir\"") }));
        var chat = new AcpChat("Agy", Cwd, "", "", "", "", disk: disk);
        var record = new HostRecord();
        var create = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start()).Single()), AgyInit)), record, T0)).Single();
        chat.FromCli(J(Response(Id(create), AgyNew)), record, T0);
        var load = Sent(chat.Resume("r1", AgyOther, "viewer-a")).Single();
        chat.FromCli(J(Update(AgyOther, Text("user_message_chunk", "question 1"))), record, T0);
        chat.FromCli(J(Update(AgyOther, Text("agent_message_chunk", "answer 1"))), record, T0);
        var opened = chat.FromCli(J(Response(Id(load), AgyLoaded)), record, T0);
        Assert.Equal(new[] { "question 1", "answer 1" }, Seeded(opened).Lines.Select(L).Select(Said));

        // The rest of it arrives: nothing is shown as being said, nothing is at work, and nothing is kept as said now.
        var more = new List<ChatEffect>();
        more.AddRange(chat.FromCli(J(Update(AgyOther, Text("user_message_chunk", "question 2"))), record, T0.AddMilliseconds(100)));
        more.AddRange(chat.FromCli(J(Update(AgyOther, Text("agent_message_chunk", "answer 2"))), record, T0.AddMilliseconds(200)));
        more.AddRange(chat.FromCli(J(Update(AgyOther, AgyRun)), record, T0.AddMilliseconds(300)));
        more.AddRange(chat.FromCli(J(Update(AgyOther, """{"sessionUpdate":"usage_update","used":4321,"size":1048576}""")), record, T0.AddMilliseconds(300)));
        Assert.Empty(more);
        Assert.Equal("idle", record.AgentStatus);
        // A message typed meanwhile waits: what follows a prompt sent now could not be told from the past still arriving.
        Assert.Empty(chat.FromViewer(J(UserSays("and now?")), record, T0.AddMilliseconds(400)));
        Assert.Empty(chat.Tick(record, T0.AddMilliseconds(900)));
        // Quiet is counted from the last of it, and a message still arriving is not taken to be said and done.
        Assert.Empty(chat.FromCli(J(Update(AgyOther, Text("agent_message_chunk", "the last of it"))), record, T0.AddMilliseconds(950)));
        Assert.Empty(chat.Tick(record, T0.AddMilliseconds(1500)));

        // It has gone quiet: the rest is kept as the past it is, with its own times, and then the message is sent.
        var ended = chat.Tick(record, After(2));
        var kept = Kept(ended);
        Assert.Equal(("the last of it", true), (Said(kept[3]), kept[3].GetProperty("history").GetBoolean()));
        kept.RemoveAt(3);
        Assert.Equal(new[] { "user", "assistant", "assistant", "user" }, kept.Take(4).Select(Type));
        Assert.Equal(("question 2", "answer 2", "Shell"), (Said(kept[0]), Said(kept[1]), Str(Block(kept[2]), "name")));
        Assert.All(kept.Take(3), l => Assert.True(l.GetProperty("history").GetBoolean()));
        Assert.Equal(new string?[] { "2026-03-04T18:30:00.000Z", "2026-03-04T18:30:09.000Z", null }, kept.Take(3).Select(When));
        Assert.Empty(Passed(ended).Where(l => Type(l) == "stream_event"));
        Assert.Equal("and now?", Said(kept[3]));
        Assert.Equal("session/prompt", Method(Sent(ended).Single()));
        Assert.Equal((4321L, "busy"), (record.ContextTokens, record.AgentStatus));
        // From here on what the agent says is said now.
        Assert.Contains(Passed(chat.FromCli(J(Update(AgyOther, Text("agent_message_chunk", "Now this."))), record, After(3))), l => Type(l) == "stream_event");
        // And an earlier page holds the whole of the past, in order.
        Assert.Equal(new[] { "question 1", "answer 1", "question 2", "answer 2" }, Earlier(chat, Str(kept[3], "uuid")).GetProperty("lines").EnumerateArray().Where(l => Type(l) != "assistant" || Str(Block(l), "type") == "text").Take(4).Select(Said));
    }

    // ---- work that goes on with nothing said: commands in the background, and long silences ----

    static string Running(string id, string status, bool update = false) =>
        "{\"sessionUpdate\":\"" + (update ? "tool_call_update" : "tool_call") + "\",\"toolCallId\":\"" + id + "\",\"name\":\"run_command\",\"status\":\"" + status + "\",\"kind\":\"execute\",\"title\":\"a long job\",\"rawInput\":{\"CommandLine\":\"a long job\"}" + (status == "completed" ? ",\"rawOutput\":{\"output\":\"done\",\"exit_code\":0}" : "") + "}";

    static string[] StillRunning(JsonElement result) =>
        result.TryGetProperty("running", out var ids) ? ids.EnumerateArray().Select(i => i.GetString()!).ToArray() : Array.Empty<string>();

    [Fact]
    public void A_turn_that_ends_with_commands_still_running_is_waiting_on_them_and_goes_on_when_each_reports()
    {
        // Measured on Antigravity 1.5.13: three commands sent to the background, the turn ended at once with their calls
        // still open, and each call reported two to six minutes later, after which the agent spoke again each time.
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "run the three reviewers");
        chat.FromCli(J(Update(AgyId, Running("call_a", "in_progress"))), record, After(1));
        chat.FromCli(J(Update(AgyId, Running("call_b", "in_progress"))), record, After(2));
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "I will wait for both."))), record, After(3));
        var ended = Kept(chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(4)));
        // The end of the turn says which calls are still going, so a pane does not show them as cut off.
        Assert.Equal(new[] { "call_a", "call_b" }, StillRunning(ended.Single(l => Type(l) == "result")));
        Assert.Equal(("scheduled", "Stop"), (record.AgentStatus, record.LastEvent));
        Assert.Empty(chat.Tick(record, After(100)));
        Assert.Equal("scheduled", record.AgentStatus);

        // One reports: the agent is about to go on, and does.
        var back = chat.FromCli(J(Update(AgyId, Running("call_a", "completed", update: true))), record, After(130));
        Assert.Equal("busy", record.AgentStatus);
        Assert.Contains(Passed(back), l => Type(l) == "system" && Str(l, "subtype") == "init");
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "The first is done."))), record, After(140));
        Assert.Equal("The first is done.", Said(Kept(chat.Tick(record, After(142))).Single()));
        // It has said its piece and the other is still going: waiting again, not done.
        var paused = Kept(chat.Tick(record, After(171))).Single();
        Assert.Equal(("result", "call_b"), (Type(paused), StillRunning(paused).Single()));
        Assert.Equal("scheduled", record.AgentStatus);

        chat.FromCli(J(Update(AgyId, Running("call_b", "completed", update: true))), record, After(300));
        Assert.Equal("busy", record.AgentStatus);
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "Both are done."))), record, After(305));
        chat.Tick(record, After(307));
        var done = Kept(chat.Tick(record, After(336))).Single();
        Assert.Empty(StillRunning(done));
        Assert.Equal("idle", record.AgentStatus);
    }

    [Fact]
    public void Work_that_goes_silent_after_a_call_is_still_work_until_the_agent_has_spoken()
    {
        // Measured: nine edits came back, then nothing for 5 minutes 24 seconds (the agent was compacting its context),
        // then it went on. What a call came back with is always answered: silence after it is the model at work.
        var (chat, record) = AfterATurn();
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "One more thing."))), record, After(40));
        chat.FromCli(J(Update(AgyId, AgyRun)), record, After(41));
        chat.FromCli(J(Update(AgyId, AgyRunDone)), record, After(43));
        Assert.Equal("busy", record.AgentStatus);
        foreach (double later in new[] { 75, 200, 366, 600 })
        {
            Assert.DoesNotContain(Kept(chat.Tick(record, After(later))), l => Type(l) == "result");
            Assert.Equal("busy", record.AgentStatus);
        }
        // A thought is not the end of it either.
        chat.FromCli(J(Update(AgyId, Text("agent_thought_chunk", "Now the summary."))), record, After(610));
        Assert.DoesNotContain(Kept(chat.Tick(record, After(700))), l => Type(l) == "result");
        // Its words are: 30 quiet seconds after them the work is over.
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "All done."))), record, After(710));
        chat.Tick(record, After(712));
        Assert.Empty(chat.Tick(record, After(739)));
        Assert.Equal("result", Type(Kept(chat.Tick(record, After(740))).Single()));
        Assert.Equal("idle", record.AgentStatus);

        // A thought after its words: it is not done, though it has made no call.
        var (thinking, thinker) = AfterATurn();
        thinking.FromCli(J(Update(AgyId, Text("agent_message_chunk", "Let me check one thing."))), thinker, After(40));
        thinking.FromCli(J(Update(AgyId, Text("agent_thought_chunk", "Is the count right?"))), thinker, After(41));
        Assert.DoesNotContain(Kept(thinking.Tick(thinker, After(44))), l => Type(l) == "result");
        Assert.Empty(thinking.Tick(thinker, After(120)));
        Assert.Equal("busy", thinker.AgentStatus);

        // An agent that never speaks again is not waited on for ever.
        var (silent, stuck) = AfterATurn();
        silent.FromCli(J(Update(AgyId, AgyRun)), stuck, After(41));
        silent.FromCli(J(Update(AgyId, AgyRunDone)), stuck, After(43));
        Assert.Empty(silent.Tick(stuck, After(43 + 599)));
        Assert.Equal("result", Type(Kept(silent.Tick(stuck, After(43 + 600))).Single()));
        Assert.Equal("idle", stuck.AgentStatus);
    }

    [Fact]
    public void A_call_that_ends_a_moment_after_its_turn_or_in_a_turn_that_was_stopped_is_not_work_in_the_background()
    {
        // The end of the call and the end of the turn can cross: nothing follows from that.
        var (chat, record, _) = OpenWith("Agy");
        var turn = Prompt(chat, record, "run it");
        chat.FromCli(J(Update(AgyId, Running("call_a", "in_progress"))), record, After(1));
        chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(2));
        Assert.Empty(Passed(chat.FromCli(J(Update(AgyId, Running("call_a", "completed", update: true))), record, After(2.5))));
        chat.Tick(record, After(3));
        Assert.Equal("idle", record.AgentStatus);

        // A turn the person stopped: its calls were cut off, and are not waited on.
        var (stopped, stoppedRecord, _) = OpenWith("Agy");
        var cut = Prompt(stopped, stoppedRecord, "run it");
        stopped.FromCli(J(Update(AgyId, Running("call_a", "in_progress"))), stoppedRecord, After(1));
        stopped.FromViewer(J(Interrupt("i1")), stoppedRecord, After(2));
        var end = Kept(stopped.FromCli(J(Response(Id(cut), Stopped("cancelled"))), stoppedRecord, After(3)));
        Assert.Empty(StillRunning(end.Single(l => Type(l) == "result")));
        Assert.Equal("idle", stoppedRecord.AgentStatus);
        stopped.Tick(stoppedRecord, After(10));
        Assert.Equal("idle", stoppedRecord.AgentStatus);

        // And a command is not waited on for ever: one that has not reported in two hours is taken to be lost.
        var (old, oldRecord, _) = OpenWith("Agy");
        var long_ = Prompt(old, oldRecord, "run it");
        old.FromCli(J(Update(AgyId, Running("call_a", "in_progress"))), oldRecord, After(1));
        old.FromCli(J(Response(Id(long_), Stopped("end_turn"))), oldRecord, After(2));
        old.Tick(oldRecord, After(7199));
        Assert.Equal("scheduled", oldRecord.AgentStatus);
        old.Tick(oldRecord, After(7202));
        Assert.Equal("idle", oldRecord.AgentStatus);
    }

    [Fact]
    public void No_two_lines_of_a_conversation_have_one_id()
    {
        // A message that carries an id and is kept in two parts (said, a pause, said on): a viewer names a line by its id.
        var (chat, record) = AfterATurn("OpenCode");
        chat.FromCli(J(Update(OcId, Text("agent_message_chunk", "One more thing.", "msg_late"))), record, After(40));
        var first = Kept(chat.Tick(record, After(43))).Single();
        chat.FromCli(J(Update(OcId, Text("agent_message_chunk", "And another.", "msg_late"))), record, After(44));
        var second = Kept(chat.Tick(record, After(47))).Single();
        Assert.Equal(("One more thing.", "And another."), (Said(first), Said(second)));
        Assert.NotEqual(Str(first, "uuid"), Str(second, "uuid"));
        Assert.Equal("One more thing.", Said(Earlier(chat, Str(second, "uuid")).GetProperty("lines").EnumerateArray().Last(l => Type(l) == "assistant")));

        // A subagent's steps that say no place of their own.
        var (helped, helpedRecord, _) = Delegated();
        var lines = Kept(helped.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(
            "{\"type\":\"PLANNER_RESPONSE\",\"content\":\"First.\",\"tool_calls\":[{\"name\":\"run_command\",\"args\":{\"CommandLine\":\"\\\"dir\\\"\"}}]}",
            "{\"type\":\"GENERIC\",\"content\":\"ok\"}",
            "{\"type\":\"PLANNER_RESPONSE\",\"content\":\"Second.\",\"tool_calls\":[{\"name\":\"run_command\",\"args\":{\"CommandLine\":\"\\\"dir\\\"\"}}]}"))), helpedRecord, After(10)));
        var ids = lines.Where(l => Type(l) != "system").Select(l => Str(l, "uuid")).ToList();
        Assert.Equal(5, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void What_a_subagents_call_came_back_with_belongs_to_a_call_of_the_step_before_it()
    {
        // A call with nothing written for what it came back with: the next step's result is not it.
        var (chat, record, _) = Delegated();
        var lines = Kept(chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, StepsOf(
            AgyDiskTests.Planned(1, "2026-03-04T10:00:02Z", calls: new[] { ("run_command", "CommandLine", "\"first\"") }),
            AgyDiskTests.Planned(2, "2026-03-04T10:00:03Z", calls: new[] { ("run_command", "CommandLine", "\"second\"") }),
            AgyDiskTests.Outcome(3, "2026-03-04T10:00:04Z", "from the second")))), record, After(10)));
        var calls = lines.Where(l => Type(l) == "assistant").ToList();
        var result = lines.Single(l => Type(l) == "user");
        Assert.Equal(new[] { "first", "second" }, calls.Select(c => Str(Block(c).GetProperty("input"), "command")));
        Assert.Equal(Str(Block(calls[1]), "id"), Str(Block(result), "tool_use_id"));
    }

    [Fact]
    public void A_page_with_word_of_a_subagents_task_holds_the_call_it_is_about()
    {
        // The subagent is started, a long stretch of conversation follows, and only then is it said to be over: a viewer
        // can do nothing with that word without the call it is about.
        var (chat, record, _) = Delegated();
        chat.Outside(News(AgyId, (Sub1, "File Lister", Alive, new List<AgyStep>())), record, After(10));
        for (int i = 1; i <= 150; i++)
        {
            var turn = Prompt(chat, record, $"question {i}");
            chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", $"answer {i}"))), record, After(20));
            chat.FromCli(J(Response(Id(turn), Stopped("end_turn"))), record, After(20));
        }
        chat.Outside(News(AgyId, (Sub1, "File Lister", "SUBAGENT_STATE_KILLED", new List<AgyStep>())), record, After(30));
        var last = Prompt(chat, record, "and then?");
        chat.FromCli(J(Update(AgyId, Text("agent_message_chunk", "That is all."))), record, After(31));
        var said = Kept(chat.FromCli(J(Response(Id(last), Stopped("end_turn"))), record, After(32)))[0];

        var lines = Earlier(chat, Str(said, "uuid")).GetProperty("lines").EnumerateArray().ToList();
        Assert.Contains(lines, l => Type(l) == "system" && Str(l, "subtype") == "task_notification");
        Assert.Equal("call_sp1", Str(lines[0], "uuid"));
    }
}
