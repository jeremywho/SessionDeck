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

        Assert.True(chat.Apply(Line(Init), record, T0.AddSeconds(1)));
        Assert.Equal(("busy", "live-1", "claude-haiku-5-5"), (record.AgentStatus, record.SessionId, record.Model));
        Assert.Equal(T0.AddSeconds(1), record.StatusAt);

        Assert.True(chat.Apply(Line(Echo), record, T0.AddSeconds(2)));
        Assert.Equal(("UserPromptSubmit", ""), (record.LastEvent, record.Title));
        Assert.Equal(T0.AddSeconds(1), record.StatusAt);

        Assert.True(chat.Apply(Line(ToolUse), record, T0.AddSeconds(3)));
        Assert.Equal("Write", record.LastTool);
        Assert.False(chat.Apply(Line(ToolUse), record, T0.AddSeconds(4)));

        Assert.True(chat.Apply(Line(Result), record, T0.AddSeconds(5)));
        Assert.Equal(("idle", "Stop"), (record.AgentStatus, record.LastEvent));
        Assert.Equal(T0.AddSeconds(5), record.StatusAt);
    }

    [Fact]
    public void The_tab_takes_the_name_the_cli_gives_the_session()
    {
        var record = new HostRecord { Title = "billing" };
        var chat = new ChatStream();
        Assert.True(chat.Apply(Line("""{"type":"system","subtype":"session_title_changed","title":"billing rework"}"""), record, T0));
        Assert.Equal("billing rework", record.Title);
        Assert.False(chat.Apply(Line("""{"type":"system","subtype":"session_title_changed","title":""}"""), record, T0));
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
        Assert.False(new ChatStream().Apply(Line(Answer("interrupt-1")), record, T0));
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
    public void Only_the_conversation_itself_is_kept_for_a_later_viewer(string json, bool kept) =>
        Assert.Equal(kept, ChatStream.Durable(Line(json)));

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
        Assert.Contains("\"hostId\":\"chat\"", SessionCatalog.ToJson(entries));
        Assert.Contains("\"kind\":\"chat\"", SessionCatalog.ToJson(entries));
        Assert.Contains("\"kind\":\"\"", SessionCatalog.ToJson(entries));
        Assert.True(Host("chat", ChatStream.Kind).IsChat);
        Assert.False(Host("term", "").IsChat);
    }
}
