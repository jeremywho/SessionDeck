using System.IO;
using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>What a chat host reads out of the CLI's stream-json lines. The lines are shaped like Claude Code 2.1.322's, cut to the fields that matter.</summary>
public class ChatStreamTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-chatstream-" + Guid.NewGuid().ToString("N"));
    static readonly DateTime T0 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    public ChatStreamTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static JsonElement Line(string json) => JsonDocument.Parse(json).RootElement.Clone();

    const string Init = """{"type":"system","subtype":"init","session_id":"live-1","model":"claude-haiku-5-5","permissionMode":"default"}""";
    const string Echo = """{"type":"user","isReplay":true,"message":{"role":"user","content":"  fix the\tflaky   test in the gateway and then tell me what was wrong with it  \nsecond line"}}""";
    const string ToolUse = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Write","input":{}}]}}""";
    const string Result = """{"type":"result","subtype":"success","is_error":false}""";
    static string Ask(string id) => """{"type":"control_request","request_id":"ID","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{},"tool_use_id":"t-ID"}}""".Replace("ID", id);
    static string Answer(string id) => """{"type":"control_response","response":{"subtype":"success","request_id":"ID","response":{"behavior":"allow","updatedInput":{}}}}""".Replace("ID", id);

    [Fact]
    public void A_turn_reads_as_busy_then_idle_and_names_the_session_and_the_model()
    {
        var record = new HostRecord { AgentStatus = "idle", StatusAt = T0 };
        var chat = new ChatStream();

        Assert.True(chat.Apply(Line(Init), record, T0.AddSeconds(1)).Record);
        Assert.Equal(("busy", "live-1", "claude-haiku-5-5"), (record.AgentStatus, record.SessionId, record.Model));
        Assert.Equal(T0.AddSeconds(1), record.StatusAt);

        Assert.True(chat.Apply(Line(Echo), record, T0.AddSeconds(2)).Record);
        Assert.Equal(("UserPromptSubmit", ""), (record.LastEvent, record.Title));
        Assert.Equal(T0.AddSeconds(1), record.StatusAt);

        Assert.True(chat.Apply(Line(ToolUse), record, T0.AddSeconds(3)).Record);
        Assert.Equal("Write", record.LastTool);
        Assert.False(chat.Apply(Line(ToolUse), record, T0.AddSeconds(4)).Record);

        Assert.True(chat.Apply(Line(Result), record, T0.AddSeconds(5)).Record);
        Assert.Equal(("idle", "Stop"), (record.AgentStatus, record.LastEvent));
        Assert.Equal(T0.AddSeconds(5), record.StatusAt);
    }

    [Fact]
    public void The_tab_takes_the_name_the_cli_gives_the_session()
    {
        var record = new HostRecord { Title = "billing" };
        var chat = new ChatStream();
        Assert.True(chat.Apply(Line("""{"type":"system","subtype":"session_title_changed","title":"billing rework"}"""), record, T0).Record);
        Assert.Equal("billing rework", record.Title);
        Assert.False(chat.Apply(Line("""{"type":"system","subtype":"session_title_changed","title":""}"""), record, T0).Record);
        Assert.Equal("billing rework", record.Title);
    }

    [Fact]
    public void The_session_waits_while_any_permission_request_is_open()
    {
        var record = new HostRecord { AgentStatus = "busy" };
        var chat = new ChatStream();
        chat.Apply(Line(Ask("r1")), record, T0);
        chat.Apply(Line(Ask("r2")), record, T0);
        Assert.Equal(("waiting", "Bash", "PermissionRequest"), (record.AgentStatus, record.LastTool, record.LastEvent));

        chat.Apply(Line(Answer("r1")), record, T0);
        Assert.Equal("waiting", record.AgentStatus);
        chat.Apply(Line(ToolUse), record, T0);
        Assert.Equal("waiting", record.AgentStatus);

        chat.Apply(Line("""{"type":"control_cancel_request","request_id":"r2"}"""), record, T0);
        Assert.Equal("busy", record.AgentStatus);

        chat.Apply(Line(Ask("r3")), record, T0);
        chat.Apply(Line(Result), record, T0);
        Assert.Equal("idle", record.AgentStatus);
        chat.Apply(Line(Init), record, T0);
        Assert.Equal("busy", record.AgentStatus);
    }

    [Fact]
    public void An_answer_to_something_else_changes_nothing()
    {
        var record = new HostRecord { AgentStatus = "idle" };
        Assert.False(new ChatStream().Apply(Line(Answer("interrupt-1")), record, T0).Record);
        Assert.Equal("idle", record.AgentStatus);
    }

    [Theory]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta"}}""", false)]
    [InlineData("""{"type":"keep_alive"}""", false)]
    [InlineData(Init, false)]
    [InlineData("""{"type":"system","subtype":"status","status":"requesting"}""", false)]
    [InlineData("""{"type":"system","subtype":"hook_response","hook_event":"SessionStart"}""", false)]
    [InlineData("""{"type":"system","subtype":"session_title_changed","title":"x"}""", false)]
    [InlineData("""{"type":"system","subtype":"compact_boundary"}""", true)]
    [InlineData(Echo, true)]
    [InlineData(ToolUse, true)]
    [InlineData(Result, true)]
    [InlineData("""{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool"}}""", true)]
    [InlineData("""{"type":"something_new"}""", true)]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"sd-set-1","response":{"mode":"plan"}}}""", false)]
    public void Only_the_conversation_itself_is_kept_for_a_later_viewer(string json, bool kept) =>
        Assert.Equal(kept, new ChatStream().Durable(Line(json)));

    [Fact]
    public void An_answer_is_part_of_the_conversation_only_when_it_settles_a_permission_request()
    {
        var chat = new ChatStream();
        var record = new HostRecord { AgentStatus = "busy" };
        Assert.False(chat.Durable(Line(Answer("r1"))));
        chat.Apply(Line(Ask("r1")), record, T0);
        Assert.True(chat.Durable(Line(Answer("r1"))));
        chat.Apply(Line(Answer("r1")), record, T0);
        Assert.False(chat.Durable(Line(Answer("r1"))));
    }

    const string Introduction = """
        {"type":"control_response","response":{"subtype":"success","request_id":"sd-initialize","response":{
          "current_permission_mode":"bypassPermissions","account":{"email":"someone@example.com"},
          "models":[
            {"value":"default","resolvedModel":"claude-opus-5-5","displayName":"Default (recommended)","description":"Use the default model","supportedEffortLevels":["low","medium","high"],"supportsFastMode":true},
            {"value":"haiku","resolvedModel":"claude-haiku-5-5","displayName":"Haiku","description":"Fast","supportedEffortLevels":[]},
            {"displayName":"no value"}],
          "commands":[
            {"name":"compact","description":"Clear history but keep a summary","argumentHint":"<optional instructions>","builtin":true},
            {"name":"__remote-workflow","description":"internal","argumentHint":""},
            {"name":"council","description":"LONG","argumentHint":""}]}}}
        """;

    [Fact]
    public void The_cli_introduces_itself_once_and_viewers_get_its_models_and_commands_cut_to_what_they_show()
    {
        var chat = new ChatStream();
        var record = new HostRecord();
        Assert.Equal("", chat.CatalogFrame());
        var introduction = Line(Introduction.Replace("LONG", new string('x', 400)));
        Assert.True(ChatStream.AnswersTheHost(introduction));
        Assert.False(chat.Durable(introduction));
        var change = chat.Apply(introduction, record, T0);
        Assert.Equal((false, true, true, false), (change.Record, change.Catalog, change.Settings, change.Refresh));

        var catalog = Line(chat.CatalogFrame());
        Assert.Equal("catalog", catalog.GetProperty("type").GetString());
        var models = catalog.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(new[] { "default", "haiku" }, models.Select(m => m.GetProperty("value").GetString()));
        Assert.Equal(("claude-opus-5-5", "Default (recommended)", "Use the default model"),
            (models[0].GetProperty("resolved").GetString(), models[0].GetProperty("name").GetString(), models[0].GetProperty("description").GetString()));
        Assert.Equal(new[] { "low", "medium", "high" }, models[0].GetProperty("efforts").EnumerateArray().Select(e => e.GetString()));
        Assert.False(models[0].TryGetProperty("supportsFastMode", out _));
        var commands = catalog.GetProperty("commands").EnumerateArray().ToList();
        Assert.Equal(new[] { "compact", "council" }, commands.Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("<optional instructions>", commands[0].GetProperty("hint").GetString());
        Assert.Equal(240, commands[1].GetProperty("description").GetString()!.Length);
        Assert.DoesNotContain("someone@example.com", chat.CatalogFrame());

        Assert.Equal("bypassPermissions", Line(chat.SettingsFrame()).GetProperty("mode").GetString());
        Assert.False(chat.Apply(introduction, record, T0).Catalog);
    }

    [Fact]
    public void The_settings_in_force_follow_the_cli_and_the_host_asks_again_when_they_may_have_moved()
    {
        var chat = new ChatStream();
        var record = new HostRecord { AgentStatus = "idle" };
        (string Mode, string Model, string Effort) Settings()
        {
            var f = Line(chat.SettingsFrame());
            return (f.GetProperty("mode").GetString()!, f.GetProperty("model").GetString()!, f.GetProperty("effort").GetString()!);
        }
        Assert.Equal(("", "", ""), Settings());

        var applied = Line("""{"type":"control_response","response":{"subtype":"success","request_id":"sd-settings","response":{"effective":{"hooks":{}},"applied":{"model":"claude-haiku-5-5","effort":"medium"}}}}""");
        Assert.True(ChatStream.AnswersTheHost(applied));
        var change = chat.Apply(applied, record, T0);
        Assert.Equal((true, false, true, false), (change.Record, change.Catalog, change.Settings, change.Refresh));
        Assert.Equal(("", "claude-haiku-5-5", "medium"), Settings());
        Assert.Equal("claude-haiku-5-5", record.Model);

        // A viewer switched the mode: the answer names it, and the host asks what else is in force.
        var set = Line("""{"type":"control_response","response":{"subtype":"success","request_id":"sd-set-7","response":{"mode":"plan"}}}""");
        Assert.False(ChatStream.AnswersTheHost(set));
        change = chat.Apply(set, record, T0);
        Assert.Equal((true, true), (change.Settings, change.Refresh));
        Assert.Equal("plan", Settings().Mode);

        // A refused switch changes nothing and asks nothing.
        change = chat.Apply(Line("""{"type":"control_response","response":{"subtype":"error","request_id":"sd-set-8","error":"no such model"}}"""), record, T0);
        Assert.Equal((false, false), (change.Settings, change.Refresh));

        // The CLI says "Set model to …" as if it had been typed; that is no turn.
        change = chat.Apply(Line("""{"type":"user","isReplay":true,"message":{"role":"user","content":"<local-command-stdout>Set model to `sonnet`</local-command-stdout>"}}"""), record, T0);
        Assert.False(change.Record);
        Assert.Equal("idle", record.AgentStatus);

        // Each turn says its model and mode as it starts, and its end is when a typed /model or /effort is picked up.
        chat.Apply(Line("""{"type":"system","subtype":"init","session_id":"s","model":"claude-sonnet-5-5","permissionMode":"acceptEdits"}"""), record, T0);
        Assert.Equal(("acceptEdits", "claude-sonnet-5-5", "medium"), Settings());
        Assert.True(chat.Apply(Line(Result), record, T0).Refresh);
    }

    [Fact]
    public void A_plan_is_approved_back_into_the_mode_the_session_left_and_the_answer_itself_says_which()
    {
        var chat = new ChatStream();
        var record = new HostRecord { AgentStatus = "idle" };
        (string Mode, string Resume) Modes()
        {
            var f = Line(chat.SettingsFrame());
            return (f.GetProperty("mode").GetString()!, f.GetProperty("resume").GetString()!);
        }
        Assert.Equal(("", ""), Modes());

        chat.Apply(Line("""{"type":"control_response","response":{"subtype":"success","request_id":"sd-initialize","response":{"current_permission_mode":"bypassPermissions","models":[],"commands":[]}}}"""), record, T0);
        Assert.Equal(("bypassPermissions", "bypassPermissions"), Modes());

        // Into plan mode: the mode to come back to is still the one it left.
        Assert.True(chat.Apply(Line("""{"type":"control_response","response":{"subtype":"success","request_id":"sd-set-1","response":{"mode":"plan"}}}"""), record, T0).Settings);
        Assert.Equal(("plan", "bypassPermissions"), Modes());
        chat.Apply(Line("""{"type":"system","subtype":"init","session_id":"s","model":"m","permissionMode":"plan"}"""), record, T0);
        Assert.Equal(("plan", "bypassPermissions"), Modes());

        // The plan is approved "and accept edits": the CLI is in that mode from the answer on, before any new turn says so.
        chat.Apply(Line("""{"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{"plan":"x"},"tool_use_id":"t1"}}"""), record, T0);
        var approved = chat.Apply(Line("""{"type":"control_response","response":{"subtype":"success","request_id":"p1","response":{"behavior":"allow","updatedInput":{"plan":"x"},"updatedPermissions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}]}}}"""), record, T0);
        Assert.True(approved.Settings);
        Assert.Equal(("acceptEdits", "acceptEdits"), Modes());

        // An answer that names no mode, a rule that is not a mode, and the same answer seen again change nothing.
        chat.Apply(Line(Ask("r2")), record, T0);
        Assert.False(chat.Apply(Line(Answer("r2")), record, T0).Settings);
        chat.Apply(Line(Ask("r3")), record, T0);
        Assert.False(chat.Apply(Line("""{"type":"control_response","response":{"subtype":"success","request_id":"r3","response":{"behavior":"allow","updatedInput":{},"updatedPermissions":[{"type":"addRules","rules":[{"toolName":"Bash"}],"behavior":"allow","destination":"session"}]}}}"""), record, T0).Settings);
        Assert.False(chat.Apply(Line("""{"type":"control_response","response":{"subtype":"success","request_id":"p1","response":{"behavior":"allow","updatedPermissions":[{"type":"setMode","mode":"default","destination":"session"}]}}}"""), record, T0).Settings);
        Assert.Equal(("acceptEdits", "acceptEdits"), Modes());
    }

    [Fact]
    public void An_images_bytes_stay_out_of_the_scrollback_and_everything_else_is_left_as_it_was()
    {
        string data = new string('A', 5000);
        var typed = Encoding.UTF8.GetBytes("""{"type":"user","isReplay":true,"message":{"role":"user","content":[{"type":"text","text":"what is this \"base64\" thing"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"DATA"}}]}}""".Replace("DATA", data));
        var stripped = Line(Encoding.UTF8.GetString(ChatStream.WithoutImageData(typed)));
        var content = stripped.GetProperty("message").GetProperty("content");
        Assert.Equal("what is this \"base64\" thing", content[0].GetProperty("text").GetString());
        Assert.Equal(("", 5000, "image/png"), (content[1].GetProperty("source").GetProperty("data").GetString(), content[1].GetProperty("source").GetProperty("omitted").GetInt32(), content[1].GetProperty("source").GetProperty("media_type").GetString()));
        Assert.True(stripped.GetProperty("isReplay").GetBoolean());
        Assert.True(ChatStream.WithoutImageData(typed).Length < 400);

        // An image a tool returned sits deeper; it goes the same way.
        var returned = Encoding.UTF8.GetBytes("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"DATA"}}]}]}}""".Replace("DATA", data));
        Assert.DoesNotContain(data, Encoding.UTF8.GetString(ChatStream.WithoutImageData(returned)));

        var plain = Encoding.UTF8.GetBytes(Echo);
        Assert.Same(plain, ChatStream.WithoutImageData(plain));
        var mention = Encoding.UTF8.GetBytes("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"the \"base64\" encoding"}]}}""");
        Assert.Same(mention, ChatStream.WithoutImageData(mention));
        var broken = Encoding.UTF8.GetBytes("{\"base64\" not json");
        Assert.Same(broken, ChatStream.WithoutImageData(broken));

        string path = Path.Combine(_dir, "with-image.jsonl");
        File.WriteAllText(path, Encoding.UTF8.GetString(typed).Replace("\"isReplay\":true,", "") + "\n");
        var history = ChatStream.History(path, 1 << 20).Single();
        Assert.True(history.Length < 400);
        Assert.Equal(5000, Line(Encoding.UTF8.GetString(history)).GetProperty("message").GetProperty("content")[1].GetProperty("source").GetProperty("omitted").GetInt32());
    }

    [Fact]
    public void A_request_nobody_can_answer_is_declined_and_a_permission_request_is_left_for_the_person()
    {
        Assert.Null(ChatStream.Decline(Line(Ask("r1"))));
        Assert.Null(ChatStream.Decline(Line(Result)));

        var declined = Line(ChatStream.Decline(Line("""{"type":"control_request","request_id":"m1","request":{"subtype":"mcp_message"}}"""))!);
        Assert.Equal("control_response", declined.GetProperty("type").GetString());
        Assert.Equal("error", declined.GetProperty("response").GetProperty("subtype").GetString());
        Assert.Equal("m1", declined.GetProperty("response").GetProperty("request_id").GetString());
        Assert.Contains("mcp_message", declined.GetProperty("response").GetProperty("error").GetString());
    }

    [Fact]
    public void Input_reaches_the_cli_only_as_whole_json_objects()
    {
        var lines = ChatStream.InputLines("{\"type\":\"user\"}\r\nnot json\n\n[1,2]\n\"text\"\n  {\"type\":\"control_response\"}  \n{\"broken\":");
        Assert.Equal(new[] { "{\"type\":\"user\"}", "{\"type\":\"control_response\"}" }, lines);

        var typed = Line(ChatStream.UserLine("he said \"hi\"\nthen left"));
        Assert.Equal("user", typed.GetProperty("type").GetString());
        Assert.Equal("he said \"hi\"\nthen left", typed.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, typed.GetProperty("parent_tool_use_id").ValueKind);
        Assert.DoesNotContain('\n', ChatStream.UserLine("a\nb"));
    }

    [Fact]
    public void A_live_line_is_wrapped_whole_and_a_host_line_carries_any_text()
    {
        var frame = Line(Encoding.UTF8.GetString(ChatStream.LiveFrame(Encoding.UTF8.GetBytes(Init))));
        Assert.Equal("live", frame.GetProperty("type").GetString());
        Assert.Equal("live-1", frame.GetProperty("line").GetProperty("session_id").GetString());

        var host = Line(Encoding.UTF8.GetString(ChatStream.HostLine("stderr", "can't \"log in\"\nline two")));
        Assert.Equal(("host", "stderr", "can't \"log in\"\nline two"),
            (host.GetProperty("type").GetString(), host.GetProperty("subtype").GetString(), host.GetProperty("text").GetString()));
    }

    [Fact]
    public void A_resumed_session_shows_what_was_said_before_and_nothing_that_was_not_part_of_it()
    {
        string path = Path.Combine(_dir, "t.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"queue-operation","operation":"enqueue"}""",
            """{"type":"user","uuid":"u1","timestamp":"2026-11-12T12:00:00Z","message":{"role":"user","content":"first question"}}""",
            """{"type":"user","uuid":"u2","isMeta":true,"message":{"role":"user","content":"caveat for the model"}}""",
            """{"type":"attachment","uuid":"a1","attachment":{}}""",
            """{"type":"assistant","uuid":"x1","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"a"}}]}}""",
            """{"type":"user","uuid":"u3","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"hello"}]}}""",
            """{"type":"assistant","uuid":"s1","isSidechain":true,"message":{"role":"assistant","content":[{"type":"text","text":"inside a subagent"}]}}""",
            """{"type":"user","uuid":"u4","isCompactSummary":true,"message":{"role":"user","content":"summary of earlier"}}""",
            """{"type":"assistant","uuid":"x2","message":{"role":"assistant","content":[{"type":"text","text":"first answer"}]}}""",
            "not json at all",
            "",
        });

        var lines = ChatStream.History(path, 1 << 20).Select(b => Line(Encoding.UTF8.GetString(b))).ToList();
        Assert.Equal(new[] { "user", "assistant", "user", "user", "assistant" }, lines.Select(l => l.GetProperty("type").GetString()));
        Assert.All(lines, l => Assert.True(l.GetProperty("history").GetBoolean()));
        Assert.Equal("first question", lines[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("2026-11-12T12:00:00Z", lines[0].GetProperty("timestamp").GetString());
        Assert.Equal("t1", lines[2].GetProperty("message").GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert.True(lines[3].GetProperty("compactSummary").GetBoolean());
        Assert.False(lines[0].TryGetProperty("uuid", out _));
        Assert.All(ChatStream.History(path, 1 << 20), b => Assert.DoesNotContain((byte)'\n', b));
    }

    [Fact]
    public void History_larger_than_its_share_of_the_scrollback_keeps_the_newest_and_a_missing_transcript_is_empty()
    {
        string path = Path.Combine(_dir, "big.jsonl");
        File.WriteAllLines(path, Enumerable.Range(1, 50).Select(i => """{"type":"user","message":{"role":"user","content":"message number NN padded out to a fixed width"}}""".Replace("NN", i.ToString("D2"))));
        var kept = ChatStream.History(path, 600).Select(b => Line(Encoding.UTF8.GetString(b)).GetProperty("message").GetProperty("content").GetString()!).ToList();
        Assert.InRange(kept.Count, 2, 10);
        Assert.EndsWith("50 padded out to a fixed width", kept[^1]);
        Assert.Equal(kept.OrderBy(k => k, StringComparer.Ordinal), kept);
        Assert.True(ChatStream.History(path, 600).Sum(b => b.Length + 1) <= 600);

        Assert.Empty(ChatStream.History(Path.Combine(_dir, "nowhere.jsonl"), 1 << 20));
        Assert.Empty(ChatStream.History("", 1 << 20));
    }

    [Fact]
    public void A_chat_is_launched_on_the_protocol_flags_with_the_deck_flags_and_no_hook_settings()
    {
        var found = HostManager.ClaudeExeLocator;
        try
        {
            HostManager.ClaudeExeLocator = () => @"C:\Tools\claude bin\claude.exe";
            string fresh = HostManager.NewClaudeChatCommand("abc-123", "my \"work\"", "--dangerously-skip-permissions", "claude-haiku-5-5", "high");
            Assert.Equal($"\"C:\\Tools\\claude bin\\claude.exe\" {ChatStream.ProtocolFlags} --session-id abc-123 --name \"my \\\"work\\\"\" --model \"claude-haiku-5-5\" --effort high --dangerously-skip-permissions", fresh);
            Assert.Contains("--input-format stream-json --output-format stream-json", fresh);
            Assert.Contains("--permission-prompt-tool stdio", fresh);
            Assert.DoesNotContain("--settings", fresh);

            Assert.Equal($"\"C:\\Tools\\claude bin\\claude.exe\" {ChatStream.ProtocolFlags} --resume abc-123 --dangerously-skip-permissions",
                HostManager.ResumeClaudeChatCommand("abc-123", "--dangerously-skip-permissions"));

            var settings = new Settings { ResumeFlags = "--dangerously-skip-permissions" };
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var plan = SessionStarter.Plan(new StartRequest("claude", home, "notes", "hello", Chat: true), settings)!;
            Assert.Equal((ChatStream.Kind, "Claude", "notes", "hello"), (plan.Kind, plan.Provider, plan.Title, plan.Prompt));
            Assert.Contains($"--session-id {plan.SessionId} --name \"notes\"", plan.Command);
            Assert.Equal("", SessionStarter.Plan(new StartRequest("claude", home), settings)!.Kind);
            Assert.Equal("", SessionStarter.Plan(new StartRequest("codex", home, Chat: true), settings)!.Kind);

            HostManager.ClaudeExeLocator = () => null;
            var missing = Assert.Throws<InvalidOperationException>(() => HostManager.NewClaudeChatCommand("abc", null, ""));
            Assert.Contains("claude.exe", missing.Message);
        }
        finally { HostManager.ClaudeExeLocator = found; }
    }

    [Fact]
    public void Claude_is_found_on_the_path_or_in_the_native_install_folder_and_only_as_an_exe()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\tools\claude.exe", @"C:\Users\x\.local\bin\claude.exe", @"C:\npm\claude.cmd" };
        Assert.Equal(@"D:\tools\claude.exe", HostManager.FindClaudeExe(@"C:\npm;;""D:\tools"";C:\other", @"C:\Users\x", files.Contains));
        Assert.Equal(@"C:\Users\x\.local\bin\claude.exe", HostManager.FindClaudeExe(@"C:\npm;C:\other", @"C:\Users\x", files.Contains));
        Assert.Null(HostManager.FindClaudeExe(@"C:\npm", @"C:\Users\nobody", files.Contains));
    }

    [Fact]
    public void A_new_sessions_transcript_is_expected_under_its_folders_slug_and_an_existing_one_is_found_there()
    {
        string projects = Path.Combine(_dir, "projects");
        string expected = Path.Combine(projects, "C--Work-my-repo", "abc-123.jsonl");
        Assert.Equal(expected, SessionScanner.TranscriptPathFor("abc-123", @"C:\Work\my repo", projects));
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, "{}");
        Assert.Equal(expected, SessionScanner.TranscriptPathFor("abc-123", @"C:\Work\my repo", projects));
        Assert.Equal("", SessionScanner.TranscriptPathFor("", @"C:\Work\my repo", projects));

        // A transcript that sits under another folder's slug is not hunted for: starting a session must not walk the tree.
        Directory.CreateDirectory(Path.Combine(projects, "elsewhere"));
        File.WriteAllText(Path.Combine(projects, "elsewhere", "moved-9.jsonl"), "{}");
        Assert.Equal(Path.Combine(projects, "C--Work-my-repo", "moved-9.jsonl"), SessionScanner.TranscriptPathFor("moved-9", @"C:\Work\my repo", projects));
    }

    [Fact]
    public void A_chat_session_is_listed_for_a_phone_but_not_offered_as_a_terminal()
    {
        HostRecord Host(string id, string kind) => new() { Id = id, SessionId = "s-" + id, Provider = "Claude", AgentStatus = "idle", StatusAt = T0, Protocol = 4, Title = id, StartedAt = T0.AddHours(-1), Kind = kind };
        SessionInfo Info(string id) => new() { SessionId = "s-" + id, Status = "idle", Kind = "interactive", Name = id, Provider = SessionProvider.Claude, StatusUpdatedAt = T0.ToLocalTime(), UpdatedAt = T0.ToLocalTime() };
        var entries = new SessionCatalog().Build(new() { (Host("term", ""), Info("term")), (Host("chat", ChatStream.Kind), Info("chat")) }, new Settings(), T0.AddMinutes(1), null);
        Assert.True(entries.Single(e => e.HostId == "term").MobileReady);
        Assert.False(entries.Single(e => e.HostId == "chat").MobileReady);
        // A phone opens it as a conversation instead: the gateway says it can relay one, and only while it runs.
        Assert.True(entries.Single(e => e.HostId == "chat").ChatReady);
        Assert.False(entries.Single(e => e.HostId == "term").ChatReady);
        Assert.Contains("\"chatReady\":true", SessionCatalog.ToJson(entries));
        var ended = Host("gone", ChatStream.Kind);
        ended.ExitCode = 0;
        Assert.False(new SessionCatalog().Build(new() { (ended, Info("gone")) }, new Settings(), T0.AddMinutes(1), null).Single().ChatReady);
        Assert.Contains("\"hostId\":\"chat\"", SessionCatalog.ToJson(entries));
        Assert.Contains("\"kind\":\"chat\"", SessionCatalog.ToJson(entries));
        Assert.Contains("\"kind\":\"\"", SessionCatalog.ToJson(entries));
        Assert.True(Host("chat", ChatStream.Kind).IsChat);
        Assert.False(Host("term", "").IsChat);
    }
}
