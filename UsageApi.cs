using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeSessionMonitor;

/// <summary>
/// Fetches plan usage straight from the account's usage endpoint, rather than reading whatever
/// Claude Code last happened to cache on disk.
/// <para>This is the one place the app talks to the network. It exists because the on-disk cache
/// (<c>~/.claude.json</c> → <c>cachedUsageUtilization</c>) refreshes on no schedule we can predict —
/// it was observed sitting 52 minutes stale while a session hammered the API — which makes any
/// "is this current?" answer derived from it a guess. Polling ourselves means the refresh interval
/// is ours, so staleness becomes a fact instead of an invention.</para>
/// <para>The response body is byte-identical in shape to the cached <c>utilization</c> object, so
/// <see cref="AccountScanner.ParseLimits"/> handles both.</para>
/// </summary>
internal static class UsageApi
{
    const string Url = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>Claude Code's live auth state — read here for the bearer token, and watched by
    /// <see cref="CredentialsWatcher"/> because it being replaced is what an account switch looks
    /// like from outside. Never written; see <see cref="ReadAccessToken"/>.</summary>
    public static readonly string CredentialsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Poll interval. Also the basis for the staleness threshold — see SessionsWindow.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// GETs the current usage. Returns null on any failure — expired token, offline, endpoint moved —
    /// and the caller falls back to the on-disk cache. Never throws.
    /// </summary>
    public static async Task<List<UsageMeter>?> FetchAsync(CancellationToken ct = default)
    {
        // Test hook: force the offline/failed-call path so the cache fallback can be exercised
        // without unplugging anything. Same spirit as CSM_FAKE_UPDATE / CSM_NO_INSTALL.
        if (Environment.GetEnvironmentVariable("CSM_NO_USAGE_API") == "1") return null;

        try
        {
            // Re-read per call rather than caching: Claude Code refreshes this token roughly every
            // 12 hours and rewrites the file, and a token we held onto would 401 from then on.
            var token = ReadAccessToken();
            if (string.IsNullOrEmpty(token)) return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, Url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Headers.TryAddWithoutValidation("User-Agent", "ClaudeSessionMonitor");

            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            var body = await res.Content.ReadAsStringAsync(ct);
            return AccountScanner.ParseApiUsage(body);
        }
        catch { return null; }   // offline / DNS / timeout / cancelled — the cache covers us
    }

    /// <summary>
    /// Reads the OAuth access token. STRICTLY read-only: this file is Claude Code's own auth state,
    /// and writing to it — even to "helpfully" refresh an expired token — risks breaking the user's
    /// sign-in. An expired token here just means FetchAsync returns null and we show the cache.
    /// </summary>
    static string? ReadAccessToken()
    {
        try
        {
            if (!File.Exists(CredentialsFile)) return null;
            string text;
            using (var fs = new FileStream(CredentialsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o) ||
                o.ValueKind != JsonValueKind.Object) return null;
            return o.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch { return null; }
    }
}
