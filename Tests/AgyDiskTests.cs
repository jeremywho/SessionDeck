using System.IO;
using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>What Antigravity keeps on disk about a conversation, in the shapes it was measured to write (1.5.13), with made-up content.</summary>
public class AgyDiskTests : IDisposable
{
    const string Talk = "11111111-2222-4333-8444-555555555555", Helper = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", Other = "99999999-8888-4777-8666-555555555555";
    readonly string _root = Path.Combine(Path.GetTempPath(), "sd-agydisk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    string Kept(string conversation, params string[] under) => Path.Combine(new[] { _root, conversation, ".system_generated" }.Concat(under).ToArray());

    void Transcript(string conversation, string text)
    {
        Directory.CreateDirectory(Kept(conversation, "logs"));
        File.WriteAllText(Kept(conversation, "logs", "transcript.jsonl"), text, new UTF8Encoding(false));
    }

    internal static string Asked(int index, string at, string words) =>
        JsonSerializer.Serialize(new { step_index = index, source = "USER_EXPLICIT", type = "USER_INPUT", status = "DONE", created_at = at, content = "<USER_REQUEST>\n" + words + "\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nThe current local time is: somewhen.\n</ADDITIONAL_METADATA>" });

    internal static string Planned(int index, string at, string content = "", string thinking = "", params (string Name, string Arg, string Value)[] calls)
    {
        var step = new Dictionary<string, object?> { ["step_index"] = index, ["source"] = "MODEL", ["type"] = "PLANNER_RESPONSE", ["status"] = "DONE", ["created_at"] = at, ["model"] = "some-model" };
        if (thinking.Length > 0) step["thinking"] = thinking;
        if (content.Length > 0) step["content"] = content;
        if (calls.Length > 0) step["tool_calls"] = calls.Select(c => new { name = c.Name, args = new Dictionary<string, string> { [c.Arg] = c.Value } }).ToArray();
        return JsonSerializer.Serialize(step);
    }

    internal static string Outcome(int index, string at, string content) =>
        JsonSerializer.Serialize(new { step_index = index, source = "MODEL", type = "GENERIC", status = "DONE", created_at = at, content });

    internal static string Notice(int index, string at, string content) =>
        JsonSerializer.Serialize(new { step_index = index, source = "SYSTEM", type = "SYSTEM_MESSAGE", status = "DONE", created_at = at, content });

    // ---- a step ----

    [Fact]
    public void A_step_is_read_with_its_time_its_words_and_its_calls()
    {
        var asked = AgyStep.Parse(Encoding.UTF8.GetBytes(Asked(0, "2026-03-04T10:00:05Z", "count the files")))!;
        Assert.Equal((0, AgyStep.UserInput, new DateTime(2026, 3, 4, 10, 0, 5, DateTimeKind.Utc)), (asked.Index, asked.Type, asked.At!.Value));
        Assert.Equal(DateTimeKind.Utc, asked.At.Value.Kind);
        Assert.Contains("count the files", asked.Content);

        // An argument is written as JSON inside a string; one that is not is taken as it stands.
        var planned = AgyStep.Parse(Encoding.UTF8.GetBytes(Planned(1, "2026-03-04T10:00:06Z", thinking: "I will list them.", calls: new[] { ("run_command", "CommandLine", "\"dir /b\""), ("view_file", "AbsolutePath", "C:\\x.txt") })))!;
        Assert.Equal(("I will list them.", ""), (planned.Thinking, planned.Content));
        Assert.Equal(new[] { ("run_command", "dir /b"), ("view_file", "C:\\x.txt") }, planned.Calls.Select(c => (c.Name, c.Args.Values.Single())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"step_index\":3}")]
    [InlineData("{\"type\":7}")]
    public void What_is_not_a_step_is_none(string line) => Assert.Null(AgyStep.Parse(Encoding.UTF8.GetBytes(line)));

    [Fact]
    public void A_step_with_no_time_that_reads_has_none()
    {
        Assert.Null(AgyStep.Parse(Encoding.UTF8.GetBytes("{\"type\":\"GENERIC\",\"created_at\":\"lately\"}"))!.At);
        Assert.Equal(-1, AgyStep.Parse(Encoding.UTF8.GetBytes("{\"type\":\"GENERIC\"}"))!.Index);
    }

    // ---- the transcript ----

    [Fact]
    public void A_transcript_is_read_a_whole_line_at_a_time_from_where_the_last_look_ended()
    {
        var disk = new AgyDisk(_root);
        string first = Asked(0, "2026-03-04T10:00:05Z", "hello") + "\n", second = Planned(1, "2026-03-04T10:00:06Z", "hi") + "\n", third = Planned(2, "2026-03-04T10:00:09Z", "still here");
        Transcript(Talk, first + second + third[..20]);
        long at = 0;
        Assert.Equal(new[] { 0, 1 }, disk.Since(Talk, ref at).Select(s => s.Index));
        Assert.Equal(Encoding.UTF8.GetByteCount(first + second), at);
        // Nothing more is whole yet.
        Assert.Empty(disk.Since(Talk, ref at));
        Assert.Equal(Encoding.UTF8.GetByteCount(first + second), at);

        Transcript(Talk, first + second + third + "\n");
        Assert.Equal(new[] { 2 }, disk.Since(Talk, ref at).Select(s => s.Index));
        Assert.Empty(disk.Since(Talk, ref at));
        Assert.Equal(new[] { 0, 1, 2 }, disk.Steps(Talk).Select(s => s.Index));
    }

    [Fact]
    public void A_line_that_is_not_a_step_is_passed_over_and_the_rest_is_read()
    {
        Transcript(Talk, Asked(0, "2026-03-04T10:00:05Z", "hello") + "\nnot a step\n" + Planned(2, "2026-03-04T10:00:06Z", "hi") + "\n");
        Assert.Equal(new[] { 0, 2 }, new AgyDisk(_root).Steps(Talk).Select(s => s.Index));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\" + Talk)]
    [InlineData("C:")]
    [InlineData("")]
    [InlineData("ses_e2ae77411ffe3oE5bZaI08qWmI")]
    public void Only_a_conversation_id_is_ever_made_into_a_path(string id)
    {
        // A transcript sits where each of these would lead, were it followed.
        Transcript(Talk, Asked(0, "2026-03-04T10:00:05Z", "hello") + "\n");
        Directory.CreateDirectory(Path.Combine(_root, ".system_generated", "logs"));
        File.Copy(Kept(Talk, "logs", "transcript.jsonl"), Path.Combine(_root, ".system_generated", "logs", "transcript.jsonl"));
        var disk = new AgyDisk(Path.Combine(_root, Talk));
        Assert.Empty(disk.Steps(id));
        Assert.Empty(disk.Subagents(id));
        Assert.False(AgyDisk.IsConversation(id));
    }

    [Fact]
    public void A_conversation_with_nothing_on_disk_has_no_steps_and_no_subagents()
    {
        var disk = new AgyDisk(_root);
        Assert.Empty(disk.Steps(Talk));
        Assert.Empty(disk.Subagents(Talk));
        Assert.Empty(new AgyDisk(Path.Combine(_root, "nowhere")).Steps(Talk));
    }

    // ---- subagents ----

    [Fact]
    public void Subagents_are_the_ones_the_conversation_started_in_the_order_it_started_them()
    {
        Directory.CreateDirectory(Kept(Talk, "subagents"));
        string Entry(string id, string role, int spawn, string state = "SUBAGENT_STATE_ALIVE") =>
            "{\"conversationId\":\"" + id + "\", \"subagentDescriptor\":{\"typeName\":\"self\", \"role\":\"" + role + "\"}, \"state\":\"" + state + "\", \"spawnStepIndex\":" + spawn + ", \"workspaceUris\":[\"file:///C:/somewhere\"]}";
        File.WriteAllText(Kept(Talk, "subagents", Other + ".json"), Entry(Other, "Second", 9, "SUBAGENT_STATE_KILLED"));
        File.WriteAllText(Kept(Talk, "subagents", Helper + ".json"), Entry(Helper, "First", 2));
        File.WriteAllText(Kept(Talk, "subagents", "half.json"), "{\"conversationId\":\"" + Helper);
        File.WriteAllText(Kept(Talk, "subagents", "elsewhere.json"), Entry("..\\\\..\\\\x", "Nobody", 1));
        File.WriteAllText(Kept(Talk, "subagents", "notes.txt"), "not one");

        var found = new AgyDisk(_root).Subagents(Talk);
        Assert.Equal(new[] { (Helper, "First", "self", "SUBAGENT_STATE_ALIVE", 2), (Other, "Second", "self", "SUBAGENT_STATE_KILLED", 9) }, found.Select(s => (s.Id, s.Role, s.TypeName, s.State, s.SpawnStep)));
    }
}
