using System.IO;
using System.Text.Json;

namespace ClaudeSessionMonitor;

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

    public string PercentDisplay => $"{Percent}%";

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
            return s;
        }
    }
}

/// <summary>Signed-in account plus the usage meters, as cached on disk by Claude Code.</summary>
internal sealed class AccountInfo
{
    public string Email = "";
    public string Organization = "";
    public List<UsageMeter> Meters = new();
    public DateTime FetchedAt;       // local; MinValue when the file carried no usage cache

    /// <summary>
    /// The usage block is a *cache* — only a running Claude Code process refreshes it. With no live
    /// session the numbers sit there going quietly wrong, so past this age we say so in the UI
    /// rather than present them as current.
    /// <para>The refresh is activity-driven, not a fixed tick: observed gaps ran ~10 minutes while
    /// turns were in flight but stretched past 16 with every session sitting idle. 15 minutes was
    /// tried first and cried stale during ordinary use, so this is set well clear of the idle case —
    /// a false "stale" on a live box is worse than being slow to flag a genuinely cold one.</para>
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(45);

    public TimeSpan Age => FetchedAt == DateTime.MinValue ? TimeSpan.Zero : DateTime.Now - FetchedAt;
    public bool IsStale => FetchedAt != DateTime.MinValue && Age > StaleAfter;

    /// <summary>
    /// Hover text for the account label, which ellipsizes on a narrow window. Deliberately carries
    /// the absolute fetch time rather than a relative age, so the string only changes when the data
    /// does — re-assigning it every tick would dismiss the tooltip the user is trying to read.
    /// </summary>
    public string Tooltip
    {
        get
        {
            var s = Email.Length > 0 ? Email : "Not signed in";
            if (Organization.Length > 0) s += $"\n{Organization}";
            s += FetchedAt == DateTime.MinValue
                ? "\nNo usage data cached yet"
                : $"\nUsage last updated {FetchedAt:h:mm tt}";
            return s;
        }
    }

    public string AgeDisplay =>
        Age.TotalHours >= 24 ? $"{(int)Age.TotalDays}d" :
        Age.TotalMinutes >= 60 ? $"{(int)Age.TotalHours}h" :
        $"{(int)Age.TotalMinutes}m";
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
            }

            if (!root.TryGetProperty("cachedUsageUtilization", out var cache) ||
                cache.ValueKind != JsonValueKind.Object) return info;

            if (cache.TryGetProperty("fetchedAtMs", out var f) && f.TryGetInt64(out var ms))
                info.FetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

            if (!cache.TryGetProperty("utilization", out var util) ||
                util.ValueKind != JsonValueKind.Object) return info;

            // Read the self-describing `limits` array rather than the sibling `five_hour` /
            // `seven_day_opus` fields: the per-model limits show up *only* here (as weekly_scoped
            // with a model scope, while seven_day_opus and friends stay null), and each entry
            // carries the server's own severity, so nothing here has to guess a threshold.
            if (!util.TryGetProperty("limits", out var limits) ||
                limits.ValueKind != JsonValueKind.Array) return info;

            foreach (var l in limits.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(l, "kind");
                var meter = new UsageMeter
                {
                    Label = LabelFor(kind, l),
                    Percent = l.TryGetProperty("percent", out var p) && p.TryGetInt32(out var pv) ? pv : 0,
                    Severity = Str(l, "severity"),
                };
                if (DateTimeOffset.TryParse(Str(l, "resets_at"), out var r))
                    meter.ResetsAt = r.LocalDateTime;
                info.Meters.Add(meter);
            }

            return info;
        }
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
            case "session": return "Session";
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
