using System.Text.Json;
using Xunit;

namespace SessionDeck.Tests;

public class PageWatchTests
{
    readonly PageWatch _watch = new();

    string? Line(string json, long now = 10_000)
    {
        using var doc = JsonDocument.Parse(json);
        return _watch.Line(doc.RootElement, now);
    }

    [Fact]
    public void A_slow_switch_says_where_the_time_went() =>
        Assert.Equal("tab-switch total=2400ms queued=2100ms handler=12ms frame=180ms pane=9410668b6e6b kind=chat",
            Line("""{"type":"switched","at":7600,"queued":2100,"handler":12,"frame":180,"changed":true,"pane":"9410668b6e6b","kind":"chat","hidden":false}"""));

    [Fact]
    public void A_quick_switch_is_logged_too_so_a_stall_past_the_page_can_be_told_from_nothing_recorded() =>
        Assert.Equal("tab-switch total=14ms queued=1ms handler=2ms frame=9ms pane=abc kind=terminal",
            Line("""{"type":"switched","at":9986,"queued":1,"handler":2,"frame":9,"changed":true,"pane":"abc","kind":"terminal"}"""));

    [Theory]
    [InlineData(9_701, false)]
    [InlineData(9_700, true)]
    [InlineData(1, true)]
    public void A_layout_that_changed_nothing_on_show_is_logged_from_300_ms(long postedAt, bool logged)
    {
        string? line = Line($$"""{"type":"switched","at":{{postedAt}},"queued":0,"handler":0,"frame":0,"changed":false,"pane":"a","kind":"chat"}""");
        Assert.Equal(logged, line != null);
        if (logged) Assert.StartsWith("page-layout total=", line);
    }

    [Theory]
    [InlineData("""{"type":"switched","queued":5000,"handler":0,"frame":0,"changed":true}""")]
    [InlineData("""{"type":"switched","at":0,"queued":5000,"changed":true}""")]
    [InlineData("""{"type":"switched","at":"7600","queued":5000,"changed":true}""")]
    public void A_switch_that_does_not_say_when_it_was_asked_for_is_not_logged(string json) => Assert.Null(Line(json));

    [Fact]
    public void A_switch_on_a_page_nobody_sees_says_so_and_has_no_frame() =>
        Assert.Equal("tab-switch total=900ms queued=880ms handler=3ms frame=-1ms pane=- kind=- hidden",
            Line("""{"type":"switched","at":9100,"queued":880,"handler":3,"frame":-1,"changed":true,"pane":"","kind":"","hidden":true}"""));

    [Fact]
    public void A_stall_says_what_the_pages_handlers_took_of_it() =>
        Assert.Equal("page-stall 1800ms ran=chat-paint=1650ms/21@abc fit=90ms/20@def",
            Line("""{"type":"stall","ms":1800.4,"ran":"chat-paint=1650ms/21@abc fit=90ms/20@def","hidden":false}"""));

    [Fact]
    public void A_stall_with_no_handler_of_ours_in_it_says_none() =>
        Assert.Equal("page-stall 650ms hidden ran=-", Line("""{"type":"stall","ms":650,"ran":"","hidden":true}"""));

    [Theory]
    [InlineData("""{"type":"visibility","hidden":true}""", "page-visibility hidden")]
    [InlineData("""{"type":"visibility","hidden":false}""", "page-visibility visible")]
    public void The_page_being_hidden_and_shown_is_logged(string json, string line) => Assert.Equal(line, Line(json));

    [Fact]
    public void A_pane_that_keeps_asking_for_a_host_that_is_gone_is_logged_once_and_once_more_when_it_is_back()
    {
        Assert.Equal("pane-socket lost host=9410668b6e6b retry=500ms", Line("""{"type":"disconnected","id":"9410668b6e6b","retryMs":500}"""));
        Assert.Null(Line("""{"type":"disconnected","id":"9410668b6e6b","retryMs":1000}"""));
        Assert.Null(Line("""{"type":"disconnected","id":"9410668b6e6b","retryMs":30000}"""));
        Assert.Equal("pane-socket lost host=other retry=500ms", Line("""{"type":"disconnected","id":"other","retryMs":500}"""));
        Assert.Equal("pane-socket back host=9410668b6e6b", Line("""{"type":"connected","id":"9410668b6e6b"}"""));
        Assert.Null(Line("""{"type":"connected","id":"9410668b6e6b"}"""));
        Assert.Equal("pane-socket lost host=9410668b6e6b retry=500ms", Line("""{"type":"disconnected","id":"9410668b6e6b","retryMs":500}"""));
    }

    [Theory]
    [InlineData("""{"type":"connected","id":"never-lost"}""")]
    [InlineData("""{"type":"disconnected","retryMs":500}""")]
    [InlineData("""{"type":"metrics","ms":900}""")]
    [InlineData("""{"ms":900}""")]
    [InlineData("""[1,2]""")]
    [InlineData("\"stall\"")]
    public void Anything_else_from_the_page_is_no_line(string json) => Assert.Null(Line(json));

    [Fact]
    public void Panes_that_never_come_back_do_not_pile_up_without_end()
    {
        for (int i = 0; i < 2000; i++) Assert.NotNull(Line($$"""{"type":"disconnected","id":"h{{i}}","retryMs":500}"""));
        Assert.InRange(_watch.Lost, 1, 500);
    }

    [Fact]
    public void What_the_page_says_cannot_start_another_line_or_another_value()
    {
        string? line = Line("""{"type":"switched","at":1,"changed":true,"queued":"x","handler":null,"frame":1e400,"pane":"a b\r\nfake-line 1ms","kind":"chat\tx"}""");
        Assert.Equal("tab-switch total=9999ms queued=0ms handler=0ms frame=0ms pane=abfake-line1ms kind=chatx", line);
        string? stall = Line("""{"type":"stall","ms":900,"ran":"one\r\ntwo"}""");
        Assert.Equal("page-stall 900ms ran=one  two", stall);
        Assert.True(Line("""{"type":"stall","ms":900,"ran":"REPEAT"}""".Replace("REPEAT", new string('x', 5000)))!.Length < 340);
        Assert.Equal(40, Line("""{"type":"disconnected","id":"REPEAT"}""".Replace("REPEAT", new string('y', 500)))!.Split("host=")[1].Split(' ')[0].Length);
    }
}
