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
        Assert.Equal(new[] { ("user", "msg_1d5188c07001fdiBvEW46G1J9u"), ("assistant", "msg_1d5188c24001bKuq4rxATpy9ex"), ("assistant", "call_b9ca73159a9d4bf9862d79cc"), ("user", "call_b9ca73159a9d4bf9862d79cc"), ("assistant", "msg_1d518aac6001wQC1v4NzTuijhq") },
            lines.Select(l => (Type(l), Str(l, "uuid"))));
        Assert.All(lines, l => Assert.True(l.GetProperty("history").GetBoolean()));
        Assert.Equal(("Do these in order, one tool call each.", "I'll do these three steps in order.", "done"), (Said(lines[0]), Said(lines[1]), Said(lines[4])));
        Assert.Equal(("tool_use", "Shell", "echo acp-lab-one"), (Str(Block(lines[2]), "type"), Str(Block(lines[2]), "name"), Str(Block(lines[2]).GetProperty("input"), "command")));
        Assert.Equal(("tool_result", "acp-lab-one\n", false), (Str(Block(lines[3]), "type"), Str(Block(lines[3]), "content"), Block(lines[3]).GetProperty("is_error").GetBoolean()));
        Assert.Equal((OcId, "Do these in order, one tool call each."), (record.SessionId, record.Title));

        var prompt = Sent(loaded).Single(m => Method(m) == "session/prompt");
        Assert.Equal(("session/prompt", OcId, "what next"), (Method(prompt), Str(Params(prompt), "sessionId"), Str(Params(prompt).GetProperty("prompt")[0], "text")));
        int resetAt = loaded.IndexOf(reset), userAt = loaded.FindIndex(e => e.Kind == ChatEffectKind.Durable && Type(Of(e)) == "user");
        Assert.True(resetAt < userAt, "the message held during the load must land after the scrollback is reset");
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
        Assert.Equal(("user", "call_b9ca73159a9d4bf9862d79cc"), (Type(result), Str(result, "uuid")));
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

    [Fact]
    public void Antigravitys_calls_are_read_by_meaning_from_its_own_input_names()
    {
        var (chat, record, _) = OpenWith("Agy");
        Prompt(chat, record, "Do these in order, one tool call each.");
        var run = Block(Kept(chat.FromCli(J(Update(AgyId, AgyRun)), record, T0)).Single());
        Assert.Equal(("Shell", "echo acp-lab-one"), (Str(run, "name"), Str(run.GetProperty("input"), "command")));
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
        var next = Sent(loaded).Single();
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
}
