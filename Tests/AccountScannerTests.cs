using Xunit;

namespace SessionDeck.Tests;

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

        Assert.Equal("5h", info.Meters[0].Label);
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

    // The API returns the same object the config caches under cachedUsageUtilization.utilization,
    // so one parser serves both. This is that response, captured live.
    const string ApiResponse = """
    {
      "five_hour": { "utilization": 6.0, "resets_at": "2026-07-22T18:09:59.703915+00:00" },
      "seven_day": { "utilization": 75.0, "resets_at": "2026-07-25T22:59:59.703935+00:00" },
      "seven_day_opus": null,
      "limits": [
        { "kind": "session", "group": "session", "percent": 6, "severity": "normal",
          "resets_at": "2026-07-22T18:09:59.703915+00:00", "scope": null, "is_active": false },
        { "kind": "weekly_all", "group": "weekly", "percent": 75, "severity": "warning",
          "resets_at": "2026-07-25T22:59:59.703935+00:00", "scope": null, "is_active": false },
        { "kind": "weekly_scoped", "group": "weekly", "percent": 100, "severity": "critical",
          "resets_at": "2026-07-25T22:59:59.704193+00:00",
          "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
          "is_active": true }
      ]
    }
    """;

    [Fact]
    public void Api_response_parses_through_the_same_path_as_the_cache()
    {
        var live = AccountScanner.ParseApiUsage(ApiResponse);

        Assert.NotNull(live);
        Assert.Equal(3, live!.Count);
        Assert.Equal(new[] { "5h", "Week", "Fable" }, live.ConvertAll(m => m.Label));
        Assert.Equal(new[] { 6, 75, 100 }, live.ConvertAll(m => m.Percent));
        Assert.Equal(new[] { "normal", "warning", "critical" }, live.ConvertAll(m => m.Severity));
    }

    // The two sources must not drift: same limits in, same meters out, whichever door they came
    // through. Only the percentages differ here (the cache sample is older).
    [Fact]
    public void Cache_and_api_agree_on_labels_and_severities()
    {
        var cached = AccountScanner.Parse(Config)!.Meters;
        var live = AccountScanner.ParseApiUsage(ApiResponse)!;

        Assert.Equal(cached.ConvertAll(m => m.Label), live.ConvertAll(m => m.Label));
        Assert.Equal(cached.ConvertAll(m => m.Severity), live.ConvertAll(m => m.Severity));
    }

    [Fact]
    public void Api_garbage_is_rejected_rather_than_shown()
    {
        Assert.Null(AccountScanner.ParseApiUsage("not json"));
        Assert.Null(AccountScanner.ParseApiUsage(""));
        Assert.Null(AccountScanner.ParseApiUsage("[1,2,3]"));          // wrong root kind
        Assert.Empty(AccountScanner.ParseApiUsage("{}")!);             // valid, just no limits
    }

    [Fact]
    public void Age_reads_in_the_largest_useful_unit()
    {
        Assert.Equal("20m", new AccountInfo { FetchedAt = DateTime.Now.AddMinutes(-20) }.AgeDisplay);
        Assert.Equal("3h", new AccountInfo { FetchedAt = DateTime.Now.AddHours(-3) }.AgeDisplay);
        Assert.Equal("2d", new AccountInfo { FetchedAt = DateTime.Now.AddDays(-2) }.AgeDisplay);
    }
}
