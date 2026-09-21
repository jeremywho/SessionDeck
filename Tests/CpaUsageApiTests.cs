using Xunit;

namespace SessionDeck.Tests;

public class CpaUsageApiTests
{
    const string Dashboard = """
    {
      "rows": [
        {
          "email": "waiting@example.com",
          "priority": 20,
          "disabled": false,
          "hasData": true,
          "state": "healthy",
          "meters": [
            { "label": "5-hour", "pct": 99, "resetsAt": null, "severity": "critical" }
          ]
        },
        {
          "email": "serving@example.com",
          "priority": 10,
          "disabled": false,
          "hasData": true,
          "state": "healthy",
          "meters": [
            { "label": "5-hour", "pct": 3.6,
              "resetsAt": "2026-08-22T20:00:00.000Z", "severity": "normal" },
            { "label": "Weekly (all)", "pct": 12,
              "resetsAt": "2026-08-28T20:00:00.000Z", "severity": "warning" },
            { "label": "Weekly (Fable)", "pct": 87,
              "resetsAt": "2026-08-28T20:00:00.000Z", "severity": "critical" }
          ]
        }
      ],
      "fetchedAt": "2026-08-22T17:30:00.000Z",
      "cacheAgeMs": 90000,
      "generatedAt": "2026-08-22T17:31:30.000Z",
      "serving": {
        "account": "SERVING@example.com",
        "when": "2026-08-22T17:00:00.000Z"
      },
      "proxyUp": true
    }
    """;

    [Fact]
    public void Selects_only_the_account_named_by_the_current_binding()
    {
        var snapshot = CpaUsageApi.Parse(Dashboard);

        Assert.NotNull(snapshot);
        Assert.Equal("serving@example.com", snapshot!.Email, ignoreCase: true);
        Assert.Equal("cpa:serving@example.com", snapshot.AccountKey);
        Assert.True(snapshot.ProxyUp);
        Assert.True(snapshot.HasData);
        Assert.Equal("healthy", snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(90), snapshot.CacheAgeAtRead);
        Assert.NotEqual(DateTime.MinValue, snapshot.UsageFetchedAt);
        Assert.NotEqual(DateTime.MinValue, snapshot.BindingAt);

        Assert.Equal(new[] { "5h", "Week", "Fable" }, snapshot.Meters.ConvertAll(m => m.Label));
        Assert.Equal(new[] { 4, 12, 87 }, snapshot.Meters.ConvertAll(m => m.Percent));
        Assert.Equal(new[] { "normal", "warning", "critical" },
            snapshot.Meters.ConvertAll(m => m.Severity));
        Assert.All(snapshot.Meters, m => Assert.Equal("CPA · selected Claude account", m.Note));
        Assert.DoesNotContain(snapshot.Meters, m => m.Percent == 99);
    }

    [Fact]
    public void A_bound_account_without_a_matching_cache_row_keeps_identity_but_has_no_data()
    {
        var snapshot = CpaUsageApi.Parse("""
        {
          "rows": [],
          "serving": { "account": "new@example.com", "when": "2026-08-22T17:00:00Z" },
          "proxyUp": false
        }
        """);

        Assert.NotNull(snapshot);
        Assert.Equal("new@example.com", snapshot!.Email);
        Assert.False(snapshot.ProxyUp);
        Assert.False(snapshot.HasData);
        Assert.Empty(snapshot.Meters);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"serving\": null }")]
    [InlineData("{ \"serving\": {} }")]
    [InlineData("not json")]
    public void Missing_or_invalid_binding_is_rejected(string json)
    {
        Assert.Null(CpaUsageApi.Parse(json));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8317/", true)]
    [InlineData("http://localhost:8317/v1", true)]
    [InlineData("http://[::1]:8317/", true)]
    [InlineData("http://127.0.0.1:8318/", false)]
    [InlineData("https://api.anthropic.com/", false)]
    [InlineData("not a url", false)]
    public void Detects_only_the_loopback_CPA_proxy(string url, bool expected)
    {
        Assert.Equal(expected, CpaUsageApi.IsCpaBaseUrl(url));
    }

    [Theory]
    [InlineData("5-hour", "5h")]
    [InlineData("Weekly (all)", "Week")]
    [InlineData("Weekly (Fable)", "Fable")]
    [InlineData("Monthly", "Monthly")]
    public void Dashboard_labels_are_compacted_for_the_footer(string input, string expected)
    {
        Assert.Equal(expected, CpaUsageApi.CompactLabel(input));
    }
}
