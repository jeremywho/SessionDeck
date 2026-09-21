using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionDeck;

/// <summary>
/// Discovers live sessions from ~/.claude/sessions/&lt;pid&gt;.json (one heartbeat file per live
/// session, named by OS process id) and enriches each with token/model/tool/title data parsed
/// from the tail of its JSONL transcript.
/// </summary>
internal static class SessionScanner
{
    static readonly string Home = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    static readonly string SessionsDir = Path.Combine(Home, "sessions");
    static readonly string ProjectsDir = Path.Combine(Home, "projects");

    /// <summary>The live-session registry directory — watched for change-driven refresh.</summary>
    public static string SessionsDirectory => SessionsDir;

    // Last known-good detail per session id. A turn in progress can briefly leave no parseable
    // assistant line in the tail; we reuse these instead of flashing "no model / 0%". Also caches the
    // transcript's mtime/size so an unchanged transcript skips the re-read entirely (see Enrich).
    internal sealed class Detail
    {
        public string Model = ""; public string Effort = ""; public long Ctx; public long Out; public string LastTool = ""; public string Title = "";
        public bool ApiError; public string ErrorText = "";
        public long Mtime; public long Size;
    }
    static readonly Dictionary<string, Detail> _detailCache = new();
    static readonly SubagentCounter _subagents = new();

    public static List<SessionInfo> Scan()
    {
        var list = new List<SessionInfo>();
        if (!Directory.Exists(SessionsDir)) return list;

        foreach (var file in Directory.EnumerateFiles(SessionsDir, "*.json"))
        {
            var s = TryReadSession(file);
            if (s == null || !IsAlive(s.Pid)) continue;   // skip dead/stale registry files
            Enrich(s);
            CountSubagents(s);
            list.Add(s);
        }

        list.Sort((a, b) => a.IdleSeconds.CompareTo(b.IdleSeconds));

        // forget cached detail (and fallback-path probes) for sessions that are no longer live
        var liveIds = new HashSet<string>(list.ConvertAll(x => x.SessionId));
        foreach (var key in new List<string>(_detailCache.Keys))
            if (!liveIds.Contains(key)) _detailCache.Remove(key);
        foreach (var key in new List<string>(_fallback.Keys))
            if (!liveIds.Contains(key)) _fallback.Remove(key);
        _subagents.Retain(new HashSet<string>(list.Select(SubagentsDir), StringComparer.OrdinalIgnoreCase));

        return list;
    }

    static SessionInfo? TryReadSession(string file)
    {
        try
        {
            string text;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();

            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;

            var s = new SessionInfo
            {
                Pid = GetInt(r, "pid"),
                SessionId = GetStr(r, "sessionId"),
                Cwd = GetStr(r, "cwd"),
                Name = GetStr(r, "name"),
                Status = GetStr(r, "status"),
                Version = GetStr(r, "version"),
                Kind = GetStr(r, "kind"),
            };
            s.StartedAt = FromUnixMs(GetLong(r, "startedAt"));
            s.UpdatedAt = FromUnixMs(GetLong(r, "updatedAt"));
            s.StatusUpdatedAt = FromUnixMs(GetLong(r, "statusUpdatedAt"));

            if (s.Pid == 0 || s.SessionId.Length == 0) return null;
            return s;
        }
        catch { return null; }
    }

    /// <summary>PID is alive AND it's actually a claude process (guards against PID reuse).</summary>
    static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // StartsWith, not Equals: the auto-updater renames the running exe
            // claude.exe -> claude.exe.old.<timestamp>, which still has ProcessName "claude*".
            // Still guards against PID reuse by unrelated processes.
            return !p.HasExited && p.ProcessName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void Enrich(SessionInfo s)
    {
        string path = TranscriptPath(s);
        s.TranscriptPath = path;

        long mtime = 0, size = 0;
        bool haveFile = false;
        if (path.Length > 0)
        {
            try { var fi = new FileInfo(path); if (fi.Exists) { mtime = fi.LastWriteTimeUtc.Ticks; size = fi.Length; haveFile = true; } }
            catch { }
        }

        _detailCache.TryGetValue(s.SessionId, out var prev);

        // #1 fast path: the transcript hasn't changed since we last parsed it -> reuse everything and
        // skip the (expensive) tail read + JSON parse. A cheap stat is all we do for an idle session.
        if (haveFile && prev != null && prev.Mtime == mtime && prev.Size == size)
        {
            s.Model = prev.Model; s.Effort = prev.Effort; s.ContextTokens = prev.Ctx; s.OutputTokens = prev.Out;
            s.LastTool = prev.LastTool; s.Title = prev.Title;
            s.ApiError = prev.ApiError; s.ErrorText = prev.ErrorText;
            return;
        }

        if (haveFile)
        {
            try
            {
                var lines = ReadTail(path, 256 * 1024);
                bool checkedLatest = false;
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string ln = lines[i];
                    if (ln.Length == 0) continue;

                    bool isAssistant = ln.Contains("\"type\":\"assistant\"");
                    if (isAssistant && !checkedLatest)
                    {
                        // is the MOST RECENT assistant message a synthetic API-error message?
                        checkedLatest = true;
                        if (ln.Contains("\"isApiErrorMessage\":true"))
                        {
                            s.ApiError = true;
                            s.ErrorText = ParseErrorText(ln);
                        }
                    }

                    if (s.Model.Length == 0 && isAssistant)
                        ParseAssistant(ln, s);   // skips "<synthetic>" -> lands on the last real turn
                    if (s.LastTool.Length == 0 && ln.Contains("\"tool_use\""))
                        s.LastTool = ParseLastTool(ln);
                    if (s.Title.Length == 0 && (ln.Contains("\"ai-title\"") || ln.Contains("\"custom-title\"")))
                        s.Title = ParseTitle(ln);

                    if (checkedLatest && s.Model.Length > 0 && s.LastTool.Length > 0 && s.Title.Length > 0) break;
                }
            }
            catch { }
        }

        if (prev != null) FillGapsFrom(s, prev);
        _detailCache[s.SessionId] = new Detail
        {
            Model = s.Model, Effort = s.Effort, Ctx = s.ContextTokens, Out = s.OutputTokens, LastTool = s.LastTool, Title = s.Title,
            ApiError = s.ApiError, ErrorText = s.ErrorText, Mtime = mtime, Size = size,
        };
    }

    // Fallback-search results, cached — including misses, which retry on a cooldown. The fallback
    // walks ALL of ~/.claude/projects, and TranscriptPath runs on every 2s scan: uncached, one
    // session with a missing transcript meant that whole recursive walk 30 times a minute.
    sealed record FallbackProbe(string Path, DateTime At);
    static readonly Dictionary<string, FallbackProbe> _fallback = new();
    static readonly TimeSpan FallbackRetry = TimeSpan.FromSeconds(30);

    static string TranscriptPath(SessionInfo s)
    {
        // cwd -> slug: every non-alphanumeric char becomes '-'  (C:\Users\Jeremy -> C--Users-Jeremy)
        string slug = Regex.Replace(s.Cwd, "[^a-zA-Z0-9]", "-");
        string p = Path.Combine(ProjectsDir, slug, s.SessionId + ".jsonl");
        if (File.Exists(p)) return p;

        if (_fallback.TryGetValue(s.SessionId, out var prev))
        {
            if (prev.Path.Length > 0 && File.Exists(prev.Path)) return prev.Path;
            if (DateTime.UtcNow - prev.At < FallbackRetry) return "";   // recent miss — don't re-walk yet
        }

        // Fallback: locate by session id anywhere under projects/ (slug rules vary across versions).
        string found = "";
        try
        {
            found = Directory.EnumerateFiles(ProjectsDir, s.SessionId + ".jsonl", SearchOption.AllDirectories)
                             .FirstOrDefault() ?? "";
        }
        catch { }
        _fallback[s.SessionId] = new FallbackProbe(found, DateTime.UtcNow);
        return found;
    }

    /// <summary>
    /// Count this session's subagents from &lt;transcriptDir&gt;/&lt;sessionId&gt;/subagents/agent-*.jsonl —
    /// total ever spawned, plus how many were written in the last ~30s (actively streaming = working now).
    /// The incremental cache inventories newly changed directories, stats only recently active files
    /// between passes, and periodically reconciles settled history to catch an unusual resumed agent.
    /// </summary>
    static void CountSubagents(SessionInfo s)
    {
        try
        {
            string dir = SubagentsDir(s);
            if (dir.Length == 0) return;
            (s.SubagentsTotal, s.SubagentsActive) = _subagents.Count(dir, DateTime.UtcNow);
        }
        catch { }
    }

    static string SubagentsDir(SessionInfo s)
    {
        string baseDir = s.TranscriptPath.Length > 0
            ? Path.GetDirectoryName(s.TranscriptPath) ?? ""
            : Path.Combine(ProjectsDir, Regex.Replace(s.Cwd, "[^a-zA-Z0-9]", "-"));
        return baseDir.Length > 0 ? Path.Combine(baseDir, s.SessionId, "subagents") : "";
    }

    /// <summary>
    /// Sticky last-known-good: a new turn can briefly leave no parseable assistant line in the tail,
    /// so anything this pass came up empty on falls back to the previous pass. It only fills empties,
    /// which is what lets a real drop — a smaller context after auto-compact — still show through.
    /// The consequence worth knowing: a value never goes back to unknown while the session lives, so
    /// an effort or model that genuinely stops being reported keeps displaying its last real value.
    /// </summary>
    internal static void FillGapsFrom(SessionInfo s, Detail prev)
    {
        if (s.Model.Length == 0) s.Model = prev.Model;
        if (s.Effort.Length == 0) s.Effort = prev.Effort;
        if (s.ContextTokens == 0) s.ContextTokens = prev.Ctx;
        if (s.OutputTokens == 0) s.OutputTokens = prev.Out;
        if (s.LastTool.Length == 0) s.LastTool = prev.LastTool;
        if (s.Title.Length == 0) s.Title = prev.Title;
    }

    /// <summary>
    /// Reads model, reasoning effort and token usage off one assistant transcript record. Effort sits at
    /// the record ROOT, not inside <c>message</c> — Claude Code stamps the level that actually ran on the
    /// turn, after any silent downgrade for a model that can't do the requested one. It is therefore the
    /// last completed turn's effort, not necessarily what a later <c>/effort</c> selected.
    /// </summary>
    internal static void ParseAssistant(string line, SessionInfo s)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            if (!d.RootElement.TryGetProperty("message", out var m)) return;
            string model = m.TryGetProperty("model", out var mo) ? (mo.GetString() ?? "") : "";
            if (model == "<synthetic>") return;   // error/synthetic message — not a real model/usage turn
            s.Model = model;
            s.Effort = GetStr(d.RootElement, "effort");
            if (m.TryGetProperty("usage", out var u))
            {
                s.ContextTokens = U(u, "input_tokens") + U(u, "cache_read_input_tokens") + U(u, "cache_creation_input_tokens");
                s.OutputTokens = U(u, "output_tokens");
            }
        }
        catch { }
    }

    static long U(JsonElement u, string name)
        => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    static string ParseErrorText(string line)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            if (d.RootElement.TryGetProperty("message", out var m) &&
                m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in c.EnumerateArray())
                    if (b.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        b.TryGetProperty("text", out var tx))
                        return (tx.GetString() ?? "").Trim();
            }
        }
        catch { }
        return "API error";
    }

    static string ParseLastTool(string line)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            if (!d.RootElement.TryGetProperty("message", out var m)) return "";
            if (!m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) return "";

            string name = "";
            foreach (var block in c.EnumerateArray())
                if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                    && block.TryGetProperty("name", out var n))
                    name = n.GetString() ?? name;
            return name;
        }
        catch { return ""; }
    }

    static string ParseTitle(string line)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            foreach (var prop in d.RootElement.EnumerateObject())
                if ((prop.NameEquals("aiTitle") || prop.NameEquals("customTitle") || prop.NameEquals("title"))
                    && prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString() ?? "";
            return "";
        }
        catch { return ""; }
    }

    static string[] ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        // The first line may be a partial fragment from the seek; it just fails to parse and is skipped.
        return sr.ReadToEnd().Split('\n');
    }

    static int GetInt(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    static long GetLong(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    static string GetStr(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static DateTime FromUnixMs(long ms) => ms <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
}
