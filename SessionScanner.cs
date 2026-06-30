using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSessionMonitor;

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

    // Last known-good detail per session id. A turn in progress can briefly leave no parseable
    // assistant line in the tail; we reuse these instead of flashing "no model / 0%".
    sealed class Detail { public string Model = ""; public long Ctx; public long Out; public string LastTool = ""; public string Title = ""; }
    static readonly Dictionary<string, Detail> _detailCache = new();

    public static List<SessionInfo> Scan()
    {
        var list = new List<SessionInfo>();
        if (!Directory.Exists(SessionsDir)) return list;

        foreach (var file in Directory.EnumerateFiles(SessionsDir, "*.json"))
        {
            var s = TryReadSession(file);
            if (s == null || !IsAlive(s.Pid)) continue;   // skip dead/stale registry files
            Enrich(s);
            list.Add(s);
        }

        list.Sort((a, b) => a.IdleSeconds.CompareTo(b.IdleSeconds));

        // forget cached detail for sessions that are no longer live
        var liveIds = new HashSet<string>(list.ConvertAll(x => x.SessionId));
        foreach (var key in new List<string>(_detailCache.Keys))
            if (!liveIds.Contains(key)) _detailCache.Remove(key);

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

        if (path.Length > 0 && File.Exists(path))
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

        // Sticky last-known-good: a new turn can briefly leave no parseable assistant line in the
        // tail. Reuse cached values for anything we came up empty on, then remember the good ones.
        // (Only fills empties, so a real drop — e.g. after auto-compact — still updates.)
        _detailCache.TryGetValue(s.SessionId, out var prev);
        if (prev != null)
        {
            if (s.Model.Length == 0) s.Model = prev.Model;
            if (s.ContextTokens == 0) s.ContextTokens = prev.Ctx;
            if (s.OutputTokens == 0) s.OutputTokens = prev.Out;
            if (s.LastTool.Length == 0) s.LastTool = prev.LastTool;
            if (s.Title.Length == 0) s.Title = prev.Title;
        }
        _detailCache[s.SessionId] = new Detail
        {
            Model = s.Model, Ctx = s.ContextTokens, Out = s.OutputTokens, LastTool = s.LastTool, Title = s.Title,
        };
    }

    static string TranscriptPath(SessionInfo s)
    {
        // cwd -> slug: every non-alphanumeric char becomes '-'  (C:\Users\Jeremy -> C--Users-Jeremy)
        string slug = Regex.Replace(s.Cwd, "[^a-zA-Z0-9]", "-");
        string p = Path.Combine(ProjectsDir, slug, s.SessionId + ".jsonl");
        if (File.Exists(p)) return p;

        // Fallback: locate by session id anywhere under projects/ (slug rules vary across versions).
        try
        {
            return Directory.EnumerateFiles(ProjectsDir, s.SessionId + ".jsonl", SearchOption.AllDirectories)
                            .FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    static void ParseAssistant(string line, SessionInfo s)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            if (!d.RootElement.TryGetProperty("message", out var m)) return;
            string model = m.TryGetProperty("model", out var mo) ? (mo.GetString() ?? "") : "";
            if (model == "<synthetic>") return;   // error/synthetic message — not a real model/usage turn
            s.Model = model;
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
