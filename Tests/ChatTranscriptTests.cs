using System.IO;
using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>Earlier conversation paged out of a transcript from its end, and what the scrollback is spared.</summary>
public class ChatTranscriptTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-chattranscript-" + Guid.NewGuid().ToString("N"));

    public ChatTranscriptTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static JsonElement Json(byte[] line) => JsonDocument.Parse(line).RootElement.Clone();
    static string Said(byte[] line) => Json(line).GetProperty("message").GetProperty("content").GetString()!;
    static string User(int i, string pad = "") => "{\"type\":\"user\",\"uuid\":\"msg-" + i + "\",\"parentUuid\":\"msg-" + (i - 1) + "\",\"message\":{\"role\":\"user\",\"content\":\"message " + i + pad + "\"}}";

    string Write(string name, IEnumerable<string> lines, string newline = "\n", bool endNewline = true)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join(newline, lines) + (endNewline ? newline : "")));
        return path;
    }

    [Fact]
    public void Paging_back_from_the_end_visits_every_message_once_in_order()
    {
        var lines = new List<string>();
        for (int i = 1; i <= 120; i++)
        {
            lines.Add(User(i));
            if (i % 7 == 0) lines.Add("{\"type\":\"queue-operation\",\"operation\":\"enqueue\"}");
            if (i % 11 == 0) lines.Add("{\"type\":\"assistant\",\"uuid\":\"side-" + i + "\",\"isSidechain\":true,\"message\":{\"role\":\"assistant\",\"content\":[]}}");
        }
        string path = Write("t.jsonl", lines);

        var seen = new List<string>();
        long before = long.MaxValue;
        int pages = 0;
        while (true)
        {
            var page = ChatTranscript.Before(path, before, 4096, 25);
            pages++;
            Assert.InRange(page.Lines.Count, 1, 25);
            Assert.True(page.Lines.Sum(l => l.Length + 1) <= 4096);
            // Within a page the lines are oldest first, and each says where it sits in the file.
            var places = page.Lines.Select(l => Json(l).GetProperty("at").GetInt64()).ToList();
            Assert.Equal(places.OrderBy(p => p), places);
            seen.InsertRange(0, page.Lines.Select(Said));
            if (page.Next < 0) break;
            Assert.Equal(places[0], page.Next);
            before = page.Next;
            Assert.True(pages < 100, "paging does not end");
        }
        Assert.Equal(Enumerable.Range(1, 120).Select(i => "message " + i), seen);
        Assert.True(pages > 4);

        // The place a line reports is where that line starts.
        var bytes = File.ReadAllBytes(path);
        var last = ChatTranscript.Before(path, long.MaxValue, 1 << 20, 3).Lines;
        foreach (var l in last)
        {
            long at = Json(l).GetProperty("at").GetInt64();
            Assert.StartsWith("{\"type\":\"user\",\"uuid\":\"" + Json(l).GetProperty("uuid").GetString() + "\"", Encoding.UTF8.GetString(bytes, (int)at, 60));
        }
    }

    [Fact]
    public void Lines_longer_than_a_read_and_windows_line_endings_are_read_whole()
    {
        // 700 KB in one line: more than two of the pieces the file is read in.
        string big = new string('x', 700 * 1024);
        string path = Write("big.jsonl", new[] { User(1), User(2, big), User(3), User(4, " é ü 日本") }, newline: "\r\n");

        var all = ChatTranscript.Before(path, long.MaxValue, 8 << 20, 100);
        Assert.Equal(new[] { "message 1", "message 2" + big, "message 3", "message 4 é ü 日本" }, all.Lines.Select(Said));
        Assert.Equal(-1, all.Next);

        // A page too small for the big line still hands it over alone rather than stalling on it.
        var newest = ChatTranscript.Before(path, long.MaxValue, 1000, 100);
        Assert.Equal(new[] { "message 3", "message 4 é ü 日本" }, newest.Lines.Select(Said));
        var middle = ChatTranscript.Before(path, newest.Next, 1000, 100);
        Assert.Equal(new[] { "message 2" + big }, middle.Lines.Select(Said));
        var oldest = ChatTranscript.Before(path, middle.Next, 1000, 100);
        Assert.Equal(new[] { "message 1" }, oldest.Lines.Select(Said));
        Assert.Equal(-1, oldest.Next);
    }

    [Fact]
    public void A_line_still_being_written_is_left_for_later_and_a_missing_or_empty_file_is_no_conversation()
    {
        string path = Write("live.jsonl", new[] { User(1), User(2), "{\"type\":\"user\",\"uuid\":\"msg-3\",\"message\":{\"role\":\"user\",\"content\":\"half wri" }, endNewline: false);
        Assert.Equal(new[] { "message 1", "message 2" }, ChatTranscript.Before(path, long.MaxValue, 1 << 20, 100).Lines.Select(Said));

        Assert.Empty(ChatTranscript.Before(Path.Combine(_dir, "nowhere.jsonl"), long.MaxValue, 1 << 20, 100).Lines);
        Assert.Empty(ChatTranscript.Before("", long.MaxValue, 1 << 20, 100).Lines);
        Assert.Empty(ChatTranscript.Before(Write("empty.jsonl", Array.Empty<string>(), endNewline: false), long.MaxValue, 1 << 20, 100).Lines);
        Assert.Empty(ChatTranscript.Before(path, 0, 1 << 20, 100).Lines);
        // A place past the end means the end.
        Assert.Equal(2, ChatTranscript.Before(path, 1L << 40, 1 << 20, 100).Lines.Count);
    }

    [Fact]
    public void A_message_is_found_by_its_own_uuid_wherever_it_sits_and_not_by_a_mention_of_it()
    {
        var lines = new List<string>();
        string filler = new string('f', 9000);
        for (int i = 1; i <= 90; i++) lines.Add(User(i, filler));
        // A later line quotes msg-40's uuid inside its text, and names it as its parent: neither is msg-40.
        lines.Add("{\"type\":\"user\",\"uuid\":\"msg-91\",\"parentUuid\":\"msg-40\",\"message\":{\"role\":\"user\",\"content\":\"see \\\"uuid\\\":\\\"msg-40\\\" above\"}}");
        lines.Add("{\"type\":\"attachment\",\"uuid\":\"att-1\",\"quoted\":{\"uuid\":\"msg-40\"}}");
        string path = Write("find.jsonl", lines);
        var bytes = File.ReadAllBytes(path);

        foreach (int i in new[] { 1, 2, 28, 40, 57, 90 })
        {
            long at = ChatTranscript.Find(path, "msg-" + i);
            Assert.True(at >= 0, "msg-" + i);
            Assert.StartsWith("{\"type\":\"user\",\"uuid\":\"msg-" + i + "\"", Encoding.UTF8.GetString(bytes, (int)at, 40));
            if (i > 1) Assert.Equal((byte)'\n', bytes[at - 1]);
        }
        // What comes before a found message is the page a viewer asked for.
        var before40 = ChatTranscript.Before(path, ChatTranscript.Find(path, "msg-40"), 1 << 20, 2);
        Assert.Equal(new[] { "message 38" + filler, "message 39" + filler }, before40.Lines.Select(Said));

        Assert.Equal(-1, ChatTranscript.Find(path, "msg-999"));
        Assert.Equal(-1, ChatTranscript.Find(path, ""));
        Assert.Equal(-1, ChatTranscript.Find(path, "msg-4\"} not a uuid"));
        Assert.Equal(-1, ChatTranscript.Find(Path.Combine(_dir, "nowhere.jsonl"), "msg-1"));
    }

    [Fact]
    public void A_place_counts_only_in_the_file_where_that_message_is_at_it()
    {
        // The conversation moved to a new file under the host; the viewer's oldest message is from the first one.
        string first = Write("first.jsonl", Enumerable.Range(1, 6).Select(i => User(i, "")).ToList());
        string second = Write("second.jsonl", Enumerable.Range(101, 6).Select(i => User(i, "")).ToList());
        long at4 = ChatTranscript.Find(first, "msg-4");
        Assert.True(ChatTranscript.Holds(first, at4, "msg-4"));
        Assert.False(ChatTranscript.Holds(second, at4, "msg-4"));
        Assert.False(ChatTranscript.Holds(first, at4 + 1, "msg-4"));
        Assert.False(ChatTranscript.Holds(first, at4, "msg-5"));
        Assert.False(ChatTranscript.Holds(first, 1L << 40, "msg-4"));
        Assert.False(ChatTranscript.Holds(Path.Combine(_dir, "nowhere.jsonl"), at4, "msg-4"));

        // The file in use now is listed first, and the place is still read in the file it belongs to.
        var files = new[] { second, first };
        Assert.Equal(new[] { "message 1", "message 2", "message 3" }, ChatTranscript.BeforeMessage(files, at4, "msg-4", 1 << 20, 100).Lines.Select(Said));
        // A place that is that message's nowhere: the message is looked for by its uuid.
        Assert.Equal(new[] { "message 101", "message 102" }, ChatTranscript.BeforeMessage(files, at4 + 7, "msg-103", 1 << 20, 100).Lines.Select(Said));
        // By uuid alone, as for a message that arrived live; one found nowhere gets nothing.
        Assert.Equal(new[] { "message 1" }, ChatTranscript.BeforeMessage(files, -1, "msg-2", 1 << 20, 100).Lines.Select(Said));
        Assert.Empty(ChatTranscript.BeforeMessage(files, at4, "msg-999", 1 << 20, 100).Lines);
        // By place alone (a line with no uuid), in the file in use.
        Assert.Equal(3, ChatTranscript.BeforeMessage(new[] { "", first, second }, at4, "", 1 << 20, 100).Lines.Count);
        Assert.Empty(ChatTranscript.BeforeMessage(new[] { "" }, at4, "", 1 << 20, 100).Lines);
    }

    [Fact]
    public void The_scrollback_is_spared_what_no_viewer_reads()
    {
        string whole = new string('w', 60_000);
        // The structured copy beside a tool result (for an edit, the file as it was) and its bookkeeping go; the result stays.
        var edit = Encoding.UTF8.GetBytes("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"The file was updated.\"}]},\"parent_tool_use_id\":null,\"tool_use_result\":{\"originalFile\":\"" + whole + "\"},\"tool_result_meta\":[{\"id\":\"t1\"}],\"uuid\":\"u1\"}");
        var kept = Json(ChatStream.ForRing(edit));
        Assert.False(kept.TryGetProperty("tool_use_result", out _));
        Assert.False(kept.TryGetProperty("tool_result_meta", out _));
        Assert.Equal("The file was updated.", kept.GetProperty("message").GetProperty("content")[0].GetProperty("content").GetString());
        Assert.Equal("u1", kept.GetProperty("uuid").GetString());
        Assert.True(ChatStream.ForRing(edit).Length < 400);

        // What a subagent read along the way is cut to its beginning; what the session itself read is not.
        string read = new string('r', 8000);
        string result = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t2\",\"content\":\"" + read + "\"}]},\"parent_tool_use_id\":PARENT}";
        var sub = Json(ChatStream.ForRing(Encoding.UTF8.GetBytes(result.Replace("PARENT", "\"toolu_agent\""))));
        string cut = sub.GetProperty("message").GetProperty("content")[0].GetProperty("content").GetString()!;
        Assert.Equal(1501, cut.Length);
        Assert.EndsWith("…", cut);
        var main = Encoding.UTF8.GetBytes(result.Replace("PARENT", "null"));
        Assert.Same(main, ChatStream.ForRing(main));

        // A subagent's result given as blocks, and a long input it passed to a tool, are cut the same way.
        var blocks = Json(ChatStream.ForRing(Encoding.UTF8.GetBytes("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t3\",\"content\":[{\"type\":\"text\",\"text\":\"" + read + "\"}]}]},\"parent_tool_use_id\":\"toolu_agent\"}")));
        Assert.Equal(1501, blocks.GetProperty("message").GetProperty("content")[0].GetProperty("content").GetString()!.Length);
        var wrote = Json(ChatStream.ForRing(Encoding.UTF8.GetBytes("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t4\",\"name\":\"Write\",\"input\":{\"file_path\":\"a.txt\",\"content\":\"" + read + "\"}}]},\"parent_tool_use_id\":\"toolu_agent\"}")));
        var input = wrote.GetProperty("message").GetProperty("content")[0].GetProperty("input");
        Assert.Equal(("a.txt", 1501), (input.GetProperty("file_path").GetString(), input.GetProperty("content").GetString()!.Length));

        // An image in the session's own message still loses only its bytes, and a line with nothing to spare is left as it was.
        var image = Json(ChatStream.ForRing(Encoding.UTF8.GetBytes("{\"type\":\"user\",\"isReplay\":true,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + new string('A', 5000) + "\"}}]}}")));
        Assert.Equal(5000, image.GetProperty("message").GetProperty("content")[0].GetProperty("source").GetProperty("omitted").GetInt32());
        var plain = Encoding.UTF8.GetBytes("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"about tool_use_result\"}]},\"parent_tool_use_id\":null}");
        Assert.Same(plain, ChatStream.ForRing(plain));
        var broken = Encoding.UTF8.GetBytes("{\"tool_use_result\" not json");
        Assert.Same(broken, ChatStream.ForRing(broken));
    }

    [Fact]
    public void A_restart_puts_the_mode_and_effort_back_and_the_model_goes_on_the_command_line()
    {
        var both = ChatStream.RestoreLines("plan", "low").Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        Assert.Equal(2, both.Count);
        Assert.Equal(("set_permission_mode", "plan"), (both[0].GetProperty("request").GetProperty("subtype").GetString(), both[0].GetProperty("request").GetProperty("mode").GetString()));
        Assert.Equal(("apply_flag_settings", "low"), (both[1].GetProperty("request").GetProperty("subtype").GetString(), both[1].GetProperty("request").GetProperty("settings").GetProperty("effortLevel").GetString()));
        // Named like a viewer's change, so the answers are read the same way.
        Assert.All(both, l => Assert.StartsWith(ChatStream.SetPrefix, l.GetProperty("request_id").GetString()));
        Assert.Empty(ChatStream.RestoreLines("", ""));
        Assert.Single(ChatStream.RestoreLines("", "high"));

        var found = HostManager.ClaudeExeLocator;
        try
        {
            HostManager.ClaudeExeLocator = () => @"C:\Tools\claude.exe";
            // Last, where it wins over a default model named in the deck's flags.
            Assert.Equal($"\"C:\\Tools\\claude.exe\" {ChatStream.ProtocolFlags} --resume abc --dangerously-skip-permissions --model opus --model \"claude-haiku-5-5\"",
                HostManager.ResumeClaudeChatCommand("abc", "--dangerously-skip-permissions --model opus", "claude-haiku-5-5"));
            Assert.DoesNotContain("--model", HostManager.ResumeClaudeChatCommand("abc", "--dangerously-skip-permissions"));
        }
        finally { HostManager.ClaudeExeLocator = found; }

        // The record carries what the session was last reported to be in.
        var chat = new ChatStream();
        var record = new HostRecord();
        chat.Apply(JsonDocument.Parse("""{"type":"control_response","response":{"subtype":"success","request_id":"sd-set-1","response":{"mode":"plan"}}}""").RootElement, record, DateTime.UtcNow);
        var change = chat.Apply(JsonDocument.Parse("""{"type":"control_response","response":{"subtype":"success","request_id":"sd-settings","response":{"applied":{"model":"m","effort":"high"}}}}""").RootElement, record, DateTime.UtcNow);
        Assert.True(change.Record);
        Assert.Equal(("plan", "high", "m"), (record.PermissionMode, record.Effort, record.Model));

        // And that is what its replacement is started with; a terminal has nothing of the kind to hand on.
        record.Kind = ChatStream.Kind;
        Assert.Equal(new HostManager.ChatRestore("plan", "high"), HostManager.ChatRestore.Of(record));
        Assert.Null(HostManager.ChatRestore.Of(new HostRecord { PermissionMode = "plan", Effort = "high" }));
    }
}
