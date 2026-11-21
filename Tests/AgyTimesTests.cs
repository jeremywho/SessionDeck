using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;
using static SessionDeck.Tests.AgyDiskTests;

namespace SessionDeck.Tests;

/// <summary>
/// A replayed Antigravity conversation given the times its transcript on disk has. The shapes are the
/// measured ones (1.5.13): the replay as the host's lines, the transcript as steps; the words are made up.
/// </summary>
public class AgyTimesTests
{
    static byte[] User(string id, string text) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["type"] = "user", ["uuid"] = id, ["isReplay"] = true, ["message"] = new { role = "user", content = text }, ["parent_tool_use_id"] = null, ["history"] = true });
    static byte[] Agent(string id, object block) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["type"] = "assistant", ["uuid"] = id, ["message"] = new { role = "assistant", content = new[] { block } }, ["parent_tool_use_id"] = null, ["history"] = true });
    static byte[] Text(string id, string text) => Agent(id, new { type = "text", text });
    static byte[] Thought(string id, string text) => Agent(id, new { type = "thinking", thinking = text });
    static byte[] Called(string id, string name) => Agent(id, new { type = "tool_use", id, name, input = new { command = "dir" } });
    static byte[] Returned(string id) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["type"] = "user", ["uuid"] = id + ":r", ["message"] = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = id, content = "ok", is_error = false } } }, ["parent_tool_use_id"] = null, ["history"] = true });

    static List<AgyStep> Steps(params string[] lines) => lines.Select(l => AgyStep.Parse(Encoding.UTF8.GetBytes(l))!).ToList();
    static JsonElement J(byte[] line) => JsonDocument.Parse(line).RootElement.Clone();
    static string? When(byte[] line) => J(line).TryGetProperty("timestamp", out var t) ? t.GetString() : null;
    static string Id(byte[] line) => J(line).GetProperty("uuid").GetString()!;
    static string Words(byte[] line) => J(line).GetProperty("message").GetProperty("content") is { ValueKind: JsonValueKind.String } s ? s.GetString()! : J(line).GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;
    static string At(string clock) => "2026-03-04T" + clock + "Z";
    static string Stamp(string clock) => "2026-03-04T" + clock + ".000Z";

    [Fact]
    public void What_was_asked_thought_and_said_gets_the_time_of_the_step_that_holds_it()
    {
        var lines = new[] { User("u-1", "count the files"), Thought("a-1", "I will list them first."), Called("call_1", "Shell"), Returned("call_1"), Text("a-2", "There are 12 files.") };
        var steps = Steps(
            Asked(0, At("10:00:05"), "count the files"),
            Planned(1, At("10:00:06"), thinking: "I will list them first.", calls: new[] { ("run_command", "CommandLine", "\"dir\"") }),
            Outcome(2, At("10:00:09"), "Created At: now\n12 files"),
            Planned(3, At("10:00:11"), "There are 12 files."));

        var stamped = AgyTimes.Stamp(lines, steps);
        Assert.Equal(new[] { Stamp("10:00:05"), Stamp("10:00:06"), null, null, Stamp("10:00:11") }, stamped.Select(When));
        // A call and what it came back with are as they were.
        Assert.Same(lines[2], stamped[2]);
        Assert.Same(lines[3], stamped[3]);
        // Nothing else about a line changes.
        Assert.Equal(new[] { "u-1", "a-1", "call_1", "call_1:r", "a-2" }, stamped.Select(Id));
        Assert.True(J(stamped[4]).GetProperty("history").GetBoolean());
    }

    [Fact]
    public void What_the_person_typed_is_found_inside_what_the_agent_wrapped_around_it()
    {
        // Measured: the step holds the request between marks, with the agent's own context after it, and can append to
        // the request itself (a command name it took from the words).
        var steps = Steps(
            JsonSerializer.Serialize(new { step_index = 0, type = "USER_INPUT", created_at = At("09:00:00"), content = "<USER_REQUEST>\nplease tidy the notes folder and tell me what moved/teamwork\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nplease tidy other things too\n</ADDITIONAL_METADATA>" }),
            Planned(1, At("09:00:04"), "Done:\n\n- a.txt moved"));
        var stamped = AgyTimes.Stamp(new[] { User("u-1", "please tidy the notes folder and tell me what moved"), Text("a-1", "Done:\n- a.txt moved") }, steps);
        Assert.Equal(new[] { Stamp("09:00:00"), Stamp("09:00:04") }, stamped.Select(When));
    }

    [Fact]
    public void A_message_the_replay_ran_together_is_the_separate_messages_it_was_each_with_its_time()
    {
        // A timer's reply, 40 s after the message that set it: one run of text in the replay, two steps with the
        // timer's notice between them on disk.
        var lines = new[] { User("u-1", "in 40 seconds say DING"), Called("call_1", "Schedule"), Text("a-1", "Timer set for 40 seconds.\n\nDING"), User("u-2", "thanks") };
        var steps = Steps(
            Asked(0, At("10:00:00"), "in 40 seconds say DING"),
            Planned(1, At("10:00:00"), calls: new[] { ("schedule", "DurationSeconds", "40") }),
            Outcome(2, At("10:00:03"), "Tool is running as a background task"),
            Planned(3, At("10:00:03"), "Timer set for 40 seconds."),
            Notice(4, At("10:00:43"), "<SYSTEM_MESSAGE>the timer fired</SYSTEM_MESSAGE>"),
            Planned(5, At("10:00:43"), "DING"),
            Asked(6, At("10:05:00"), "thanks"));

        var stamped = AgyTimes.Stamp(lines, steps);
        Assert.Equal(new[] { ("u-1", "in 40 seconds say DING", Stamp("10:00:00")), ("call_1", null, null), ("a-1", "Timer set for 40 seconds.", Stamp("10:00:03")), ("a-1.1", "DING", Stamp("10:00:43")), ("u-2", "thanks", Stamp("10:05:00")) },
            stamped.Select(l => (Id(l), Id(l).StartsWith("call") ? null : Words(l), When(l))));
        Assert.True(J(stamped[3]).GetProperty("history").GetBoolean());
    }

    [Fact]
    public void Steps_that_followed_each_other_with_nothing_between_stay_one_message()
    {
        var steps = Steps(Asked(0, At("10:00:00"), "go"), Planned(1, At("10:00:01"), "First part."), Planned(2, At("10:00:02"), "Second part."), Asked(3, At("10:01:00"), "more"), Planned(4, At("10:01:02"), "Ok."));
        var stamped = AgyTimes.Stamp(new[] { User("u-1", "go"), Text("a-1", "First part.\n\nSecond part."), User("u-2", "more"), Text("a-2", "Ok.") }, steps);
        Assert.Equal(new[] { ("u-1", Stamp("10:00:00")), ("a-1", Stamp("10:00:01")), ("u-2", Stamp("10:01:00")), ("a-2", Stamp("10:01:02")) }, stamped.Select(l => (Id(l), When(l))));
        Assert.Equal("First part.\n\nSecond part.", Words(stamped[1]));
    }

    [Fact]
    public void A_message_that_cannot_be_cut_where_its_steps_meet_is_left_whole_with_the_time_it_began()
    {
        // The words agree once white space is evened out, but the second step's are not in the text as written.
        var steps = Steps(Asked(0, At("10:00:00"), "go"), Planned(1, At("10:00:01"), "Set."), Notice(2, At("10:00:40"), "fired"), Planned(3, At("10:00:41"), "Now  it  rings."));
        var stamped = AgyTimes.Stamp(new[] { User("u-1", "go"), Text("a-1", "Set.\n\nNow it rings.") }, steps);
        Assert.Equal(new[] { ("u-1", Stamp("10:00:00")), ("a-1", Stamp("10:00:01")) }, stamped.Select(l => (Id(l), When(l))));
        Assert.Equal("Set.\n\nNow it rings.", Words(stamped[1]));
    }

    [Fact]
    public void Words_that_are_not_where_they_should_be_get_no_time_and_the_next_thing_asked_puts_it_right()
    {
        var lines = new[] { User("u-1", "something the transcript does not have"), Text("a-1", "An answer nobody wrote down."), User("u-2", "second question"), Text("a-2", "Second answer.") };
        var steps = Steps(Asked(0, At("10:00:00"), "first question"), Planned(1, At("10:00:02"), "First answer."), Asked(2, At("10:10:00"), "second question"), Planned(3, At("10:10:03"), "Second answer."));
        var stamped = AgyTimes.Stamp(lines, steps);
        Assert.Equal(new[] { null, null, Stamp("10:10:00"), Stamp("10:10:03") }, stamped.Select(When));
        Assert.Same(lines[0], stamped[0]);
        Assert.Same(lines[1], stamped[1]);
    }

    [Fact]
    public void An_answer_is_never_looked_for_past_the_next_thing_asked()
    {
        // The second question was not found, so the answers after it belong to a turn that cannot be placed: the
        // same words in a later turn are not theirs.
        var lines = new[] { User("u-1", "first"), Text("a-1", "Yes, that is right."), User("u-2", "lost"), Text("a-2", "Yes, that is right.") };
        var steps = Steps(Asked(0, At("10:00:00"), "first"), Planned(1, At("10:00:02"), "Yes, that is right."), Asked(2, At("10:10:00"), "something else entirely"), Planned(3, At("10:10:03"), "Yes, that is right."));
        Assert.Equal(new[] { Stamp("10:00:00"), Stamp("10:00:02"), null, null }, AgyTimes.Stamp(lines, steps).Select(When));
    }

    [Fact]
    public void Short_words_are_matched_whole_or_from_the_start_and_never_found_inside_others()
    {
        // "ok" is inside "book it" and inside the agent's context around every request.
        var steps = Steps(Asked(0, At("10:00:00"), "book it"), Planned(1, At("10:00:02"), "Booked."), Asked(2, At("10:20:00"), "ok"), Planned(3, At("10:20:01"), "Good."));
        Assert.Equal(new[] { Stamp("10:20:00"), Stamp("10:20:01") }, AgyTimes.Stamp(new[] { User("u-9", "ok"), Text("a-9", "Good.") }, steps).Select(When));
        // And the same words said twice get their two times in turn.
        var twice = Steps(Asked(0, At("11:00:00"), "yes"), Planned(1, At("11:00:01"), "One."), Asked(2, At("11:30:00"), "yes"), Planned(3, At("11:30:01"), "Two."));
        Assert.Equal(new[] { Stamp("11:00:00"), Stamp("11:00:01"), Stamp("11:30:00"), Stamp("11:30:01") }, AgyTimes.Stamp(new[] { User("u-1", "yes"), Text("a-1", "One."), User("u-2", "yes"), Text("a-2", "Two.") }, twice).Select(When));
    }

    [Fact]
    public void A_message_the_replay_left_out_is_passed_over_only_for_words_long_enough_to_tell_apart()
    {
        var steps = Steps(Asked(0, At("10:00:00"), "go"), Planned(1, At("10:00:01"), "A remark the replay does not have."), Planned(2, At("10:00:05"), "The long answer that the replay does have."), Planned(3, At("10:00:09"), "Fine."));
        Assert.Equal(new[] { Stamp("10:00:00"), Stamp("10:00:05") }, AgyTimes.Stamp(new[] { User("u-1", "go"), Text("a-1", "The long answer that the replay does have.") }, steps).Select(When));
        Assert.Equal(new[] { Stamp("10:00:00"), null }, AgyTimes.Stamp(new[] { User("u-1", "go"), Text("a-1", "Fine.") }, steps).Select(When));
    }

    [Fact]
    public void A_time_is_never_earlier_than_the_one_before_it()
    {
        var steps = Steps(Asked(0, At("10:00:30"), "go"), Planned(1, At("10:00:10"), "Done."), Asked(2, At("10:00:50"), "again"));
        Assert.Equal(new[] { Stamp("10:00:30"), Stamp("10:00:30"), Stamp("10:00:50") }, AgyTimes.Stamp(new[] { User("u-1", "go"), Text("a-1", "Done."), User("u-2", "again") }, steps).Select(When));
    }

    [Fact]
    public void A_step_with_no_time_gives_none_and_a_line_that_has_one_keeps_it()
    {
        var steps = Steps(JsonSerializer.Serialize(new { step_index = 0, type = "USER_INPUT", content = "<USER_REQUEST>\ngo\n</USER_REQUEST>" }), Planned(1, At("10:00:10"), "Done."));
        byte[] already = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["type"] = "assistant", ["uuid"] = "a-1", ["message"] = new { role = "assistant", content = new[] { new { type = "text", text = "Done." } } }, ["timestamp"] = "2020-01-01T00:00:00.000Z" });
        var stamped = AgyTimes.Stamp(new[] { User("u-1", "go"), already }, steps);
        Assert.Equal(new[] { null, "2020-01-01T00:00:00.000Z" }, stamped.Select(When));
    }

    [Fact]
    public void With_nothing_on_disk_every_line_is_the_one_that_came()
    {
        var lines = new[] { User("u-1", "go"), Text("a-1", "Done."), Encoding.UTF8.GetBytes("not a line"), Encoding.UTF8.GetBytes("{\"type\":\"result\"}") };
        Assert.Equal(lines, AgyTimes.Stamp(lines, new List<AgyStep>()));
        var stamped = AgyTimes.Stamp(lines, Steps(Asked(0, At("10:00:00"), "go"), Planned(1, At("10:00:01"), "Done.")));
        Assert.Same(lines[2], stamped[2]);
        Assert.Same(lines[3], stamped[3]);
    }
}
