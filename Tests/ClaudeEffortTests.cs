using Xunit;

namespace ClaudeSessionMonitor.Tests;

/// <summary>
/// Claude Code stamps the effort that actually ran on each assistant transcript record, at the record
/// root rather than inside <c>message</c>. These pin that location, because a schema move would silently
/// blank the field rather than fail anything.
/// </summary>
public sealed class ClaudeEffortTests
{
    static SessionInfo Parse(string line)
    {
        var s = new SessionInfo();
        SessionScanner.ParseAssistant(line, s);
        return s;
    }

    [Fact]
    public void Reads_effort_from_the_record_root()
    {
        var s = Parse("""
            {"type":"assistant","effort":"max","message":{"model":"claude-opus-5","usage":{"input_tokens":10}}}
            """);

        Assert.Equal("max", s.Effort);
        Assert.Equal("claude-opus-5", s.Model);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("max")]
    [InlineData("xhigh")]
    public void Accepts_every_observed_level(string level)
    {
        Assert.Equal(level, Parse(
            "{\"type\":\"assistant\",\"effort\":\"" + level + "\",\"message\":{\"model\":\"claude-fable-5\"}}").Effort);
    }

    [Fact]
    public void Absent_effort_is_empty_not_a_failure()
    {
        // ~0.1% of records carry no effort, and older transcripts predate the field entirely. The rest of
        // the record must still parse — an empty string is what HasEffort already treats as "don't show".
        var s = Parse("""{"type":"assistant","message":{"model":"claude-opus-5","usage":{"output_tokens":7}}}""");

        Assert.Equal("", s.Effort);
        Assert.Equal("claude-opus-5", s.Model);
        Assert.Equal(7, s.OutputTokens);
    }

    [Fact]
    public void Ignores_effort_nested_under_message()
    {
        // Guards the one plausible wrong reading: `message.effort` is not where it lives, and accepting it
        // there would make a future schema move look like it still worked.
        Assert.Equal("", Parse("""
            {"type":"assistant","message":{"model":"claude-opus-5","effort":"high"}}
            """).Effort);
    }

    [Fact]
    public void A_synthetic_record_contributes_nothing()
    {
        // Synthetic records are API-error placeholders. They carry no real turn, so neither the model nor
        // the effort on one may be adopted — the scan keeps walking back to the last real turn.
        var s = Parse("""{"type":"assistant","effort":"high","message":{"model":"<synthetic>"}}""");

        Assert.Equal("", s.Effort);
        Assert.Equal("", s.Model);
    }

    [Fact]
    public void A_non_string_effort_reads_as_absent()
    {
        Assert.Equal("", Parse("""
            {"type":"assistant","effort":3,"message":{"model":"claude-opus-5"}}
            """).Effort);
    }

    // The parser returning "" is only half the delivered behaviour: Enrich then fills the gap from the
    // previous pass, so what the row actually shows after a record with no effort is the LAST KNOWN
    // effort, not blank. Pinning it here because the parser tests above look like they cover it and
    // don't — a reading of ParseAssistant alone predicts a blank cell that never appears.
    [Fact]
    public void A_gap_keeps_the_last_known_effort_rather_than_blanking_the_row()
    {
        var s = new SessionInfo { SessionId = "x", Model = "claude-opus-5" };
        SessionScanner.FillGapsFrom(s, new SessionScanner.Detail { Effort = "max", Model = "claude-opus-5" });

        Assert.Equal("max", s.Effort);
    }

    [Fact]
    public void A_real_effort_is_never_overwritten_by_the_cached_one()
    {
        var s = new SessionInfo { SessionId = "x", Effort = "high" };
        SessionScanner.FillGapsFrom(s, new SessionScanner.Detail { Effort = "max" });

        Assert.Equal("high", s.Effort);
    }
}
