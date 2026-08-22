using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeSessionMonitor;

/// <summary>One sanitized reading from the loopback-only CPA usage dashboard.</summary>
internal sealed class CpaUsageSnapshot
{
    public string AccountKey = "";
    public string Email = "";
    public bool ProxyUp;
    public bool HasData;
    public string State = "";
    public List<UsageMeter> Meters = new();
    public DateTime UsageFetchedAt;
    public DateTime BindingAt;
    public DateTime ReadAt = DateTime.Now;
    public TimeSpan CacheAgeAtRead;

    public TimeSpan CacheAge => CacheAgeAtRead + (DateTime.Now - ReadAt);
}

/// <summary>
/// Reads the active Claude account and its usage from Jeremy's local CPA dashboard. The dashboard
/// already performs the sensitive work: it reads CPA's binding log and credential metadata, refreshes
/// the multi-account usage cache, and exposes only sanitized identity/usage JSON on loopback. This
/// client sends no credentials and never opens CPA's auth files.
/// </summary>
internal static class CpaUsageApi
{
    const string DefaultUrl = "http://127.0.0.1:8318/api/usage";
    internal const string UrlVariable = "CSM_CPA_USAGE_URL";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    /// <summary>The dashboard itself polls every 20s; match it so an account switch appears promptly.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    /// <summary>The dashboard attempts a usage-cache refresh after two minutes. More than two missed
    /// refresh opportunities is objectively stale rather than a guessed threshold.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UrlVariable)) ||
        IsCpaBaseUrl(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"));

    internal static bool IsCpaBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback && uri.Port == 8317;
    }

    public static async Task<CpaUsageSnapshot?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            string url = Environment.GetEnvironmentVariable(UrlVariable) ?? DefaultUrl;
            using var res = await Http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            return Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch { return null; }
    }

    /// <summary>Select the dashboard row named by <c>serving.account</c>. The other account rows are
    /// deliberately ignored: the footer describes the account CPA is routing right now, not the pool.</summary>
    internal static CpaUsageSnapshot? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("serving", out var serving) || serving.ValueKind != JsonValueKind.Object)
                return null;

            string account = Str(serving, "account");
            if (account.Length == 0) return null;

            var snapshot = new CpaUsageSnapshot
            {
                AccountKey = "cpa:" + account.ToLowerInvariant(),
                Email = account,
                ProxyUp = Bool(root, "proxyUp"),
            };

            if (DateTimeOffset.TryParse(Str(serving, "when"), out var binding))
                snapshot.BindingAt = binding.LocalDateTime;
            if (DateTimeOffset.TryParse(Str(root, "fetchedAt"), out var fetched))
                snapshot.UsageFetchedAt = fetched.LocalDateTime;
            if (root.TryGetProperty("cacheAgeMs", out var age) && age.TryGetDouble(out double ms))
                snapshot.CacheAgeAtRead = TimeSpan.FromMilliseconds(Math.Max(0, ms));

            if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return snapshot;

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !string.Equals(Str(row, "email"), account, StringComparison.OrdinalIgnoreCase)) continue;

                snapshot.HasData = Bool(row, "hasData");
                snapshot.State = Str(row, "state");
                if (!row.TryGetProperty("meters", out var meters) || meters.ValueKind != JsonValueKind.Array)
                    return snapshot;

                foreach (var m in meters.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object ||
                        !m.TryGetProperty("pct", out var pct) || !pct.TryGetDouble(out double used)) continue;

                    var meter = new UsageMeter
                    {
                        Label = CompactLabel(Str(m, "label")),
                        Percent = Math.Clamp((int)Math.Round(used, MidpointRounding.AwayFromZero), 0, 100),
                        Severity = Str(m, "severity"),
                        Note = "CPA · selected Claude account",
                    };
                    if (DateTimeOffset.TryParse(Str(m, "resetsAt"), out var reset))
                        meter.ResetsAt = reset.LocalDateTime;
                    snapshot.Meters.Add(meter);
                }
                return snapshot;
            }

            return snapshot;
        }
        catch (JsonException) { return null; }
    }

    internal static string CompactLabel(string label)
    {
        if (label.Equals("5-hour", StringComparison.OrdinalIgnoreCase)) return "5h";
        if (label.Equals("Weekly (all)", StringComparison.OrdinalIgnoreCase)) return "Week";
        const string prefix = "Weekly (";
        if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && label.EndsWith(')'))
            return label[prefix.Length..^1];
        return label;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True ||
            v.ValueKind == JsonValueKind.False) && v.GetBoolean();
}
