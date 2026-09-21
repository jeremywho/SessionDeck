using System.IO;
using System.Text.Json;

namespace SessionDeck;

/// <summary>
/// One usage limit as the server reports it — the session (5h) window, the all-model weekly window,
/// or a weekly window scoped to a single model. Deliberately not modelled as an enum: the set of
/// limits is server-driven and grows.
/// </summary>
internal sealed class UsageMeter
{
    // Properties, not fields: this type is bound directly by the usage bar's ItemTemplate, and WPF
    // bindings resolve properties only — as fields these render blank with no error anywhere.
    public string Label { get; set; } = "";
    public int Percent { get; set; }
    public string Severity { get; set; } = "";   // "normal" / "warning" / "critical" — open set
    public DateTime ResetsAt { get; set; }       // local; MinValue when absent

    /// <summary>Extra tooltip line naming where the number came from (e.g. the Codex plan).</summary>
    public string Note { get; set; } = "";

    /// <summary>When the source wrote this reading; MinValue to omit. Unlike the Claude meters — which
    /// the app polls on its own schedule — the Codex numbers are only as fresh as your last Codex turn,
    /// so the age has to be visible somewhere.</summary>
    public DateTime ReadAt { get; set; }

    public string PercentDisplay => $"{Percent}%";

    // A getter, not a stored string: it's evaluated when the tooltip opens, so "resets in…" and the
    // reading's age stay true even though the meter object itself is only rebuilt when a value changes.
    public string Tooltip
    {
        get
        {
            var s = $"{Label} — {Percent}% used";
            if (ResetsAt != DateTime.MinValue)
            {
                var d = ResetsAt - DateTime.Now;
                s += d.TotalSeconds <= 0
                    ? "\nResetting now"
                    : d.TotalHours < 24
                        ? $"\nResets in {(int)d.TotalHours}h {d.Minutes}m ({ResetsAt:h:mm tt})"
                        : $"\nResets in {(int)d.TotalDays}d {d.Hours}h ({ResetsAt:ddd h:mm tt})";
            }
            if (Note.Length > 0) s += $"\n{Note}";
            if (ReadAt != DateTime.MinValue)
                s += $"\nAs of your last Codex turn, {AccountInfo.AgeText(DateTime.Now - ReadAt)} ago";
            return s;
        }
    }
}

/// <summary>Signed-in account plus the usage meters, as cached on disk by Claude Code.</summary>
internal sealed class AccountInfo
{
    public string Email = "";
    public string Organization = "";

    /// <summary>Stable id of the signed-in account. The address can be reused across logins and is
    /// missing entirely when signed out, so identity comparisons key off this instead.</summary>
    public string AccountUuid = "";

    public List<UsageMeter> Meters = new();
    public DateTime FetchedAt;       // local; MinValue when the file carried no usage cache

    // No staleness threshold lives here any more, deliberately. Two were tried against this cache
    // (15 then 45 minutes) and both cried stale during ordinary use: the refresh answers to nothing
    // observable from outside — it was caught sitting 52 minutes old while a session was flat out.
    // Judging this file's freshness means inventing a number, so the app polls the API itself
    // (UsageApi) and judges freshness against its own interval instead. What's left here is the
    // fallback for when that call fails, and it reports itself as cached rather than as current.

    public TimeSpan Age => FetchedAt == DateTime.MinValue ? TimeSpan.Zero : DateTime.Now - FetchedAt;

    public string AgeDisplay => AgeText(Age);

    /// <summary>Coarse "how long ago", largest useful unit only.</summary>
    public static string AgeText(TimeSpan age) =>
        age.TotalHours >= 24 ? $"{(int)age.TotalDays}d" :
        age.TotalMinutes >= 60 ? $"{(int)age.TotalHours}h" :
        $"{(int)age.TotalMinutes}m";
}

/// <summary>
/// Reads the signed-in account and plan-usage percentages from <c>~/.claude.json</c> — a different
/// file from everything in <see cref="SessionScanner"/>, which lives under <c>~/.claude/</c>.
/// Undocumented, like the rest; kept in this one file so a schema change is a one-file fix.
/// </summary>
internal static class AccountScanner
{
    static readonly string ConfigFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    // The file is ~50KB and Claude Code rewrites it often, so cache on mtime/size (same trick as
    // SessionScanner's transcript reads) — an unchanged file costs a stat. Holding the last good
    // parse also rides out a torn read: the config is written non-atomically, so a scan landing
    // mid-write sees truncated JSON, and flashing an empty bar for one tick would be worse.
    static AccountInfo _cached = new();
    static long _mtime, _size;

    public static AccountInfo Read()
    {
        try
        {
            var fi = new FileInfo(ConfigFile);
            if (!fi.Exists) return _cached;

            var mtime = fi.LastWriteTimeUtc.Ticks;
            if (mtime == _mtime && fi.Length == _size) return _cached;

            string text;
            using (var fs = new FileStream(ConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();

            var parsed = Parse(text);
            if (parsed == null) return _cached;      // torn read — keep the last good one

            _mtime = mtime; _size = fi.Length;
            _cached = parsed;
        }
        catch { }   // best-effort; the previous snapshot stays on screen
        return _cached;
    }

    /// <summary>
    /// Did the signed-in account change between two readings? Only a *transition* counts: the very
    /// first reading (<paramref name="previous"/> null) is not a switch, or the app would throw away
    /// the poll it just made at startup. Signing out — a real uuid going empty — does count.
    /// </summary>
    public static bool AccountChanged(string? previous, string current) =>
        previous != null && !string.Equals(previous, current, StringComparison.Ordinal);

    /// <summary>Parses the config text. Returns null if it isn't valid JSON (a torn read).</summary>
    public static AccountInfo? Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            var info = new AccountInfo();

            if (root.TryGetProperty("oauthAccount", out var acct) && acct.ValueKind == JsonValueKind.Object)
            {
                info.Email = Str(acct, "emailAddress");
                info.Organization = Str(acct, "organizationName");
                info.AccountUuid = Str(acct, "accountUuid");
            }

            if (!root.TryGetProperty("cachedUsageUtilization", out var cache) ||
                cache.ValueKind != JsonValueKind.Object) return info;

            if (cache.TryGetProperty("fetchedAtMs", out var f) && f.TryGetInt64(out var ms))
                info.FetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

            if (cache.TryGetProperty("utilization", out var util) && util.ValueKind == JsonValueKind.Object)
                info.Meters = ParseLimits(util);

            return info;
        }
    }

    /// <summary>
    /// Parses the API's usage response. Its body is the same object the config caches under
    /// <c>cachedUsageUtilization.utilization</c>, so this is the cache parser pointed at a different
    /// source. Returns null if the body isn't valid JSON.
    /// </summary>
    public static List<UsageMeter>? ParseApiUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? ParseLimits(doc.RootElement)
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Reads the self-describing <c>limits</c> array off a utilization object, rather than the
    /// sibling <c>five_hour</c> / <c>seven_day_opus</c> fields: the per-model limits show up *only*
    /// here (as weekly_scoped with a model scope, while seven_day_opus and friends stay null), and
    /// each entry carries the server's own severity, so nothing here has to guess a threshold.
    /// </summary>
    static List<UsageMeter> ParseLimits(JsonElement utilization)
    {
        var meters = new List<UsageMeter>();
        if (!utilization.TryGetProperty("limits", out var limits) ||
            limits.ValueKind != JsonValueKind.Array) return meters;

        foreach (var l in limits.EnumerateArray())
        {
            if (l.ValueKind != JsonValueKind.Object) continue;
            var meter = new UsageMeter
            {
                Label = LabelFor(Str(l, "kind"), l),
                Percent = l.TryGetProperty("percent", out var p) && p.TryGetInt32(out var pv) ? pv : 0,
                Severity = Str(l, "severity"),
            };
            if (DateTimeOffset.TryParse(Str(l, "resets_at"), out var r))
                meter.ResetsAt = r.LocalDateTime;
            meters.Add(meter);
        }
        return meters;
    }

    /// <summary>
    /// Display name for a limit. A scoped limit is named by its model ("Fable"), so the bar follows
    /// whatever model the plan actually meters instead of hardcoding today's answer. An unrecognised
    /// kind still renders — under its raw name — rather than vanishing.
    /// </summary>
    static string LabelFor(string kind, JsonElement limit)
    {
        switch (kind)
        {
            // "5h", not "Session": the window it means is the one thing you want at a glance, and
            // the server's own `five_hour` field carries the same percent and reset time as this
            // limit. "Session" read as ambiguous next to a weekly pill.
            case "session": return "5h";
            case "weekly_all": return "Week";
            case "weekly_scoped":
                if (limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
                    scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                {
                    var name = Str(model, "display_name");
                    if (name.Length > 0) return name;
                }
                return "Scoped";
            default: return kind;
        }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
