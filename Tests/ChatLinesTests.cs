using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class ChatLinesTests
{
    static readonly DateTimeOffset At = new(2026, 11, 17, 20, 5, 9, 123, TimeSpan.Zero);

    static byte[] B(string json) => Encoding.UTF8.GetBytes(json);
    static string S(byte[] line) => Encoding.UTF8.GetString(line);

    [Theory]
    [InlineData("""{"type":"user","uuid":"u1","message":{"role":"user","content":"hi"},"parent_tool_use_id":null}""")]
    [InlineData("""{"type":"assistant","uuid":"a1","message":{"role":"assistant","content":[{"type":"text","text":"h\u00e9llo \"q\""}]},"parent_tool_use_id":null}""")]
    public void A_message_with_no_time_gets_one_and_keeps_everything_else(string json)
    {
        byte[] stamped = ChatLines.Stamped(B(json), At);
        using var before = JsonDocument.Parse(json);
        using var after = JsonDocument.Parse(stamped);
        Assert.Equal("2026-11-17T20:05:09.123Z", after.RootElement.GetProperty("timestamp").GetString());
        Assert.Equal(At, DateTimeOffset.Parse(after.RootElement.GetProperty("timestamp").GetString()!));
        foreach (var p in before.RootElement.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(p.Value, after.RootElement.GetProperty(p.Name)), p.Name);
        Assert.Equal(before.RootElement.EnumerateObject().Count() + 1, after.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("""{"type":"user","timestamp":"2026-01-02T03:04:05.000Z","message":{"role":"user","content":"hi"}}""")]
    [InlineData("""{"type":"result","subtype":"success"}""")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"control_request","request_id":"r1"}""")]
    [InlineData("""{"message":"no type"}""")]
    [InlineData("""{"type":7}""")]
    [InlineData("""["user"]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void A_line_that_has_a_time_or_is_not_a_message_is_left_exactly_as_it_was(string line)
    {
        byte[] given = B(line);
        Assert.Same(given, ChatLines.Stamped(given, At));
        Assert.Same(given, ChatLines.StampedNow(given, At.UtcDateTime));
    }

    [Fact]
    public void A_line_read_back_from_an_earlier_conversation_is_not_given_the_time_it_is_now()
    {
        byte[] replayed = B("""{"type":"user","uuid":"u1","message":{"role":"user","content":"long ago"},"history":true}""");
        Assert.Same(replayed, ChatLines.StampedNow(replayed, At.UtcDateTime));
        // A time the agent itself recorded for it is another matter.
        Assert.Contains("\"timestamp\":\"2026-11-17T20:05:09.123Z\"", S(ChatLines.Stamped(replayed, At)));
        // "history": false is a line said now.
        Assert.Contains("\"timestamp\"", S(ChatLines.StampedNow(B("""{"type":"assistant","history":false,"message":{}}"""), At.UtcDateTime)));
    }

    [Fact]
    public void Now_is_written_in_utc_whatever_kind_of_clock_it_came_from()
    {
        byte[] line = B("""{"type":"assistant","message":{"role":"assistant","content":[]}}""");
        var utc = new DateTime(2026, 11, 17, 20, 5, 9, 123, DateTimeKind.Utc);
        Assert.Contains("\"timestamp\":\"2026-11-17T20:05:09.123Z\"", S(ChatLines.StampedNow(line, utc)));
        Assert.Contains("\"timestamp\":\"2026-11-17T20:05:09.123Z\"", S(ChatLines.StampedNow(line, utc.ToLocalTime())));
    }
}
