using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>
/// The Codex translator on its own: app-server messages in the shapes codex-cli 0.167.2 sent
/// (cut down to the fields read), and what a viewer and the app server are then told.
/// </summary>
[Collection("HostManager exe locators")]
public class CodexChatTests
{
    const string Thread = "01a1d346-0f38-74b1-9d27-2413232a9b1e", Other = "01a1d33c-2727-70f0-b24f-116fd1536c2a";
    static readonly DateTime T0 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static JsonElement Of(ChatEffect e) => J(Encoding.UTF8.GetString(e.Data));
    static string Type(JsonElement e) => e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
    static string Raw(ChatEffect e) => Encoding.UTF8.GetString(e.Data);

    static List<JsonElement> Sent(List<ChatEffect> effects) => effects.Where(e => e.Kind == ChatEffectKind.ToCli).Select(Of).ToList();
    static List<JsonElement> Kept(List<ChatEffect> effects) => effects.Where(e => e.Kind == ChatEffectKind.Durable).Select(Of).ToList();
    static List<JsonElement> Passed(List<ChatEffect> effects) => effects.Where(e => e.Kind == ChatEffectKind.Live).Select(Of).ToList();
    static string Method(JsonElement m) => m.GetProperty("method").GetString()!;
    static long Id(JsonElement m) => m.GetProperty("id").GetInt64();

    static string Response(long id, string result) => "{\"id\":" + id + ",\"result\":" + result + "}";
    static string Failure(long id, string message) => "{\"id\":" + id + ",\"error\":{\"code\":-32000,\"message\":\"" + message + "\"}}";
    static string Note(string method, string @params) => "{\"method\":\"" + method + "\",\"params\":" + @params + "}";
    static string UserSays(string text) => ChatStream.UserLine(text);

    const string ThreadResult = """{"thread":{"id":"THREAD","preview":"","name":null,"path":"C:\\Users\\x\\.codex\\sessions\\rollout-THREAD.jsonl"},"model":"gpt-6-astra","reasoningEffort":"medium"}""";

    /// <summary>A chat with its app server up and a thread open: what every other test starts from.</summary>
    static (CodexChat Chat, HostRecord Record) Open(string session = "", string model = "", string effort = "", string mode = CodexChat.Full)
    {
        var chat = new CodexChat(@"C:\work", session, model, effort, mode);
        var record = new HostRecord();
        var hello = Sent(chat.Start());
        Assert.Equal("initialize", Method(hello.Single()));
        var next = Sent(chat.FromCli(J(Response(Id(hello[0]), "{\"userAgent\":\"x\"}")), record, T0));
        Assert.Equal(new[] { "initialized", "model/list", session.Length > 0 ? "thread/resume" : "thread/start" }, next.Select(Method));
        Assert.False(next[0].TryGetProperty("id", out _));
        chat.FromCli(J(Response(Id(next[2]), ThreadResult.Replace("THREAD", session.Length > 0 ? session : Thread))), record, T0);
        return (chat, record);
    }

    [Fact]
    public void It_introduces_itself_asks_for_the_models_and_opens_a_thread_as_the_mode_says()
    {
        var chat = new CodexChat(@"C:\work", "", "", "", CodexChat.Ask);
        var record = new HostRecord();
        var hello = Sent(chat.Start());
        var next = chat.FromCli(J(Response(Id(hello[0]), "{}")), record, T0);
        var start = Sent(next).Single(m => Method(m) == "thread/start").GetProperty("params");
        // Asking first is an approval policy; the sandbox is off either way (it refuses to run commands on Windows).
        Assert.Equal((@"C:\work", "untrusted", "danger-full-access"), (start.GetProperty("cwd").GetString(), start.GetProperty("approvalPolicy").GetString(), start.GetProperty("sandbox").GetString()));
        Assert.False(start.TryGetProperty("model", out _));

        var models = Sent(next).Single(m => Method(m) == "model/list");
        var told = chat.FromCli(J(Response(Id(models), """{"data":[{"id":"gpt-6-astra","model":"gpt-6-astra","displayName":"GPT-6-Astra","description":"Deep","hidden":false,"supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"ultra"}]},{"id":"secret","model":"secret","displayName":"Hidden","hidden":true,"supportedReasoningEfforts":[]}]}""")), record, T0);
        var catalog = Of(told.Single(e => e.Kind == ChatEffectKind.Frame));
        Assert.Equal(("catalog", "Codex"), (Type(catalog), catalog.GetProperty("agent").GetString()));
        var only = catalog.GetProperty("models").EnumerateArray().Single();
        Assert.Equal(("gpt-6-astra", "GPT-6-Astra", "low,ultra"), (only.GetProperty("value").GetString(), only.GetProperty("name").GetString(), string.Join(",", only.GetProperty("efforts").EnumerateArray().Select(e => e.GetString()))));
        Assert.Equal(new[] { "default", "bypassPermissions" }, catalog.GetProperty("modes").EnumerateArray().Select(m => m[0].GetString()));
        Assert.Equal(catalog.GetRawText(), chat.CatalogFrame());

        var opened = chat.FromCli(J(Response(Id(Sent(next).Single(m => Method(m) == "thread/start")), ThreadResult.Replace("THREAD", Thread))), record, T0);
        Assert.Equal((Thread, "gpt-6-astra", "medium", "default"), (record.SessionId, record.Model, record.Effort, record.PermissionMode));
        Assert.EndsWith(Thread + ".jsonl", record.TranscriptPath);
        var settings = Of(opened.Single(e => e.Kind == ChatEffectKind.Frame));
        Assert.Equal(("settings", "default", "gpt-6-astra", "medium"), (Type(settings), settings.GetProperty("mode").GetString(), settings.GetProperty("model").GetString(), settings.GetProperty("effort").GetString()));
    }

    [Fact]
    public void A_message_starts_a_turn_and_one_typed_while_a_turn_runs_joins_it()
    {
        var (chat, record) = Open(model: "gpt-6.1-sol", effort: "high");
        var first = Sent(chat.FromViewer(J(UserSays("do the thing")), record, T0)).Single();
        var p = first.GetProperty("params");
        Assert.Equal("turn/start", Method(first));
        Assert.Equal((Thread, "never", "dangerFullAccess", "gpt-6.1-sol", "high"), (p.GetProperty("threadId").GetString(), p.GetProperty("approvalPolicy").GetString(),
            p.GetProperty("sandboxPolicy").GetProperty("type").GetString(), p.GetProperty("model").GetString(), p.GetProperty("effort").GetString()));
        Assert.Equal(("text", "do the thing"), (p.GetProperty("input")[0].GetProperty("type").GetString(), p.GetProperty("input")[0].GetProperty("text").GetString()));
        Assert.Equal(("busy", "UserPromptSubmit"), (record.AgentStatus, record.LastEvent));

        // Typed before the app server has said which turn it started: held, since there is no turn to name yet.
        Assert.Empty(chat.FromViewer(J(UserSays("and this")), record, T0));
        var began = chat.FromCli(J(Response(Id(first), "{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\"}}")), record, T0);
        var steer = Sent(began).Single();
        Assert.Equal(("turn/steer", "turn-1", "and this"), (Method(steer), steer.GetProperty("params").GetProperty("expectedTurnId").GetString(), steer.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()));
        // And one typed mid-turn goes straight in.
        Assert.Equal("turn/steer", Method(Sent(chat.FromViewer(J(UserSays("one more")), record, T0)).Single()));

        // The start of the turn is for whoever is watching, not for the scrollback.
        var started = chat.FromCli(J(Note("turn/started", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-1\"}}")), record, T0);
        var init = Passed(started).Single();
        Assert.Equal(("system", "init", Thread, "gpt-6.1-sol"), (Type(init), init.GetProperty("subtype").GetString(), init.GetProperty("session_id").GetString(), init.GetProperty("model").GetString()));
        Assert.Empty(Kept(started));

        // A picture goes as a data URL, and a message of only pictures is still a message.
        var withImage = J("""{"type":"user","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"QUJD"}}]}}""");
        var input = Sent(chat.FromViewer(withImage, record, T0)).Single().GetProperty("params").GetProperty("input");
        Assert.Equal(("image", "data:image/png;base64,QUJD"), (input[0].GetProperty("type").GetString(), input[0].GetProperty("url").GetString()));
        Assert.Empty(chat.FromViewer(J(UserSays("")), record, T0));
    }

    [Fact]
    public void A_message_typed_before_the_thread_is_open_waits_for_it()
    {
        var chat = new CodexChat(@"C:\work", "", "", "", CodexChat.Full);
        var record = new HostRecord();
        var hello = Sent(chat.Start());
        Assert.Empty(chat.FromViewer(J(UserSays("early")), record, T0));
        var next = Sent(chat.FromCli(J(Response(Id(hello[0]), "{}")), record, T0));
        var opened = Sent(chat.FromCli(J(Response(Id(next.Single(m => Method(m) == "thread/start")), ThreadResult.Replace("THREAD", Thread))), record, T0));
        Assert.Equal(("turn/start", "early"), (Method(opened.Single()), opened.Single().GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()));
    }

    const string Exec = """{"type":"commandExecution","id":"exec-1","command":"\"C:\\\\pwsh.exe\" -Command 'echo hi'","commandActions":[{"type":"unknown","command":"echo hi"}],"status":"STATUS","aggregatedOutput":OUTPUT,"exitCode":CODE}""";

    static string Item(string kind, string item) => Note("item/" + kind, "{\"threadId\":\"" + Thread + "\",\"item\":" + item + "}");

    [Fact]
    public void A_turn_reads_as_the_message_what_was_said_each_call_with_its_result_and_an_end()
    {
        var (chat, record) = Open();
        chat.FromCli(J(Note("turn/started", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-1\"}}")), record, T0);

        var user = chat.FromCli(J(Item("completed", """{"type":"userMessage","id":"u1","content":[{"type":"text","text":"Fix   the\nretry loop, please, and tell me what was wrong with it afterwards in detail"},{"type":"image","url":"data:image/png;base64,AAAA"}]}""")), record, T0);
        var said = Kept(user).Single();
        Assert.Equal(("user", "u1", true), (Type(said), said.GetProperty("uuid").GetString(), said.GetProperty("isReplay").GetBoolean()));
        // The picture is counted and not kept.
        var blocks = said.GetProperty("message").GetProperty("content");
        Assert.Equal(("text", "image", "", 26), (blocks[0].GetProperty("type").GetString(), blocks[1].GetProperty("type").GetString(), blocks[1].GetProperty("source").GetProperty("data").GetString(), blocks[1].GetProperty("source").GetProperty("omitted").GetInt32()));
        // The conversation is named once, by what was first said, short enough for a tab.
        var title = Of(user.Single(e => e.Kind == ChatEffectKind.Frame));
        Assert.Equal("title", Type(title));
        Assert.Equal(record.Title, title.GetProperty("title").GetString());
        Assert.StartsWith("Fix the retry loop, please", record.Title);
        Assert.True(record.Title.Length <= 60 && record.Title.EndsWith('…'));
        Assert.DoesNotContain(chat.FromCli(J(Item("completed", """{"type":"userMessage","id":"u2","content":[{"type":"text","text":"another"}]}""")), record, T0), e => e.Kind == ChatEffectKind.Frame);

        // Words stream to whoever is watching and land once, whole.
        var streamed = Passed(chat.FromCli(J(Item("started", """{"type":"agentMessage","id":"m1","text":""}""")), record, T0)).Single();
        Assert.Equal(("content_block_start", "text"), (streamed.GetProperty("event").GetProperty("type").GetString(), streamed.GetProperty("event").GetProperty("content_block").GetProperty("type").GetString()));
        var delta = Passed(chat.FromCli(J(Note("item/agentMessage/delta", "{\"threadId\":\"" + Thread + "\",\"itemId\":\"m1\",\"delta\":\"Loo\"}")), record, T0)).Single();
        Assert.Equal(("text_delta", "Loo"), (delta.GetProperty("event").GetProperty("delta").GetProperty("type").GetString(), delta.GetProperty("event").GetProperty("delta").GetProperty("text").GetString()));
        var whole = Kept(chat.FromCli(J(Item("completed", """{"type":"agentMessage","id":"m1","text":"Looking."}""")), record, T0)).Single();
        Assert.Equal(("assistant", "m1", "Looking."), (Type(whole), whole.GetProperty("uuid").GetString(), whole.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()));
        var thought = Passed(chat.FromCli(J(Item("started", """{"type":"reasoning","id":"r1","summary":[],"content":[]}""")), record, T0)).Single();
        Assert.Equal("thinking", thought.GetProperty("event").GetProperty("content_block").GetProperty("type").GetString());
        Assert.Equal("thinking_delta", Passed(chat.FromCli(J(Note("item/reasoning/summaryTextDelta", "{\"threadId\":\"" + Thread + "\",\"delta\":\"hm\"}")), record, T0)).Single().GetProperty("event").GetProperty("delta").GetProperty("type").GetString());
        Assert.Equal("hm\n\nmore", Kept(chat.FromCli(J(Item("completed", """{"type":"reasoning","id":"r1","summary":["hm"],"content":["more"]}""")), record, T0)).Single().GetProperty("message").GetProperty("content")[0].GetProperty("thinking").GetString());

        // A command is a call when it starts and a result when it ends; the command shown is the one inside the shell wrapper.
        var call = Kept(chat.FromCli(J(Item("started", Exec.Replace("STATUS", "inProgress").Replace("OUTPUT", "null").Replace("CODE", "null"))), record, T0)).Single().GetProperty("message").GetProperty("content")[0];
        Assert.Equal(("tool_use", "exec-1", "Shell", "echo hi"), (call.GetProperty("type").GetString(), call.GetProperty("id").GetString(), call.GetProperty("name").GetString(), call.GetProperty("input").GetProperty("command").GetString()));
        Assert.Equal("Shell", record.LastTool);
        var result = Kept(chat.FromCli(J(Item("completed", Exec.Replace("STATUS", "completed").Replace("OUTPUT", "\"hi\\r\\n\"").Replace("CODE", "0"))), record, T0)).Single().GetProperty("message").GetProperty("content")[0];
        Assert.Equal(("tool_result", "exec-1", "hi\r\n", false), (result.GetProperty("type").GetString(), result.GetProperty("tool_use_id").GetString(), result.GetProperty("content").GetString(), result.GetProperty("is_error").GetBoolean()));
        // One that exits non-zero failed, whatever its status says.
        chat.FromCli(J(Item("started", Exec.Replace("exec-1", "exec-2").Replace("STATUS", "inProgress").Replace("OUTPUT", "null").Replace("CODE", "null"))), record, T0);
        Assert.True(Kept(chat.FromCli(J(Item("completed", Exec.Replace("exec-1", "exec-2").Replace("STATUS", "completed").Replace("OUTPUT", "\"no\"").Replace("CODE", "2"))), record, T0)).Single().GetProperty("message").GetProperty("content")[0].GetProperty("is_error").GetBoolean());

        // A file change carries its diff: a new file is all additions, an edit is the diff Codex made.
        var change = Kept(chat.FromCli(J(Item("started", """{"type":"fileChange","id":"fc1","status":"inProgress","changes":[{"path":"C:\\work\\a.txt","kind":{"type":"add"},"diff":"alpha\nbeta\n"},{"path":"C:\\work\\b.txt","kind":{"type":"update","move_path":null},"diff":"@@ -1 +1 @@\n-old\n+new\n"}]}""")), record, T0)).Single().GetProperty("message").GetProperty("content")[0];
        Assert.Equal(("Edit", @"C:\work\a.txt, C:\work\b.txt", "+alpha\n+beta\n@@ -1 +1 @@\n-old\n+new"), (change.GetProperty("name").GetString(), change.GetProperty("input").GetProperty("file_path").GetString(), change.GetProperty("input").GetProperty("diff").GetString()));
        var declined = Kept(chat.FromCli(J(Item("completed", """{"type":"fileChange","id":"fc1","status":"declined","changes":[]}""")), record, T0)).Single().GetProperty("message").GetProperty("content")[0];
        Assert.Equal((true, "Declined."), (declined.GetProperty("is_error").GetBoolean(), declined.GetProperty("content").GetString()));

        // A call whose start was never seen still shows whole when it ends.
        var both = Kept(chat.FromCli(J(Item("completed", """{"type":"mcpToolCall","id":"mcp1","server":"docs","tool":"search","status":"completed","arguments":{"q":"retry"},"result":{"content":[{"type":"text","text":"found"}]},"error":null}""")), record, T0));
        Assert.Equal(new[] { "assistant", "user" }, both.Select(Type));
        Assert.Equal(("docs:search", "retry"), (both[0].GetProperty("message").GetProperty("content")[0].GetProperty("name").GetString(), both[0].GetProperty("message").GetProperty("content")[0].GetProperty("input").GetProperty("q").GetString()));
        Assert.Equal("found", both[1].GetProperty("message").GetProperty("content")[0].GetProperty("content").GetString());

        var end = chat.FromCli(J(Note("turn/completed", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\",\"error\":null}}")), record, T0.AddSeconds(5));
        Assert.Equal(("result", "success", false), (Type(Kept(end).Single()), Kept(end).Single().GetProperty("subtype").GetString(), Kept(end).Single().GetProperty("is_error").GetBoolean()));
        Assert.Equal(("idle", "Stop", T0.AddSeconds(5)), (record.AgentStatus, record.LastEvent, record.StatusAt));
        // The next message starts a turn of its own.
        Assert.Equal("turn/start", Method(Sent(chat.FromViewer(J(UserSays("next")), record, T0)).Single()));
    }

    [Fact]
    public void A_stopped_turn_says_so_and_a_failed_one_says_why()
    {
        var (chat, record) = Open();
        chat.FromCli(J(Note("turn/started", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-1\"}}")), record, T0);
        var stop = Sent(chat.FromViewer(J("""{"type":"control_request","request_id":"interrupt-1","request":{"subtype":"interrupt"}}"""), record, T0)).Single();
        Assert.Equal(("turn/interrupt", Thread, "turn-1"), (Method(stop), stop.GetProperty("params").GetProperty("threadId").GetString(), stop.GetProperty("params").GetProperty("turnId").GetString()));
        var stopped = Kept(chat.FromCli(J(Note("turn/completed", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-1\",\"status\":\"interrupted\",\"error\":null}}")), record, T0));
        Assert.Equal(new[] { "user", "result" }, stopped.Select(Type));
        Assert.StartsWith("[Request interrupted by user", stopped[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(("error_during_execution", false), (stopped[1].GetProperty("subtype").GetString(), stopped[1].GetProperty("is_error").GetBoolean()));
        // With no turn running there is nothing to stop.
        Assert.Empty(Sent(chat.FromViewer(J("""{"type":"control_request","request_id":"interrupt-2","request":{"subtype":"interrupt"}}"""), record, T0)));

        chat.FromCli(J(Note("turn/started", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-2\"}}")), record, T0);
        var error = Kept(chat.FromCli(J(Note("error", "{\"threadId\":\"" + Thread + "\",\"error\":{\"message\":\"stream dropped\"},\"willRetry\":true}")), record, T0)).Single();
        Assert.Equal(("system", "api_error", "stream dropped"), (Type(error), error.GetProperty("subtype").GetString(), error.GetProperty("error").GetProperty("message").GetString()));
        var failed = Kept(chat.FromCli(J(Note("turn/completed", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-2\",\"status\":\"failed\",\"error\":{\"message\":\"usage limit reached\"}}}")), record, T0)).Single();
        Assert.Equal(("error", true, "usage limit reached"), (failed.GetProperty("subtype").GetString(), failed.GetProperty("is_error").GetBoolean(), failed.GetProperty("result").GetString()));

        // A turn the app server would not start ends at once, with its reason.
        var turn = Sent(chat.FromViewer(J(UserSays("again")), record, T0)).Single();
        var refused = Kept(chat.FromCli(J(Failure(Id(turn), "thread is archived")), record, T0)).Single();
        Assert.Equal((true, "thread is archived", "idle"), (refused.GetProperty("is_error").GetBoolean(), refused.GetProperty("result").GetString(), record.AgentStatus));
    }

    [Fact]
    public void An_approval_goes_to_the_viewers_and_their_answer_goes_back_as_codex_names_it()
    {
        var (chat, record) = Open(mode: CodexChat.Ask);
        chat.FromCli(J(Note("turn/started", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-1\"}}")), record, T0);
        string Ask(int id, string method, string extra) => "{\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":{\"threadId\":\"" + Thread + "\",\"turnId\":\"turn-1\",\"itemId\":\"it-" + id + "\"" + extra + "}}";
        string Answer(string key, string body) => "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"" + key + "\",\"response\":" + body + "}}";

        var asked = Kept(chat.FromCli(J(Ask(0, "item/commandExecution/requestApproval", ",\"reason\":\"needs the network\",\"command\":\"\\\"C:\\\\\\\\pwsh.exe\\\" -Command 'npm test'\",\"commandActions\":[{\"type\":\"unknown\",\"command\":\"npm test\"}]")), record, T0)).Single();
        var request = asked.GetProperty("request");
        Assert.Equal(("control_request", "cx-0", "can_use_tool", "Shell", "it-0", "needs the network", "npm test"), (Type(asked), asked.GetProperty("request_id").GetString(), request.GetProperty("subtype").GetString(),
            request.GetProperty("tool_name").GetString(), request.GetProperty("tool_use_id").GetString(), request.GetProperty("description").GetString(), request.GetProperty("input").GetProperty("command").GetString()));
        // "Always" is Codex's own: this command again, for the session.
        var remember = request.GetProperty("permission_suggestions")[0];
        Assert.Equal(("addRules", "session", "Shell", "npm test"), (remember.GetProperty("type").GetString(), remember.GetProperty("destination").GetString(), remember.GetProperty("rules")[0].GetProperty("toolName").GetString(), remember.GetProperty("rules")[0].GetProperty("ruleContent").GetString()));
        Assert.Equal(("waiting", "PermissionRequest", "Shell"), (record.AgentStatus, record.LastEvent, record.LastTool));

        var allowed = chat.FromViewer(J(Answer("cx-0", "{\"behavior\":\"allow\",\"updatedInput\":{}}")), record, T0);
        Assert.Equal("""{"id":0,"result":{"decision":"accept"}}""", Sent(allowed).Single().GetRawText());
        // The answer is part of the conversation: it is what closes the card for every viewer.
        Assert.Equal(("control_response", "cx-0"), (Type(Kept(allowed).Single()), Kept(allowed).Single().GetProperty("response").GetProperty("request_id").GetString()));
        Assert.Equal("busy", record.AgentStatus);
        // An answer to something not asked (twice, or never) goes nowhere.
        Assert.Empty(chat.FromViewer(J(Answer("cx-0", "{\"behavior\":\"allow\"}")), record, T0));
        Assert.Empty(chat.FromViewer(J(Answer("cx-99", "{\"behavior\":\"allow\"}")), record, T0));

        var file = Kept(chat.FromCli(J(Ask(1, "item/fileChange/requestApproval", ",\"reason\":null")), record, T0)).Single().GetProperty("request");
        Assert.Equal(("Edit", "setMode", "acceptEdits"), (file.GetProperty("tool_name").GetString(), file.GetProperty("permission_suggestions")[0].GetProperty("type").GetString(), file.GetProperty("permission_suggestions")[0].GetProperty("mode").GetString()));
        Assert.Equal("""{"id":1,"result":{"decision":"acceptForSession"}}""", Sent(chat.FromViewer(J(Answer("cx-1", "{\"behavior\":\"allow\",\"updatedPermissions\":[{\"type\":\"setMode\"}]}")), record, T0)).Single().GetRawText());
        chat.FromCli(J(Ask(2, "item/commandExecution/requestApproval", ",\"command\":\"rm x\"")), record, T0);
        Assert.Equal("""{"id":2,"result":{"decision":"decline"}}""", Sent(chat.FromViewer(J(Answer("cx-2", "{\"behavior\":\"deny\",\"message\":\"no\"}")), record, T0)).Single().GetRawText());

        // Settled some other way (the turn was stopped): the card closes, once.
        chat.FromCli(J(Ask(3, "item/commandExecution/requestApproval", ",\"command\":\"ls\"")), record, T0);
        var settled = Kept(chat.FromCli(J(Note("serverRequest/resolved", "{\"threadId\":\"" + Thread + "\",\"requestId\":3}")), record, T0)).Single();
        Assert.Equal(("control_cancel_request", "cx-3", "busy"), (Type(settled), settled.GetProperty("request_id").GetString(), record.AgentStatus));
        Assert.Empty(chat.FromCli(J(Note("serverRequest/resolved", "{\"threadId\":\"" + Thread + "\",\"requestId\":0}")), record, T0));
        // Still open when the turn ends: closed with it.
        chat.FromCli(J(Ask(4, "item/commandExecution/requestApproval", ",\"command\":\"ls\"")), record, T0);
        Assert.Equal(new[] { "control_cancel_request", "result" }, Kept(chat.FromCli(J(Note("turn/completed", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\"}}")), record, T0)).Select(Type));

        // Anything else the app server asks of its client is declined at once: unanswered, it holds the turn for good.
        var other = chat.FromCli(J("{\"id\":\"abc\",\"method\":\"item/tool/requestUserInput\",\"params\":{\"threadId\":\"" + Thread + "\"}}"), record, T0);
        var no = Sent(other).Single();
        Assert.Equal(("abc", -32601), (no.GetProperty("id").GetString(), no.GetProperty("error").GetProperty("code").GetInt32()));
        Assert.Contains("item/tool/requestUserInput", no.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(Kept(other));
    }

    [Fact]
    public void The_model_effort_and_mode_a_viewer_picks_go_with_the_next_turn()
    {
        var (chat, record) = Open();
        string Set(string id, string body) => "{\"type\":\"control_request\",\"request_id\":\"" + id + "\",\"request\":" + body + "}";
        var model = chat.FromViewer(J(Set("sd-set-1", "{\"subtype\":\"set_model\",\"model\":\"gpt-6.1-sol\"}")), record, T0);
        Assert.Equal("success", Passed(model).Single().GetProperty("response").GetProperty("subtype").GetString());
        Assert.Equal("gpt-6.1-sol", Of(model.Single(e => e.Kind == ChatEffectKind.Frame)).GetProperty("model").GetString());
        chat.FromViewer(J(Set("sd-set-2", "{\"subtype\":\"apply_flag_settings\",\"settings\":{\"effortLevel\":\"ultra\"}}")), record, T0);
        chat.FromViewer(J(Set("sd-set-3", "{\"subtype\":\"set_permission_mode\",\"mode\":\"default\"}")), record, T0);
        Assert.Equal(("gpt-6.1-sol", "ultra", "default"), (record.Model, record.Effort, record.PermissionMode));
        var p = Sent(chat.FromViewer(J(UserSays("go")), record, T0)).Single().GetProperty("params");
        Assert.Equal(("gpt-6.1-sol", "ultra", "untrusted"), (p.GetProperty("model").GetString(), p.GetProperty("effort").GetString(), p.GetProperty("approvalPolicy").GetString()));

        // A mode Codex does not have, or a setting it does not have, is refused and changes nothing.
        foreach (string body in new[] { "{\"subtype\":\"set_permission_mode\",\"mode\":\"plan\"}", "{\"subtype\":\"set_max_thinking_tokens\"}" })
        {
            var refused = chat.FromViewer(J(Set("sd-set-9", body)), record, T0);
            Assert.Equal(("error", "sd-set-9"), (Passed(refused).Single().GetProperty("response").GetProperty("subtype").GetString(), Passed(refused).Single().GetProperty("response").GetProperty("request_id").GetString()));
            Assert.DoesNotContain(refused, e => e.Kind == ChatEffectKind.Frame);
        }
        Assert.Equal("default", record.PermissionMode);
    }

    static string Turn(string id, string status, params string[] items) => "{\"id\":\"" + id + "\",\"status\":\"" + status + "\",\"items\":[" + string.Join(",", items) + "]}";
    static string UserItem(string id, string text) => "{\"type\":\"userMessage\",\"id\":\"" + id + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}";
    static string AgentItem(string id, string text) => "{\"type\":\"agentMessage\",\"id\":\"" + id + "\",\"text\":\"" + text + "\"}";
    static string Page(string? next, params string[] turnsNewestFirst) => "{\"data\":[" + string.Join(",", turnsNewestFirst) + "],\"nextCursor\":" + (next == null ? "null" : "\"" + next + "\"") + "}";
    static List<string> Uuids(JsonElement frame) => frame.GetProperty("lines").EnumerateArray().Where(l => l.TryGetProperty("uuid", out _)).Select(l => l.GetProperty("uuid").GetString()!).ToList();

    [Fact]
    public void A_resumed_conversation_is_read_back_newest_page_first_and_earlier_turns_on_request()
    {
        var chat = new CodexChat(@"C:\work", Thread, "", "", CodexChat.Full);
        var record = new HostRecord();
        var next = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start())[0]), "{}")), record, T0));
        var resume = next.Single(m => Method(m) == "thread/resume");
        Assert.Equal((Thread, true), (resume.GetProperty("params").GetProperty("threadId").GetString(), resume.GetProperty("params").GetProperty("excludeTurns").GetBoolean()));
        var opened = chat.FromCli(J(Response(Id(resume), ThreadResult.Replace("THREAD", Thread).Replace("\"preview\":\"\"", "\"preview\":\"what was first asked\""))), record, T0);
        var seed = Sent(opened).Single();
        Assert.Equal(("thread/turns/list", "desc", "full"), (Method(seed), seed.GetProperty("params").GetProperty("sortDirection").GetString(), seed.GetProperty("params").GetProperty("itemsView").GetString()));
        Assert.False(seed.GetProperty("params").TryGetProperty("cursor", out _));

        // The app server gives the newest turn first; a viewer reads oldest first, and a stopped turn says it was stopped.
        var served = chat.FromCli(J(Response(Id(seed), Page("cursor-1",
            Turn("t3", "completed", UserItem("u3", "third"), AgentItem("a3", "three")),
            Turn("t2", "interrupted", UserItem("u2", "second"))))), record, T0);
        var reset = served.Single(e => e.Kind == ChatEffectKind.Reset).Seed!;
        Assert.Equal((Thread, "what was first asked", true), (reset.Session, reset.Title, reset.Earlier));
        var lines = reset.Lines.Select(l => J(Encoding.UTF8.GetString(l))).ToList();
        Assert.Equal(new[] { "second", "[Request interrupted by user]", "third", "three" }, lines.Select(l => l.GetProperty("message").GetProperty("content") is { ValueKind: JsonValueKind.String } s ? s.GetString()! : l.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!));
        Assert.All(lines, l => Assert.True(l.GetProperty("history").GetBoolean()));
        Assert.Equal((Thread, "what was first asked", "idle"), (record.SessionId, record.Title, record.AgentStatus));

        // Before the oldest message shown: the page after the one given out, by the cursor that came with it.
        var ask = chat.History("h1", "u2", "viewer-a");
        Assert.Equal("cursor-1", Sent(ask).Single().GetProperty("params").GetProperty("cursor").GetString());
        var page = chat.FromCli(J(Response(Id(Sent(ask).Single()), Page(null, Turn("t1", "completed", UserItem("u1", "first"), AgentItem("a1", "one"))))), record, T0).Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a"), (page.Kind, page.Tag));
        Assert.Equal(("history", "h1", JsonValueKind.Null), (Type(Of(page)), Of(page).GetProperty("request").GetString(), Of(page).GetProperty("next").ValueKind));
        Assert.Equal(new[] { "u1", "a1" }, Uuids(Of(page)));
        // And before that there is nothing, which is said without asking again.
        var none = chat.History("h2", "u1", "viewer-a").Single();
        Assert.Equal((ChatEffectKind.Reply, 0), (none.Kind, Of(none).GetProperty("lines").GetArrayLength()));

        // A viewer whose oldest message is in the middle of a page (its scrollback had dropped lines) is found from the newest turn back.
        var walk = chat.History("h3", "a3", "viewer-b");
        Assert.False(Sent(walk).Single().GetProperty("params").TryGetProperty("cursor", out _));
        var found = chat.FromCli(J(Response(Id(Sent(walk).Single()), Page("cursor-1",
            Turn("t3", "completed", UserItem("u3", "third"), AgentItem("a3", "three")),
            Turn("t2", "interrupted", UserItem("u2", "second"))))), record, T0).Single();
        Assert.Equal("viewer-b", found.Tag);
        Assert.Equal(new[] { "u2", "u3" }, Uuids(Of(found)));
        Assert.Equal(JsonValueKind.Number, Of(found).GetProperty("next").ValueKind);
        // One further back than the first page is looked for on the next, and one that is nowhere gets nothing.
        var far = chat.History("h4", "a1", "viewer-b");
        var onward = chat.FromCli(J(Response(Id(Sent(far).Single()), Page("cursor-1", Turn("t3", "completed", UserItem("u3", "third"))))), record, T0);
        Assert.Equal("cursor-1", Sent(onward).Single().GetProperty("params").GetProperty("cursor").GetString());
        var last = chat.FromCli(J(Response(Id(Sent(onward).Single()), Page(null, Turn("t1", "completed", UserItem("u1", "first"), AgentItem("a1", "one"))))), record, T0).Single();
        Assert.Equal(new[] { "u1" }, Uuids(Of(last)));
        var lost = chat.History("h5", "no-such-message", "viewer-b");
        Assert.Equal(0, Of(chat.FromCli(J(Response(Id(Sent(lost).Single()), Page(null, Turn("t1", "completed", UserItem("u1", "first"))))), record, T0).Single()).GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public void More_history_than_the_scrollback_holds_keeps_the_newest_and_says_there_is_more()
    {
        var chat = new CodexChat(@"C:\work", Thread, "", "", CodexChat.Full, budget: 600);
        var record = new HostRecord();
        var next = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start())[0]), "{}")), record, T0));
        var seed = Sent(chat.FromCli(J(Response(Id(next.Single(m => Method(m) == "thread/resume")), ThreadResult.Replace("THREAD", Thread))), record, T0)).Single();
        var reset = chat.FromCli(J(Response(Id(seed), Page(null, Turn("t1", "completed", UserItem("u1", new string('a', 400)), AgentItem("a1", new string('b', 400)), UserItem("u2", "short"), AgentItem("a2", "tail"))))), record, T0)
            .Single(e => e.Kind == ChatEffectKind.Reset).Seed!;
        Assert.True(reset.Earlier);
        Assert.Equal(new[] { "u2", "a2" }, reset.Lines.Select(l => J(Encoding.UTF8.GetString(l)).GetProperty("uuid").GetString()));
        Assert.True(reset.Lines.Sum(l => l.Length + 1) <= 600);
    }

    [Fact]
    public void The_conversation_it_was_started_on_being_gone_starts_a_new_one_and_says_so()
    {
        var chat = new CodexChat(@"C:\work", Thread, "", "", CodexChat.Full);
        var record = new HostRecord();
        var next = Sent(chat.FromCli(J(Response(Id(Sent(chat.Start())[0]), "{}")), record, T0));
        var failed = chat.FromCli(J(Failure(Id(next.Single(m => Method(m) == "thread/resume")), "no rollout found")), record, T0);
        Assert.Equal("thread/start", Method(Sent(failed).Single()));
        Assert.Equal("Could not resume that conversation: no rollout found A new one was started.", Kept(failed).Single().GetProperty("text").GetString());
        chat.FromCli(J(Response(Id(Sent(failed).Single()), ThreadResult.Replace("THREAD", Other))), record, T0);
        Assert.Equal(Other, record.SessionId);
    }

    [Fact]
    public void Conversations_in_the_folder_are_listed_and_moving_to_one_serves_it_without_a_restart()
    {
        var (chat, record) = Open();
        var list = Sent(chat.Sessions("s1", "viewer-a")).Single();
        Assert.Equal(("thread/list", @"C:\work", "updated_at", true), (Method(list), list.GetProperty("params").GetProperty("cwd").GetString(), list.GetProperty("params").GetProperty("sortKey").GetString(), list.GetProperty("params").GetProperty("useStateDbOnly").GetBoolean()));
        var listed = chat.FromCli(J(Response(Id(list), "{\"data\":[{\"id\":\"" + Thread + "\",\"name\":null,\"preview\":\"the one open here\",\"updatedAt\":300},{\"id\":\"" + Other + "\",\"name\":\"Named thread\",\"preview\":\"p\",\"updatedAt\":200},"
            + "{\"id\":\"33333333-3333-4333-8333-333333333333\",\"name\":null,\"preview\":\"first   words\\nof it\",\"updatedAt\":100},{\"id\":\"44444444-4444-4444-8444-444444444444\",\"name\":null,\"preview\":\"\",\"updatedAt\":50}]}")), record, T0).Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a"), (listed.Kind, listed.Tag));
        var sessions = Of(listed).GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal(new[] { (Other, "Named thread", 200000L), ("33333333-3333-4333-8333-333333333333", "first words of it", 100000L) }, sessions.Select(s => (s.GetProperty("id").GetString()!, s.GetProperty("title").GetString()!, s.GetProperty("at").GetInt64())));
        Assert.Equal(Thread, Of(listed).GetProperty("current").GetString());

        // Not something to move to: told why, and nothing is asked of the app server.
        foreach (var (session, why) in new[] { ("..\\elsewhere", "not in this folder"), (Thread, "the one open here") })
        {
            var refused = chat.Resume("r0", session, "viewer-a").Single();
            Assert.Equal(ChatEffectKind.Reply, refused.Kind);
            Assert.Contains(why, Of(refused).GetProperty("error").GetString());
        }
        chat.FromViewer(J(UserSays("busy now")), record, T0);
        Assert.Contains("Stop the current turn first", Of(chat.Resume("r0", Other, "viewer-a").Single()).GetProperty("error").GetString());
        chat.FromCli(J(Note("turn/completed", "{\"threadId\":\"" + Thread + "\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\"}}")), record, T0);

        var resume = Sent(chat.Resume("r1", Other, "viewer-a")).Single();
        Assert.Equal(("thread/resume", Other), (Method(resume), resume.GetProperty("params").GetProperty("threadId").GetString()));
        Assert.Contains("already changing", Of(chat.Resume("r2", "33333333-3333-4333-8333-333333333333", "viewer-b").Single()).GetProperty("error").GetString());
        var seed = Sent(chat.FromCli(J(Response(Id(resume), ThreadResult.Replace("THREAD", Other).Replace("\"name\":null", "\"name\":\"Named thread\"").Replace("gpt-6-astra", "gpt-6-luna"))), record, T0)).Single();
        // Nothing a viewer sees has changed yet, and the record still names the conversation being left.
        Assert.Equal(Thread, record.SessionId);
        var served = chat.FromCli(J(Response(Id(seed), Page(null, Turn("o1", "completed", UserItem("ou1", "said there"), AgentItem("oa1", "answered there"))))), record, T0);
        var reset = served.Single(e => e.Kind == ChatEffectKind.Reset).Seed!;
        Assert.Equal((Other, "Named thread", false, 2), (reset.Session, reset.Title, reset.Earlier, reset.Lines.Count));
        // It goes on with the model the conversation was on.
        Assert.Equal((Other, "Named thread", "gpt-6-luna"), (record.SessionId, record.Title, record.Model));

        // News of the conversation left behind is no longer this session's.
        Assert.Empty(chat.FromCli(J(Item("completed", AgentItem("late", "from the old one"))), record, T0));
        Assert.Equal(Other, Sent(chat.FromViewer(J(UserSays("here now")), record, T0)).Single().GetProperty("params").GetProperty("threadId").GetString());

        // One that will not come up: the asker is told, and the session stays where it is.
        chat.FromCli(J(Note("turn/completed", "{\"threadId\":\"" + Other + "\",\"turn\":{\"id\":\"t\",\"status\":\"completed\"}}")), record, T0);
        var bad = Sent(chat.Resume("r3", "33333333-3333-4333-8333-333333333333", "viewer-b")).Single();
        var told = chat.FromCli(J(Failure(Id(bad), "thread not found")), record, T0).Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-b", "Could not resume that conversation: thread not found"), (told.Kind, told.Tag, Of(told).GetProperty("error").GetString()));
        Assert.Equal(Other, record.SessionId);
        Assert.Equal("thread/resume", Method(Sent(chat.Resume("r4", Thread, "viewer-b")).Single()));
    }

    [Fact]
    public void A_rename_is_codexs_own_request_and_no_turn()
    {
        var (chat, record) = Open();
        var asked = chat.Rename("n1", "ship the thing", "viewer-a", record);
        var set = Sent(asked).Single();
        Assert.Equal(("thread/name/set", Thread, "ship the thing"), (Method(set), set.GetProperty("params").GetProperty("threadId").GetString(), set.GetProperty("params").GetProperty("name").GetString()));
        Assert.Single(asked);
        Assert.Equal("", record.Title);

        var answered = chat.FromCli(J(Response(Id(set), "{}")), record, T0);
        Assert.Empty(Sent(answered));
        Assert.Empty(Kept(answered));
        var title = Of(answered.Single(e => e.Kind == ChatEffectKind.Frame));
        Assert.Equal(("title", "ship the thing"), (Type(title), title.GetProperty("title").GetString()));
        var reply = answered.Single(e => e.Kind == ChatEffectKind.Reply);
        Assert.Equal(("viewer-a", """{"type":"renamed","request":"n1","ok":true,"name":"ship the thing"}"""), (reply.Tag, Raw(reply)));
        Assert.Equal("ship the thing", record.Title);
        Assert.NotEqual("busy", record.AgentStatus);

        // The first message does not take the title back, and what Codex then says of the name changes nothing.
        var turn = chat.FromViewer(J(UserSays("what is left to do")), record, T0);
        Assert.Equal("turn/start", Method(Sent(turn).Single()));
        Assert.DoesNotContain(chat.FromCli(J(Item("completed", UserItem("u1", "what is left to do"))), record, T0), e => e.Kind == ChatEffectKind.Frame);
        Assert.Equal("ship the thing", record.Title);
        Assert.Empty(chat.FromCli(J(Note("thread/name/updated", "{\"threadId\":\"" + Thread + "\",\"threadName\":\"ship the thing\"}")), record, T0));
        Assert.Equal("ship the thing", record.Title);
    }

    [Fact]
    public void A_rename_answered_after_the_chat_moved_on_does_not_name_the_conversation_it_moved_to()
    {
        var (chat, record) = Open();
        var set = Sent(chat.Rename("n1", "for the one left", "viewer-a", record)).Single();
        var resume = Sent(chat.Resume("r1", Other, "viewer-a")).Single();
        var seed = Sent(chat.FromCli(J(Response(Id(resume), ThreadResult.Replace("THREAD", Other).Replace("\"name\":null", "\"name\":\"Named thread\""))), record, T0)).Single();
        chat.FromCli(J(Response(Id(seed), Page(null, Turn("o1", "completed", UserItem("ou1", "said there"), AgentItem("oa1", "answered there"))))), record, T0);
        Assert.Equal((Other, "Named thread"), (record.SessionId, record.Title));

        var late = chat.FromCli(J(Response(Id(set), "{}")), record, T0);
        Assert.Equal(("viewer-a", """{"type":"renamed","request":"n1","ok":true,"name":"for the one left"}"""), (late.Single().Tag, Raw(late.Single())));
        Assert.Equal("Named thread", record.Title);
    }

    [Fact]
    public void A_rename_codex_refuses_is_told_to_the_viewer_that_asked_and_one_before_any_thread_goes_nowhere()
    {
        var (chat, record) = Open();
        var set = Sent(chat.Rename("n1", "taken", "viewer-a", record)).Single();
        var refused = chat.FromCli(J(Failure(Id(set), "thread name must not be empty")), record, T0).Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-a", """{"type":"renamed","request":"n1","ok":false,"error":"thread name must not be empty"}"""), (refused.Kind, refused.Tag, Raw(refused)));
        Assert.Equal("", record.Title);

        var early = new CodexChat(@"C:\work", "", "", "", CodexChat.Full);
        var none = early.Rename("n2", "too soon", "viewer-b", new HostRecord()).Single();
        Assert.Equal((ChatEffectKind.Reply, "viewer-b", """{"type":"renamed","request":"n2","ok":false,"error":"There is no conversation to name yet."}"""), (none.Kind, none.Tag, Raw(none)));
    }

    [Fact]
    public void A_name_given_to_the_thread_becomes_the_title_and_what_is_not_an_object_is_let_go()
    {
        var (chat, record) = Open();
        var named = chat.FromCli(J(Note("thread/name/updated", "{\"threadId\":\"" + Thread + "\",\"threadName\":\"Retry fix\"}")), record, T0);
        Assert.Equal(("Retry fix", "Retry fix"), (record.Title, Of(named.Single()).GetProperty("title").GetString()));
        // Named: what is said next does not rename it.
        Assert.DoesNotContain(chat.FromCli(J(Item("completed", UserItem("u9", "something else"))), record, T0), e => e.Kind == ChatEffectKind.Frame);
        foreach (string odd in new[] { "[]", "7", "{\"method\":\"turn/started\"}", "{\"method\":\"item/completed\",\"params\":[]}", "{\"id\":999,\"result\":{}}", "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"" + Thread + "\",\"turn\":null}}" })
            chat.FromCli(J(odd), record, T0);
        foreach (string odd in new[] { "{\"type\":\"user\"}", "{\"type\":\"user\",\"message\":7}", "{\"type\":\"control_response\",\"response\":5}", "{\"type\":\"control_request\",\"request\":[]}", "{}" })
            Assert.DoesNotContain(chat.FromViewer(J(odd), record, T0), e => e.Kind == ChatEffectKind.ToCli && Method(Of(e)) != "turn/start");
    }

    [Theory]
    [InlineData("--dangerously-bypass-approvals-and-sandbox", CodexChat.Full)]
    [InlineData("-m gpt-6-astra --yolo", CodexChat.Full)]
    [InlineData("", CodexChat.Ask)]
    [InlineData("--sandbox workspace-write", CodexChat.Ask)]
    public void A_new_codex_chat_asks_first_unless_the_decks_codex_flags_skip_approval(string flags, string mode) =>
        Assert.Equal(mode, HostManager.CodexChatMode(flags));

    [Fact]
    public void Codex_is_found_on_the_path_or_where_its_installer_puts_it_and_the_chat_command_is_its_app_server()
    {
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\tools\codex.exe", @"C:\Users\x\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe" };
        Assert.Equal(@"D:\tools\codex.exe", HostManager.FindCodexExe(@"C:\Windows;""D:\tools"";C:\other", @"C:\Users\x\AppData\Local", exists.Contains));
        Assert.Equal(@"C:\Users\x\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe", HostManager.FindCodexExe(@"C:\Windows", @"C:\Users\x\AppData\Local", exists.Contains));
        Assert.Null(HostManager.FindCodexExe(@"C:\Windows", @"C:\nowhere", exists.Contains));
        var found = HostManager.CodexExeLocator;
        try
        {
            HostManager.CodexExeLocator = () => @"D:\tools\codex.exe";
            Assert.Equal("\"D:\\tools\\codex.exe\" app-server", HostManager.CodexChatCommand());
            Assert.Equal("\"D:\\tools\\codex.exe\" app-server", HostManager.TranslatedChatCommand(SessionProvider.Codex));
            HostManager.CodexExeLocator = () => null;
            Assert.Throws<InvalidOperationException>(() => HostManager.CodexChatCommand());
        }
        finally { HostManager.CodexExeLocator = found; }
    }

    [Fact]
    public void Agy_is_found_on_the_path_or_in_its_own_folder_and_OpenCode_through_its_npm_shim()
    {
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\tools\agy.exe", @"C:\Users\x\AppData\Local\agy\bin\agy.exe" };
        Assert.Equal(@"D:\tools\agy.exe", HostManager.FindOnPath("agy.exe", @"C:\Windows;""D:\tools"";C:\other", exists.Contains, @"C:\Users\x\AppData\Local\agy\bin"));
        Assert.Equal(@"C:\Users\x\AppData\Local\agy\bin\agy.exe", HostManager.FindOnPath("agy.exe", @"C:\Windows; ;", exists.Contains, @"C:\Users\x\AppData\Local\agy\bin"));
        Assert.Null(HostManager.FindOnPath("agy.exe", @"C:\Windows", exists.Contains));

        var npm = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\nvm4w\nodejs\opencode.cmd", @"C:\nvm4w\nodejs\node_modules\@opencode\cli\bin\opencode.exe" };
        Assert.Equal(@"C:\nvm4w\nodejs\node_modules\@opencode\cli\bin\opencode.exe", HostManager.FindOpenCodeExe(@"C:\Windows;C:\nvm4w\nodejs", npm.Contains));
        Assert.Null(HostManager.FindOpenCodeExe(@"C:\Windows;C:\nvm4w\nodejs", p => p.EndsWith("opencode.cmd", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(HostManager.FindOpenCodeExe(@"C:\Windows", npm.Contains));
        var native = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\bin\opencode.exe" };
        Assert.Equal(@"D:\bin\opencode.exe", HostManager.FindOpenCodeExe(@"C:\Windows;D:\bin", native.Contains));
    }
}
