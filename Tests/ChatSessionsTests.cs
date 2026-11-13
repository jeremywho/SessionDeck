using System.IO;
using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>The conversations a chat can move to: which transcripts are listed, what they are called, and what never reaches a path or a command line.</summary>
public class ChatSessionsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-chatsessions-" + Guid.NewGuid().ToString("N"));

    public ChatSessionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    const string A = "11111111-1111-4111-8111-111111111111", B = "22222222-2222-4222-8222-222222222222",
        C = "33333333-3333-4333-8333-333333333333", D = "44444444-4444-4444-8444-444444444444", E = "55555555-5555-4555-8555-555555555555";

    static string User(string text) => JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } });
    static string Reply(string text) => JsonSerializer.Serialize(new { type = "assistant", message = new { role = "assistant", content = new[] { new { type = "text", text } } } });

    string Write(string name, DateTime written, params string[] lines)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    static readonly DateTime T0 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    static readonly HashSet<string> Nobody = new();

    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111", true)]
    [InlineData("11111111-1111-4111-8111-11111111111", false)]
    [InlineData("{11111111-1111-4111-8111-111111111111}", false)]
    [InlineData("11111111111141118111111111111111", false)]
    [InlineData("..\\..\\somewhere\\else-1111-4111-8111", false)]
    [InlineData("11111111-1111-4111-8111-111111111111\" --dangerously", false)]
    [InlineData("", false)]
    public void Only_an_id_as_the_cli_writes_one_is_a_session_id(string id, bool valid) =>
        Assert.Equal(valid, ChatSessions.IsSessionId(id));

    [Fact]
    public void A_transcript_is_found_only_by_a_session_id_and_only_in_the_folder()
    {
        string path = Write(A + ".jsonl", T0, User("hello"));
        Write("notes.jsonl", T0, User("not a session"));
        Assert.Equal(path, ChatSessions.TranscriptOf(_dir, A));
        Assert.Equal("", ChatSessions.TranscriptOf(_dir, B));
        Assert.Equal("", ChatSessions.TranscriptOf(_dir, "notes"));
        Assert.Equal("", ChatSessions.TranscriptOf(_dir, "..\\" + Path.GetFileName(_dir) + "\\" + A));
        Assert.Equal("", ChatSessions.TranscriptOf("", A));
    }

    [Fact]
    public void The_command_is_the_spawners_resume_form_with_the_id_in_place_and_the_chats_model_last()
    {
        string template = "\"C:\\Tools\\claude.exe\" -p --resume " + ChatSessions.SessionPlaceholder + " --dangerously-skip-permissions --model opus";
        Assert.Equal("\"C:\\Tools\\claude.exe\" -p --resume " + B + " --dangerously-skip-permissions --model opus --model \"claude-haiku-5-5\"",
            ChatSessions.Command(template, B, "claude-haiku-5-5"));
        Assert.Equal("\"C:\\Tools\\claude.exe\" -p --resume " + B + " --dangerously-skip-permissions --model opus", ChatSessions.Command(template, B, ""));
    }

    [Fact]
    public void Conversations_are_listed_newest_first_by_the_name_they_have_without_the_one_open_here()
    {
        Write(A + ".jsonl", T0.AddMinutes(5), User("the one open here"), Reply("ok"));
        // Named by the person: that wins over the name the CLI made up and over what was said first.
        Write(B + ".jsonl", T0.AddMinutes(1), User("first words of b"), Reply("ok"),
            "{\"type\":\"ai-title\",\"aiTitle\":\"Made up by the CLI\"}", "{\"type\":\"custom-title\",\"customTitle\":\"Named by me\"}", "{\"type\":\"last-prompt\"}");
        Write(C + ".jsonl", T0.AddMinutes(3), User("first words of c"), Reply("ok"), "{\"type\":\"ai-title\",\"aiTitle\":\"Fix the   login\\n bug\"}");
        // Nothing but what was said: a note for the model and a tool result are not what a person said.
        Write(D + ".jsonl", T0.AddMinutes(2),
            "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"a note for the model\"}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"<command-name>/model</command-name>\"}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":\"x\"}]}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"  what a person\\n typed  \"}]}}", Reply("ok"));
        // Nobody spoke: a slash command alone wrote this.
        Write(E + ".jsonl", T0.AddMinutes(9), "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"content\":\"/resume\"}", "{\"type\":\"system\",\"subtype\":\"local_command\"}");
        Write("notes.jsonl", T0.AddMinutes(8), User("not a session"));
        Directory.CreateDirectory(Path.Combine(_dir, C));
        File.WriteAllText(Path.Combine(_dir, C, "agent-1.jsonl"), User("a subagent's own transcript") + "\n");

        var list = ChatSessions.List(_dir, A, new HashSet<string> { D });
        Assert.Equal(new[] { (C, "Fix the login bug", false), (D, "what a person typed", true), (B, "Named by me", false) }, list.Select(e => (e.Id, e.Title, e.Live)));
        Assert.Equal(new DateTimeOffset(T0.AddMinutes(3)).ToUnixTimeMilliseconds(), list[0].At);
        // With nothing open here the first one is listed too; the newest win when there are more than fit.
        Assert.Equal(new[] { A, C }, ChatSessions.List(_dir, "", Nobody, max: 2).Select(e => e.Id));
        Assert.Empty(ChatSessions.List(Path.Combine(_dir, "nowhere"), A, Nobody));
        Assert.Empty(ChatSessions.List("", A, Nobody));
    }

    [Fact]
    public void A_long_transcript_is_named_from_its_two_ends_and_a_long_first_message_is_cut()
    {
        // The name is near the end and the first words near the start; megabytes lie between that are never read.
        string filler = Reply(new string('x', 700 * 1024));
        string named = Write(A + ".jsonl", T0, User("where it began"), filler, Reply("ok"), "{\"type\":\"ai-title\",\"aiTitle\":\"Named at the end\"}");
        Assert.Equal("Named at the end", ChatSessions.Title(named));
        string unnamed = Write(B + ".jsonl", T0, User("where it began"), filler, Reply("ok"));
        Assert.Equal("where it began", ChatSessions.Title(unnamed));
        string wordy = Write(C + ".jsonl", T0, User(new string('w', 500)));
        Assert.Equal(new string('w', 140) + "…", ChatSessions.Title(wordy));
        // A first message too long to read whole (an image) still marks a conversation; it just has no name.
        string image = Write(D + ".jsonl", T0, User(new string('i', 400 * 1024)));
        Assert.Equal("", ChatSessions.Title(image));
        Assert.Null(ChatSessions.Title(Write(E + ".jsonl", T0, "{\"type\":\"system\",\"subtype\":\"local_command\"}")));
        Assert.Null(ChatSessions.Title(Path.Combine(_dir, "nowhere.jsonl")));
    }

    [Fact]
    public void A_conversation_another_running_cli_has_open_is_marked_and_this_chats_own_cli_does_not_count()
    {
        string registry = Path.Combine(_dir, "sessions");
        Directory.CreateDirectory(registry);
        File.WriteAllText(Path.Combine(registry, "100.json"), JsonSerializer.Serialize(new { pid = 100, sessionId = A }));
        File.WriteAllText(Path.Combine(registry, "200.json"), JsonSerializer.Serialize(new { pid = 200, sessionId = B }));
        File.WriteAllText(Path.Combine(registry, "300.json"), JsonSerializer.Serialize(new { pid = 300, sessionId = C }));
        File.WriteAllText(Path.Combine(registry, "400.json"), "{ not json");
        File.WriteAllText(Path.Combine(registry, "500.json"), JsonSerializer.Serialize(new { sessionId = D }));
        // 100 is this chat's own CLI, 300 has ended and left its file behind.
        var live = ChatSessions.LiveElsewhere(registry, ownChildPid: 100, alive: pid => pid != 300);
        Assert.Equal(new[] { B }, live);
        Assert.Contains(B.ToUpperInvariant(), live);
        Assert.Empty(ChatSessions.LiveElsewhere(Path.Combine(_dir, "nowhere"), 1, _ => true));
        // The real check: this test's own process is running, and no process has pid 0x7FFFFFF0.
        File.WriteAllText(Path.Combine(registry, "600.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, sessionId = E }));
        File.WriteAllText(Path.Combine(registry, "200.json"), JsonSerializer.Serialize(new { pid = 0x7FFFFFF0, sessionId = B }));
        File.Delete(Path.Combine(registry, "300.json"));
        Assert.Equal(new[] { E }, ChatSessions.LiveElsewhere(registry, ownChildPid: 100));
    }

    [Fact]
    public void The_list_and_a_refusal_are_frames_a_viewer_can_match_to_what_it_asked()
    {
        using var list = JsonDocument.Parse(ChatSessions.ListFrame("q7", A, new List<ChatSessions.Entry> { new(B, "Named \"by\" me", 1234, true) }));
        Assert.Equal(("sessions", "q7", A), (list.RootElement.GetProperty("type").GetString(), list.RootElement.GetProperty("request").GetString(), list.RootElement.GetProperty("current").GetString()));
        var only = list.RootElement.GetProperty("sessions")[0];
        Assert.Equal((B, "Named \"by\" me", 1234L, true), (only.GetProperty("id").GetString(), only.GetProperty("title").GetString(), only.GetProperty("at").GetInt64(), only.GetProperty("live").GetBoolean()));
        using var refused = JsonDocument.Parse(ChatSessions.RefusedFrame("q8", "Stop the current turn first."));
        Assert.Equal(("resumed", "q8", false, "Stop the current turn first."), (refused.RootElement.GetProperty("type").GetString(), refused.RootElement.GetProperty("request").GetString(),
            refused.RootElement.GetProperty("ok").GetBoolean(), refused.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public void An_emptied_scrollback_goes_on_counting_from_where_it_was()
    {
        var ring = new Ring(64);
        ring.Append(Encoding.ASCII.GetBytes("0123456789"));
        Assert.Equal(10, ring.Clear());
        Assert.Equal((10L, 10L), (ring.Start, ring.End));
        // A viewer that had seen all ten bytes, or only four, is answered from the same place: nothing of before is left.
        Assert.Equal((10L, 0), (ring.Since(10).Start, ring.Since(10).Data.Length));
        Assert.Equal((10L, 0), (ring.Since(4).Start, ring.Since(4).Data.Length));
        ring.Append(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal((10L, "abc"), (ring.Since(0).Start, Encoding.ASCII.GetString(ring.Since(0).Data)));
        Assert.Equal(13, ring.End);
        // Emptied again after it has wrapped: still nothing held, still counting.
        ring.Append(new byte[100]);
        Assert.Equal(113, ring.Clear());
        ring.Append(Encoding.ASCII.GetBytes("xy"));
        Assert.Equal((113L, "xy"), (ring.Since(50).Start, Encoding.ASCII.GetString(ring.Since(50).Data)));
    }
}
