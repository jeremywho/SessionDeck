using Xunit;

namespace SessionDeck.Tests;

public class CodexPlanUsageTests
{
    // Shape taken verbatim from a real rollout's token_count record.
    const string OneWindow = """
    {"limit_id":"codex","limit_name":null,
     "primary":{"used_percent":59.0,"window_minutes":10080,"resets_at":1786326217},
     "secondary":null,
     "credits":{"has_credits":false,"unlimited":false,"balance":"0"},
     "plan_type":"prolite","rate_limit_reached_type":null}
    """;

    const string TwoWindows = """
    {"limit_id":"codex",
     "primary":{"used_percent":12.4,"window_minutes":300,"resets_at":0},
     "secondary":{"used_percent":59.0,"window_minutes":10080,"resets_at":0},
     "plan_type":"pro"}
    """;

    [Fact]
    public void A_single_window_is_labelled_just_Codex()
    {
        var m = Assert.Single(CodexScanner.ParsePlanUsage(OneWindow));
        Assert.Equal("Codex", m.Label);
        Assert.Equal(59, m.Percent);
        Assert.Equal("Codex Pro Lite plan", m.Note);
        Assert.NotEqual(default, m.ResetsAt);
    }

    // With two windows "Codex" alone would be ambiguous, so each gets its span appended.
    [Fact]
    public void Two_windows_are_distinguished_by_their_span()
    {
        var meters = CodexScanner.ParsePlanUsage(TwoWindows);
        Assert.Equal(2, meters.Count);
        Assert.Equal("Codex 5h", meters[0].Label);
        Assert.Equal("Codex wk", meters[1].Label);
        Assert.Equal(12, meters[0].Percent);          // 12.4 -> 12
    }

    // Codex sends no severity of its own (the Claude limits do), so this is the one meter whose
    // colour the app decides. Thresholds match Context %, so a number doesn't mean two things.
    [Theory]
    [InlineData(10.0, "normal")]
    [InlineData(70.0, "normal")]
    [InlineData(70.6, "warning")]
    [InlineData(85.0, "warning")]
    [InlineData(92.0, "critical")]
    public void Severity_is_derived_because_codex_reports_none(double used, string expected)
    {
        string json = "{\"primary\":{\"used_percent\":" + used.ToString(System.Globalization.CultureInfo.InvariantCulture)
                      + ",\"window_minutes\":10080}}";
        Assert.Equal(expected, Assert.Single(CodexScanner.ParsePlanUsage(json)).Severity);
    }

    [Fact]
    public void An_unknown_plan_id_is_shown_as_is_rather_than_dropped()
    {
        string json = """{"primary":{"used_percent":5,"window_minutes":10080},"plan_type":"someNewTier"}""";
        Assert.Equal("Codex someNewTier plan", Assert.Single(CodexScanner.ParsePlanUsage(json)).Note);
    }

    [Fact]
    public void Missing_or_unparseable_limits_yield_nothing_rather_than_a_zero_meter()
    {
        Assert.Empty(CodexScanner.ParsePlanUsage("""{"primary":null,"secondary":null}"""));
        Assert.Empty(CodexScanner.ParsePlanUsage("{}"));
        Assert.Empty(CodexScanner.ParsePlanUsage("not json"));
        // present but with no percent -- a meter reading 0% would be a lie, not a default
        Assert.Empty(CodexScanner.ParsePlanUsage("""{"primary":{"window_minutes":10080}}"""));
    }

    [Fact]
    public void The_tooltip_carries_the_plan_and_the_readings_age()
    {
        var m = new UsageMeter { Label = "Codex", Percent = 59, Note = "Codex Pro Lite plan", ReadAt = DateTime.Now.AddMinutes(-8) };
        Assert.Contains("59% used", m.Tooltip);
        Assert.Contains("Codex Pro Lite plan", m.Tooltip);
        Assert.Contains("8m ago", m.Tooltip);
    }

    // Claude meters have no ReadAt/Note, and must not grow an empty trailing line because of them.
    [Fact]
    public void A_claude_meter_tooltip_is_unchanged_by_the_new_fields()
    {
        var m = new UsageMeter { Label = "Week", Percent = 45, Severity = "normal" };
        Assert.Equal("Week — 45% used", m.Tooltip);
    }
}
