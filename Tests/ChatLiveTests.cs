using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>
/// What a viewer that attaches late is told so that it shows what the others do, and what is kept
/// when a turn ends with something half said. The lines are in the shape every chat host sends a pane.
/// </summary>
public class ChatLiveTests
{
    static readonly DateTime Now = new(2026, 3, 4, 10, 0, 5, DateTimeKind.Utc);

    static byte[] B(string json) => Encoding.UTF8.GetBytes(json);
    static JsonElement J(byte[] line) => JsonDocument.Parse(line).RootElement.Clone();
    static byte[] Start(string kind) => B("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"" + kind + "\"}}}");
    static byte[] Delta(string text, bool thinking = false) => B(JsonSerializer.Serialize(new { type = "stream_event", @event = new { type = "content_block_delta", delta = thinking ? (object)new { type = "thinking_delta", thinking = text } : new { type = "text_delta", text } } }));
    static readonly byte[] MessageStart = B("""{"type":"stream_event","event":{"type":"message_start"}}""");
    static readonly byte[] Init = B("""{"type":"system","subtype":"init","session_id":"s1","model":"m"}""");
    static readonly byte[] Result = B("""{"type":"result","subtype":"success","is_error":false}""");
    static byte[] Said(string kind, string text) => B(JsonSerializer.Serialize(new { type = "assistant", message = new { role = "assistant", content = new[] { kind == "text" ? (object)new { type = "text", text } : new { type = "thinking", thinking = text } } }, parent_tool_use_id = (string?)null }));

    /// <summary>What the replayed lines come to, read as the pane reads them: each message begun, with all it was sent.</summary>
    static List<(string Kind, string Text)> Shown(List<byte[]> lines)
    {
        var shown = new List<(string, string)>();
        bool growing = false;
        foreach (var line in lines.Select(J).Where(l => l.GetProperty("type").GetString() == "stream_event"))
        {
            var ev = line.GetProperty("event");
            Assert.Equal(JsonValueKind.Null, line.GetProperty("parent_tool_use_id").ValueKind);
            if (ev.GetProperty("type").GetString() == "content_block_start")
            {
                string kind = ev.GetProperty("content_block").GetProperty("type").GetString()!;
                growing = kind is "text" or "thinking";
                if (growing) shown.Add((kind, ""));
            }
            else if (growing)
            {
                var delta = ev.GetProperty("delta");
                string more = delta.GetProperty("type").GetString() == "text_delta" ? delta.GetProperty("text").GetString()! : delta.GetProperty("thinking").GetString()!;
                shown[^1] = (shown[^1].Item1, shown[^1].Item2 + more);
            }
        }
        return shown;
    }

    [Fact]
    public void A_viewer_that_attaches_is_told_what_is_being_said_whole_and_nothing_that_was_already_kept()
    {
        var live = new ChatLive(saysWork: false);
        Assert.Empty(live.Now());
        live.Said(MessageStart);
        live.Said(Start("thinking"));
        live.Said(Delta("Let me ", thinking: true));
        live.Said(Delta("see.", thinking: true));
        Assert.Equal(new[] { ("thinking", "Let me see.") }, Shown(live.Now()));
        // Asked twice, told the same twice: telling does not use anything up.
        Assert.Equal(new[] { ("thinking", "Let me see.") }, Shown(live.Now()));

        // The next message begins before the first is kept, as a CLI does it.
        live.Said(Start("text"));
        live.Said(Delta("The ans"));
        Assert.Equal(new[] { ("thinking", "Let me see."), ("text", "The ans") }, Shown(live.Now()));
        Assert.Equal(new[] { Said("thinking", "Let me see.") }, live.Keep(Said("thinking", "Let me see."), Now));
        live.Said(Delta("wer."));
        Assert.Equal(new[] { ("text", "The answer.") }, Shown(live.Now()));
        live.Keep(Said("text", "The answer."), Now);
        Assert.Empty(live.Now());
    }

    [Fact]
    public void A_turn_that_ends_with_something_half_said_keeps_it_ahead_of_its_end()
    {
        var live = new ChatLive(saysWork: false);
        live.Said(Start("thinking"));
        live.Said(Delta("Hm.", thinking: true));
        live.Said(Start("text"));
        live.Said(Delta("Half of an "));
        live.Said(Start("text"));
        var kept = live.Keep(Result, Now);
        Assert.Equal(3, kept.Count);
        Assert.Same(Result, kept[2]);
        var cut = kept.Take(2).Select(J).ToList();
        Assert.All(cut, l => Assert.Equal(("assistant", true, "2026-03-04T10:00:05.000Z", JsonValueKind.Null), (l.GetProperty("type").GetString(), l.GetProperty("partial").GetBoolean(), l.GetProperty("timestamp").GetString(), l.GetProperty("parent_tool_use_id").ValueKind)));
        Assert.Equal(("thinking", "Hm."), (cut[0].GetProperty("message").GetProperty("content")[0].GetProperty("type").GetString(), cut[0].GetProperty("message").GetProperty("content")[0].GetProperty("thinking").GetString()));
        Assert.Equal(("text", "Half of an "), (cut[1].GetProperty("message").GetProperty("content")[0].GetProperty("type").GetString(), cut[1].GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()));
        Assert.NotEqual(cut[0].GetProperty("uuid").GetString(), cut[1].GetProperty("uuid").GetString());
        // Kept, they are no longer being said; and a turn that ends with nothing open keeps only its end.
        Assert.Empty(live.Now());
        Assert.Equal(new[] { Result }, live.Keep(Result, Now));
    }

    [Theory]
    [InlineData("""{"type":"user","message":{"role":"user","content":"[Request interrupted by user]"},"parent_tool_use_id":null}""", true)]
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}""", true)]
    [InlineData("""{"type":"user","message":{"role":"user","content":"carry on"}}""", false)]
    [InlineData("""{"type":"user","message":{"role":"user","content":"[Request interrupted by user]"},"parent_tool_use_id":"toolu_1"}""", false)]
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""", false)]
    [InlineData("""{"type":"system","subtype":"compact_boundary"}""", false)]
    [InlineData("not a line", false)]
    public void Only_the_end_of_a_turn_keeps_what_was_half_said(string line, bool ends)
    {
        var live = new ChatLive(saysWork: false);
        live.Said(Start("text"));
        live.Said(Delta("So far"));
        var kept = live.Keep(B(line), Now);
        Assert.Equal(ends ? 2 : 1, kept.Count);
        Assert.Equal(line, Encoding.UTF8.GetString(kept[^1]));
        Assert.Equal(ends ? 0 : 1, Shown(live.Now()).Count);
    }

    [Fact]
    public void What_was_being_said_when_the_session_ends_is_kept_and_an_empty_message_is_not()
    {
        var live = new ChatLive(saysWork: false);
        live.Said(Start("text"));
        live.Said(Delta("   "));
        live.Said(Start("text"));
        live.Said(Delta("Last words"));
        var cut = live.Ending(Now);
        Assert.Equal("Last words", J(cut.Single()).GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Empty(live.Ending(Now));
        Assert.Empty(live.Now());
    }

    [Fact]
    public void A_kept_line_ends_the_message_of_its_kind_that_began_first_and_no_other()
    {
        var live = new ChatLive(saysWork: false);
        live.Said(Start("text"));
        live.Said(Delta("first"));
        live.Said(Start("thinking"));
        live.Said(Delta("a thought", thinking: true));
        live.Said(Start("text"));
        live.Said(Delta("second"));
        live.Keep(Said("text", "first"), Now);
        Assert.Equal(new[] { ("thinking", "a thought"), ("text", "second") }, Shown(live.Now()));
        // A line with nothing of a kind being said, a tool call, and a subagent's line change nothing.
        live.Keep(B("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{}}]},"parent_tool_use_id":null}"""), Now);
        live.Keep(B("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"from a subagent"}]},"parent_tool_use_id":"toolu_9"}"""), Now);
        Assert.Equal(new[] { ("thinking", "a thought"), ("text", "second") }, Shown(live.Now()));
    }

    [Fact]
    public void A_new_message_from_the_model_ends_what_was_being_said()
    {
        // Not seen to happen (every block of a message is kept before the next message starts, in 41 recorded
        // message starts); if it did, what was being said is not told to a new viewer and is not kept.
        var live = new ChatLive(saysWork: false);
        live.Said(Start("text"));
        live.Said(Delta("never kept"));
        live.Said(MessageStart);
        Assert.Empty(live.Now());
        // Nothing is being said, so what arrives next belongs to nothing.
        live.Said(Delta("stray"));
        Assert.Empty(live.Now());
    }

    [Fact]
    public void A_call_being_written_after_a_message_does_not_get_that_messages_place()
    {
        // The pane adds each delta to the message started last. After a block it does not show, that is none: a
        // viewer told only of the message would add what follows to it.
        var live = new ChatLive(saysWork: false);
        live.Said(Start("text"));
        live.Said(Delta("Reading the file."));
        live.Said(Start("tool_use"));
        live.Said(B("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{\"file"}}}"""));
        var told = live.Now();
        Assert.Equal(new[] { ("text", "Reading the file.") }, Shown(told));
        Assert.Equal("other", J(told[^1]).GetProperty("event").GetProperty("content_block").GetProperty("type").GetString());
        live.Said(Delta("never part of the message"));
        Assert.Equal(new[] { ("text", "Reading the file.") }, Shown(live.Now()));
        // With the message still the one growing, nothing takes its place.
        var growing = new ChatLive(saysWork: false);
        growing.Said(Start("text"));
        growing.Said(Delta("Still going"));
        Assert.Equal(2, growing.Now().Count);
    }

    [Fact]
    public void What_a_subagent_is_saying_is_not_a_message_of_the_conversation()
    {
        var live = new ChatLive(saysWork: false);
        live.Said(B("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"text"}},"parent_tool_use_id":"toolu_9"}"""));
        live.Said(B("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"inside"}},"parent_tool_use_id":"toolu_9"}"""));
        Assert.Empty(live.Now());
    }

    [Fact]
    public void An_agent_said_to_be_at_work_is_at_work_for_a_new_viewer_until_the_turn_ends()
    {
        var agent = new ChatLive(saysWork: true);
        agent.Said(Init);
        Assert.Same(Init, agent.Now().Single());
        agent.Said(Start("text"));
        Assert.Equal(2, agent.Now().Count);
        agent.Keep(Result, Now);
        Assert.Empty(agent.Now());
        // A CLI says the same line at the start of every turn and its turn is told by the lines kept: nothing to add.
        var cli = new ChatLive(saysWork: false);
        cli.Said(Init);
        Assert.Empty(cli.Now());
    }

    [Fact]
    public void Another_conversation_starts_with_nothing_being_said()
    {
        var live = new ChatLive(saysWork: true);
        live.Said(Init);
        live.Said(Start("text"));
        live.Said(Delta("about the last one"));
        live.Reset();
        Assert.Empty(live.Now());
        Assert.Equal(new[] { Result }, live.Keep(Result, Now));
    }
}
