using System.Text.Json;
using Xunit;

namespace SessionDeck.Tests;

public class PageWatchTests
{
    static string? Line(string json, long now = 10_000)
    {
        using var doc = JsonDocument.Parse(json);
        return PageWatch.Line(doc.RootElement, now);
    }

    [Fact]
    public void A_slow_switch_says_where_the_time_went() =>
        Assert.Equal("tab-switch total=2400ms queued=2100ms handler=12ms frame=180ms pane=9410668b6e6b kind=chat",
            Line("""{"type":"switched","at":7600,"queued":2100,"handler":12,"frame":180,"pane":"9410668b6e6b","kind":"chat","hidden":false}"""));

    [Theory]
    [InlineData(9_701, false)]
    [InlineData(9_700, true)]
    [InlineData(1, true)]
    public void A_switch_is_logged_from_300_ms(long postedAt, bool logged) =>
        Assert.Equal(logged, Line($$"""{"type":"switched","at":{{postedAt}},"queued":0,"handler":0,"frame":0,"pane":"a","kind":"chat"}""") != null);

    [Theory]
    [InlineData("""{"type":"switched","queued":5000,"handler":0,"frame":0}""")]
    [InlineData("""{"type":"switched","at":0,"queued":5000}""")]
    [InlineData("""{"type":"switched","at":"7600","queued":5000}""")]
    public void A_switch_that_does_not_say_when_it_was_asked_for_is_not_logged(string json) => Assert.Null(Line(json));

    [Fact]
    public void A_switch_on_a_page_nobody_sees_says_so_and_has_no_frame() =>
        Assert.Equal("tab-switch total=900ms queued=880ms handler=3ms frame=-1ms pane=- kind=- hidden",
            Line("""{"type":"switched","at":9100,"queued":880,"handler":3,"frame":-1,"pane":"","kind":"","hidden":true}"""));

    [Fact]
    public void A_stall_names_the_handlers_that_ran_long_in_it() =>
        Assert.Equal("page-stall 1800ms ran=chat-paint:abc=1700ms msg-layout=110ms",
            Line("""{"type":"stall","ms":1800.4,"ran":"chat-paint:abc=1700ms msg-layout=110ms","hidden":false}"""));

    [Fact]
    public void A_stall_with_no_handler_of_ours_in_it_says_none() =>
        Assert.Equal("page-stall 650ms hidden ran=-", Line("""{"type":"stall","ms":650,"ran":"","hidden":true}"""));

    [Theory]
    [InlineData("""{"type":"visibility","hidden":true}""", "page-visibility hidden")]
    [InlineData("""{"type":"visibility","hidden":false}""", "page-visibility visible")]
    [InlineData("""{"type":"disconnected","id":"9410668b6e6b","retryMs":500}""", "pane-socket lost host=9410668b6e6b retry=500ms")]
    public void The_page_being_hidden_and_a_pane_losing_its_socket_are_logged(string json, string line) => Assert.Equal(line, Line(json));

    [Theory]
    [InlineData("""{"type":"metrics","ms":900}""")]
    [InlineData("""{"type":"connected","id":"abc"}""")]
    [InlineData("""{"ms":900}""")]
    [InlineData("""[1,2]""")]
    [InlineData("\"stall\"")]
    public void Anything_else_from_the_page_is_no_line(string json) => Assert.Null(Line(json));

    [Fact]
    public void What_the_page_says_cannot_start_another_line_or_another_value()
    {
        string? line = Line("""{"type":"switched","at":1,"queued":"x","handler":null,"frame":1e400,"pane":"a b\r\nfake-line 1ms","kind":"chat\tx"}""");
        Assert.Equal("tab-switch total=9999ms queued=0ms handler=0ms frame=0ms pane=abfake-line1ms kind=chatx", line);
        string? stall = Line("""{"type":"stall","ms":900,"ran":"one\r\ntwo"}""");
        Assert.Equal("page-stall 900ms ran=one  two", stall);
        Assert.True(Line("""{"type":"stall","ms":900,"ran":"REPEAT"}""".Replace("REPEAT", new string('x', 5000)))!.Length < 340);
        Assert.Equal(40, Line("""{"type":"disconnected","id":"REPEAT"}""".Replace("REPEAT", new string('y', 500)))!.Split("host=")[1].Split(' ')[0].Length);
    }
}
