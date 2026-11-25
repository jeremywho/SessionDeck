using System.Text.Json;
using Xunit;

namespace SessionDeck.Tests;

public class AxWatchTests
{
    readonly AxWatch _watch = new();

    const string None = """{"histograms":[]}""";

    static string Histogram(string name, params (int Low, int Count)[] buckets) =>
        JsonSerializer.Serialize(new { histograms = new[] { new { name, sum = 0, count = buckets.Sum(b => b.Count), buckets = buckets.Select(b => new { low = b.Low, high = b.Low + 1, count = b.Count }) } } });

    static string Modes(params int[] on) => Histogram(AxWatch.ModesHistogram, on.Select(m => (m, 1)).ToArray());

    static string Apis(params (int Low, int Count)[] calls) => Histogram(AxWatch.ApisHistogram, calls);

    static bool Prompts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return AxWatch.Prompts(doc.RootElement);
    }

    [Fact]
    public void The_first_answer_is_said_even_when_nothing_is_on_so_a_quiet_log_is_not_mistaken_for_no_reading() =>
        Assert.Equal("page-ax modes=- apis=- calls=0", _watch.PageLine(None, None, 1_000));

    [Fact]
    public void Modes_the_browser_switched_on_and_the_calls_made_most_are_said()
    {
        Assert.Equal("page-ax modes=0,1,3 apis=161:249156 97:212609 109:5 calls=461770",
            _watch.PageLine(Modes(3, 0, 1), Apis((109, 5), (97, 212609), (161, 249156)), 1_000));
    }

    [Fact]
    public void Only_the_eight_kinds_of_call_made_most_are_named_and_all_are_counted()
    {
        string? line = _watch.PageLine(Modes(0), Apis(Enumerable.Range(1, 12).Select(i => (i, i * 10)).ToArray()), 1_000);
        Assert.Equal("page-ax modes=0 apis=12:120 11:110 10:100 9:90 8:80 7:70 6:60 5:50 calls=780", line);
    }

    [Fact]
    public void A_bucket_with_nothing_in_it_is_not_a_mode()
    {
        string modes = Histogram(AxWatch.ModesHistogram, (0, 2), (1, 0), (3, 1));
        Assert.Equal("page-ax modes=0,3 apis=- calls=0", _watch.PageLine(modes, None, 1_000));
    }

    [Fact]
    public void A_histogram_of_another_name_in_the_answer_is_not_read()
    {
        string other = Histogram("Accessibility.ModeFlag.Other", (0, 4));
        Assert.Equal("page-ax modes=- apis=- calls=0", _watch.PageLine(other, Histogram("Accessibility.Performance.WinAPIs.X", (5, 9)), 1_000));
    }

    [Fact]
    public void The_same_modes_again_say_nothing()
    {
        Assert.NotNull(_watch.PageLine(Modes(0, 1), Apis((17, 4)), 1_000));
        Assert.Null(_watch.PageLine(Modes(1, 0), Apis((17, 4)), 500_000));
    }

    [Fact]
    public void A_change_of_modes_is_said_at_once_in_either_direction()
    {
        Assert.Equal("page-ax modes=- apis=- calls=0", _watch.PageLine(None, None, 1_000));
        Assert.Equal("page-ax modes=0,1 apis=17:4 calls=4", _watch.PageLine(Modes(0, 1), Apis((17, 4)), 1_001));
        Assert.Equal("page-ax modes=0,1,3 apis=17:4 calls=4", _watch.PageLine(Modes(0, 1, 3), Apis((17, 4)), 1_002));
    }

    [Theory]
    [InlineData(59_999, false)]
    [InlineData(60_000, true)]
    public void Calls_still_being_made_are_said_once_a_minute(long later, bool said)
    {
        Assert.NotNull(_watch.PageLine(Modes(0, 1), Apis((17, 4)), 0));
        string? line = _watch.PageLine(Modes(0, 1), Apis((17, 9)), later);
        Assert.Equal(said ? "page-ax modes=0,1 apis=17:9 calls=9" : null, line);
    }

    [Fact]
    public void A_minute_gone_with_no_more_calls_says_nothing()
    {
        Assert.NotNull(_watch.PageLine(Modes(0, 1), Apis((17, 4)), 0));
        Assert.Null(_watch.PageLine(Modes(0, 1), Apis((17, 4)), 600_000));
    }

    [Theory]
    [InlineData("not json", None)]
    [InlineData(None, """{"result":{}}""")]
    [InlineData("""{"histograms":7}""", None)]
    public void An_answer_that_is_not_one_is_said_once_as_unavailable(string modes, string apis)
    {
        Assert.Equal("page-ax unavailable=no histograms in the answer", _watch.PageLine(modes, apis, 1_000));
        Assert.Null(_watch.PageLine(modes, apis, 900_000));
        Assert.Null(_watch.Unavailable("again"));
    }

    [Fact]
    public void A_refusal_is_said_in_one_line_whatever_is_in_it() =>
        Assert.Equal("page-ax unavailable='Browser.getHistograms' wasn't found  (0x80004005)", _watch.Unavailable("'Browser.getHistograms' wasn't found\r\n(0x80004005)"));

    [Fact]
    public void The_browser_is_asked_at_most_every_five_seconds()
    {
        Assert.True(_watch.ShouldAsk(0));
        Assert.False(_watch.ShouldAsk(4_999));
        Assert.True(_watch.ShouldAsk(5_000));
        Assert.False(_watch.ShouldAsk(5_001));
    }

    [Fact]
    public void The_question_names_the_histogram_wanted() =>
        Assert.Equal("""{"query":"Accessibility.ModeFlag"}""", AxWatch.Question(AxWatch.ModesHistogram));

    [Theory]
    [InlineData("""{"type":"switch-parts","frame":290}""", true)]
    [InlineData("""{"type":"stall","ms":792,"ran":""}""", true)]
    [InlineData("""{"type":"stall","ms":792}""", true)]
    [InlineData("""{"type":"stall","ms":1935,"ran":"chat-paint=1739ms/24@a66d94579b6b"}""", false)]
    [InlineData("""{"type":"switched","frame":300}""", false)]
    [InlineData("""{"type":"frame","ms":300}""", false)]
    [InlineData("""{"type":"key","ms":80}""", false)]
    [InlineData("""[1]""", false)]
    public void Only_a_slow_switch_or_a_stall_with_nothing_of_ours_in_it_prompts_the_question(string json, bool prompts) =>
        Assert.Equal(prompts, Prompts(json));

    [Fact]
    public void The_first_ten_requests_once_the_window_is_up_are_said_then_one_a_minute_and_all_are_counted()
    {
        // As the window is made Windows asks ten times in a fifth of a second: the first is said, the rest counted.
        Assert.True(_watch.Request(1_000, out int first));
        Assert.Equal(1, first);
        for (int i = 2; i <= 10; i++) Assert.False(_watch.Request(1_000 + i * 20, out _));
        Assert.False(_watch.Request(1_000 + AxWatch.StartupMs - 1, out int eleventh));
        Assert.Equal(11, eleventh);

        long up = 1_000 + AxWatch.StartupMs;
        for (int i = 0; i < AxWatch.RequestsAtFirst; i++)
        {
            Assert.True(_watch.Request(up + i, out int n));
            Assert.Equal(12 + i, n);
        }
        long last = up + AxWatch.RequestsAtFirst - 1;
        Assert.False(_watch.Request(last + 1, out _));
        Assert.False(_watch.Request(last + AxWatch.RequestEveryMs - 1, out _));
        Assert.True(_watch.Request(last + AxWatch.RequestEveryMs, out int later));
        Assert.Equal(24, later);
        Assert.False(_watch.Request(last + AxWatch.RequestEveryMs + 1, out _));
    }

    [Theory]
    [InlineData(-25L, "UiaRootObjectId")]
    [InlineData(-4L, "OBJID_CLIENT")]
    [InlineData(0xFFFFFFFCL, "OBJID_CLIENT")]
    [InlineData(0xFFFFFFE7L, "UiaRootObjectId")]
    [InlineData(0L, "OBJID_WINDOW")]
    [InlineData(-16L, "-16")]
    [InlineData(1L, "1")]
    public void The_object_asked_for_is_named_whichever_way_its_number_was_widened(long objectId, string name) =>
        Assert.Equal(name, AxWatch.ObjectName(objectId));

    [Fact]
    public void A_request_says_what_was_asked_for_who_had_the_foreground_and_how_many_there_have_been()
    {
        Assert.Equal("ax-request objid=UiaRootObjectId foreground=1Password n=3", AxWatch.RequestLine(-25, "1Password", 3));
        Assert.Equal("ax-request objid=OBJID_CLIENT foreground=- n=1", AxWatch.RequestLine(-4, "", 1));
        Assert.Equal("ax-request objid=OBJID_WINDOW foreground=SomeName n=12", AxWatch.RequestLine(0, "Some Name\r\n", 12));
    }
}
