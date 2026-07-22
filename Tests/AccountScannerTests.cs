using Xunit;

namespace ClaudeSessionMonitor.Tests;

public class AccountScannerTests
{
    // Trimmed from a real ~/.claude.json. The shape that matters: the per-model limit exists ONLY
    // as a weekly_scoped entry in `limits`, while the sibling seven_day_opus/seven_day_sonnet
    // fields sit there null — which is exactly why the parser reads `limits` and not those.
    const string Config = """
    {
      "numStartups": 76,
      "oauthAccount": {
        "emailAddress": "someone@example.com",
        "organizationName": "someone@example.com's Organization",
        "organizationType": "claude_max"
      },
      "cachedUsageUtilization": {
        "fetchedAtMs": 1784727417458,
        "utilization": {
          "five_hour": { "utilization": 3 },
          "seven_day": { "utilization": 75 },
          "seven_day_opus": null,
          "seven_day_sonnet": null,
          "limits": [
            { "kind": "session", "percent": 3, "severity": "normal",
              "resets_at": "2026-07-22T18:09:59.994223+00:00", "scope": null },
            { "kind": "weekly_all", "percent": 75, "severity": "warning",
              "resets_at": "2026-07-25T22:59:59.994243+00:00", "scope": null },
            { "kind": "weekly_scoped", "percent": 100, "severity": "critical",
              "resets_at": "2026-07-25T22:59:59.994450+00:00",
              "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
          ]
        }
      }
    }
    """;

    [Fact]
    public void Reads_the_account_and_every_limit()
    {
        var info = AccountScanner.Parse(Config);

        Assert.NotNull(info);
        Assert.Equal("someone@example.com", info!.Email);
        Assert.Equal(3, info.Meters.Count);

        Assert.Equal("Session", info.Meters[0].Label);
        Assert.Equal(3, info.Meters[0].Percent);
        Assert.Equal("normal", info.Meters[0].Severity);

        Assert.Equal("Week", info.Meters[1].Label);
        Assert.Equal(75, info.Meters[1].Percent);
        Assert.Equal("warning", info.Meters[1].Severity);

        // The scoped limit is named by its model, so the bar tracks whatever the plan meters today
        // rather than hardcoding "Fable" in the UI.
        Assert.Equal("Fable", info.Meters[2].Label);
        Assert.Equal(100, info.Meters[2].Percent);
        Assert.Equal("critical", info.Meters[2].Severity);

        Assert.NotEqual(DateTime.MinValue, info.Meters[2].ResetsAt);
    }

    // The config is rewritten non-atomically while the app polls it, so a scan can land mid-write.
    // Parse must say "no" rather than throw or hand back a half-empty AccountInfo — the caller
    // keeps showing the last good snapshot.
    [Fact]
    public void Torn_read_is_rejected_rather_than_half_parsed()
    {
        Assert.Null(AccountScanner.Parse(Config.Substring(0, Config.Length / 2)));
        Assert.Null(AccountScanner.Parse(""));
    }

    [Fact]
    public void Account_without_a_usage_cache_still_yields_the_email()
    {
        var info = AccountScanner.Parse("""
        { "oauthAccount": { "emailAddress": "someone@example.com" } }
        """);

        Assert.NotNull(info);
        Assert.Equal("someone@example.com", info!.Email);
        Assert.Empty(info.Meters);
        Assert.Equal(DateTime.MinValue, info.FetchedAt);
        // No fetch ever happened, so there's nothing to call stale.
        Assert.False(info.IsStale);
    }

    // An unknown limit kind is a schema change, not a bug — render it under its raw name instead of
    // dropping a limit the user is actually being metered against.
    [Fact]
    public void Unknown_limit_kinds_survive_under_their_raw_name()
    {
        var info = AccountScanner.Parse("""
        { "cachedUsageUtilization": { "utilization": { "limits": [
            { "kind": "monthly_something", "percent": 12, "severity": "normal" },
            { "kind": "weekly_scoped", "percent": 5, "severity": "normal", "scope": {} }
        ] } } }
        """);

        Assert.NotNull(info);
        Assert.Equal(2, info!.Meters.Count);
        Assert.Equal("monthly_something", info.Meters[0].Label);
        Assert.Equal(12, info.Meters[0].Percent);
        // Scoped, but the server didn't name a model.
        Assert.Equal("Scoped", info.Meters[1].Label);
    }

    [Fact]
    public void Usage_older_than_the_threshold_is_flagged_stale()
    {
        Assert.False(new AccountInfo { FetchedAt = DateTime.Now.AddMinutes(-1) }.IsStale);
        Assert.False(new AccountInfo
        {
            FetchedAt = DateTime.Now - AccountInfo.StaleAfter + TimeSpan.FromMinutes(1),
        }.IsStale);
        Assert.True(new AccountInfo
        {
            FetchedAt = DateTime.Now - AccountInfo.StaleAfter - TimeSpan.FromMinutes(1),
        }.IsStale);
    }

    // Regression: the threshold started at 15 minutes and cried stale during ordinary use — the
    // cache refresh is activity-driven, and observed gaps ran past 16 minutes with live-but-idle
    // sessions. A false "stale" on a working box is the worse failure, so keep real headroom.
    [Fact]
    public void Stale_threshold_clears_the_normal_refresh_gap()
    {
        Assert.True(AccountInfo.StaleAfter > TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Age_reads_in_the_largest_useful_unit()
    {
        Assert.Equal("20m", new AccountInfo { FetchedAt = DateTime.Now.AddMinutes(-20) }.AgeDisplay);
        Assert.Equal("3h", new AccountInfo { FetchedAt = DateTime.Now.AddHours(-3) }.AgeDisplay);
        Assert.Equal("2d", new AccountInfo { FetchedAt = DateTime.Now.AddDays(-2) }.AgeDisplay);
    }
}
