using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionDeck;

internal readonly record struct ToolCall(string Text, DateTime At);

internal sealed record FoldedSource(string TranscriptPath, SessionProvider Provider, string Kind, string Cwd, DateTime UpdatedAt);

internal sealed record SessionSource(string SessionId, SessionProvider Provider, string Kind, string Cwd, DateTime UpdatedAt,
                                     string TranscriptPath, IReadOnlyList<FoldedSource> Folded);

internal sealed record AgentLink(PrKey Pr, string SessionId, int Count, DateTime LastHit, bool ViaCodex, string Evidence);

internal sealed class PrTargets
{
    public required IReadOnlyList<(string Needle, PrKey Pr, string Folder)> Worktrees { get; init; }
    public required IReadOnlySet<PrKey> Open { get; init; }
    public required IReadOnlyDictionary<int, PrKey[]> ByNumber { get; init; }

    public static PrTargets From(IReadOnlyList<PullRequest> prs, IReadOnlyList<Worktree> worktrees)
    {
        var needles = new List<(string Needle, PrKey Pr, string Folder)>();
        foreach (var pr in prs)
            foreach (var w in worktrees.Where(w => w.Matches(pr)))
                foreach (var path in new[] { w.Path, w.CanonicalPath })
                    needles.Add((PrAttribution.WithoutDrive(PrAttribution.Normalize(path)).TrimEnd('/'), pr.Key, System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))));
        return new PrTargets
        {
            Worktrees = needles.Distinct().OrderByDescending(n => n.Needle.Length).ToList(),
            Open = prs.Select(p => p.Key).ToHashSet(),
            ByNumber = prs.GroupBy(p => p.Number).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray()),
        };
    }
}

internal static class PrAttribution
{
    public const int Window = 100;
    public const int Threshold = 3;
    public static readonly TimeSpan SubagentWindow = TimeSpan.FromHours(2);

    static readonly Regex PullLink = new(@"github\.com/([a-z0-9_.-]+)/([a-z0-9_.-]+)/pull/(\d+)", RegexOptions.Compiled);
    static readonly Regex ApiPull = new(@"repos/([a-z0-9_.-]+)/([a-z0-9_.-]+)/(?:pulls|issues)/(\d+)", RegexOptions.Compiled);
    static readonly Regex GhPr = new(@"\bgh\s+pr\s+[a-z-]+\s+#?(\d+)\b", RegexOptions.Compiled);
    static readonly Regex RepoFlag = new(@"(?:--repo|-r)[\s=]+([a-z0-9_.-]+/[a-z0-9_.-]+)", RegexOptions.Compiled);
    static readonly string[] CommandSeparators = { "&&", ";", "|", "\n" };

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool slash = false;
        foreach (char c in s)
        {
            if (c is '\\' or '/')
            {
                if (!slash) sb.Append('/');
                slash = true;
                continue;
            }
            slash = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static string WithoutDrive(string normalized) =>
        normalized.Length >= 2 && normalized[1] == ':' && char.IsLetter(normalized[0]) ? normalized[2..] : normalized;

    public static List<ToolCall> ToolCalls(IEnumerable<string> lines)
    {
        var calls = new List<ToolCall>();
        foreach (var line in lines)
        {
            if (!line.Contains("\"tool_use\"", StringComparison.Ordinal) && !line.Contains("_call\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                DateTime at = DateTime.TryParse(Str(r, "timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue;
                string type = Str(r, "type");
                if (type == "assistant" && r.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && Str(item, "type") == "tool_use" && item.TryGetProperty("input", out var input))
                        {
                            var sb = new StringBuilder();
                            Flatten(input, sb);
                            calls.Add(new ToolCall(Normalize(sb.ToString()), at));
                        }
                }
                else if (type == "response_item" && r.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    string text = Str(p, "type") switch
                    {
                        "custom_tool_call" => Str(p, "input"),
                        "function_call" => Str(p, "arguments"),
                        _ => "",
                    };
                    if (text.Length > 0) calls.Add(new ToolCall(Normalize(text), at));
                }
            }
            catch (JsonException) { }
        }
        return calls;
    }

    static void Flatten(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: sb.Append(e.GetString()).Append('\n'); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Flatten(p.Value, sb); break;
            case JsonValueKind.Array: foreach (var i in e.EnumerateArray()) Flatten(i, sb); break;
        }
    }

    /// <summary>The open PRs one normalized tool call touches, each once; the folder is set when the hit was a worktree path.</summary>
    public static List<(PrKey Pr, string? Folder)> HitsIn(string text, PrTargets t)
    {
        var hits = new Dictionary<PrKey, string?>();
        var covered = new List<(int Start, int End)>();
        foreach (var (needle, pr, folder) in t.Worktrees)
        {
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
            {
                int end = i + needle.Length;
                if (!AfterDrive(text, i) || (end < text.Length && IsPathChar(text[end]))) continue;
                if (covered.Any(c => i >= c.Start && end <= c.End)) continue;
                covered.Add((i, end));
                hits[pr] = folder;
            }
        }
        foreach (Match m in PullLink.Matches(text)) Add(m.Groups[1].Value + "/" + m.Groups[2].Value, m.Groups[3].Value);
        foreach (Match m in ApiPull.Matches(text)) Add(m.Groups[1].Value + "/" + m.Groups[2].Value, m.Groups[3].Value);
        foreach (Match m in GhPr.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int number)) continue;
            var flag = RepoFlag.Match(Segment(text, m.Index));
            if (flag.Success) Add(flag.Groups[1].Value, m.Groups[1].Value);
            else if (t.ByNumber.TryGetValue(number, out var keys) && keys.Length == 1 && t.Open.Contains(keys[0])) hits.TryAdd(keys[0], null);
        }
        return hits.Select(kv => (kv.Key, kv.Value)).ToList();

        void Add(string repo, string number)
        {
            if (int.TryParse(number, out int n) && PrKey.Of(repo, n) is var key && t.Open.Contains(key)) hits.TryAdd(key, null);
        }
    }

    static bool AfterDrive(string text, int i) =>
        (i >= 2 && text[i - 1] == ':' && char.IsLetter(text[i - 2]) && (i < 3 || !IsPathChar(text[i - 3])))
        || (i >= 2 && char.IsLetter(text[i - 1]) && text[i - 2] == '/' && (i < 3 || !IsPathChar(text[i - 3])));

    static bool IsPathChar(char c) => char.IsLetterOrDigit(c) || c is '.' or '_' or '-';

    static string Segment(string text, int start)
    {
        int end = text.Length;
        foreach (var sep in CommandSeparators)
        {
            int i = text.IndexOf(sep, start, StringComparison.Ordinal);
            if (i >= 0 && i < end) end = i;
        }
        return text[start..end];
    }

    sealed class Acc
    {
        public int Count, Own, Folders, Links;
        public DateTime Last;
        public string? Folder;
    }

    public static List<AgentLink> Attribute(IReadOnlyList<SessionSource> sessions, PrTargets targets, ToolCallCache cache, DateTime nowUtc)
    {
        var links = new List<AgentLink>();
        foreach (var s in sessions)
        {
            var acc = new Dictionary<PrKey, Acc>();
            Take(cache.LastCalls(s.TranscriptPath), own: true);
            if (s.Provider == SessionProvider.Claude)
                foreach (var agent in RecentSubagents(s, nowUtc)) Take(cache.LastCalls(agent), own: true);
            if (s.Provider == SessionProvider.Codex && s.Kind == "exec") TakeCwd(s.Cwd, s.UpdatedAt, own: true);
            foreach (var f in s.Folded)
            {
                bool own = f.Provider == SessionProvider.Claude;
                Take(cache.LastCalls(f.TranscriptPath), own);
                if (f.Provider == SessionProvider.Codex && f.Kind == "exec") TakeCwd(f.Cwd, f.UpdatedAt, own);
            }
            foreach (var (pr, a) in acc)
                if (a.Count >= Threshold) links.Add(new AgentLink(pr, s.SessionId, a.Count, a.Last, a.Own == 0, Evidence(a)));

            void Take(IReadOnlyList<ToolCall> calls, bool own)
            {
                foreach (var call in calls)
                    foreach (var (pr, folder) in HitsIn(call.Text, targets))
                        Note(pr, folder, 1, call.At, own);
            }

            void TakeCwd(string cwd, DateTime at, bool own)
            {
                if (cwd.Length == 0) return;
                foreach (var (pr, folder) in HitsIn(Normalize(cwd), targets)) Note(pr, folder, Threshold, at.ToUniversalTime(), own);
            }

            void Note(PrKey pr, string? folder, int weight, DateTime at, bool own)
            {
                if (!acc.TryGetValue(pr, out var a)) acc[pr] = a = new Acc();
                a.Count += weight;
                if (own) a.Own += weight;
                if (at > a.Last) a.Last = at;
                if (folder != null) { a.Folders += weight; a.Folder = folder; }
                else a.Links += weight;
            }
        }
        cache.EndPass();
        return links;
    }

    static string Evidence(Acc a)
    {
        string where = a.Folder != null && a.Folders >= a.Links ? $" in {a.Folder}" : " referencing this pull request";
        return $"{a.Count} tool calls{where}{(a.Own == 0 ? ", all by its Codex threads" : "")}";
    }

    static IEnumerable<string> RecentSubagents(SessionSource s, DateTime nowUtc)
    {
        if (s.TranscriptPath.Length == 0) return Array.Empty<string>();
        string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(s.TranscriptPath) ?? "", s.SessionId, "subagents");
        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return new DirectoryInfo(dir).EnumerateFiles("agent-*.jsonl")
                .Where(f => nowUtc - f.LastWriteTimeUtc <= SubagentWindow)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>Each transcript's last <see cref="PrAttribution.Window"/> tool calls, re-read only when its mtime or size changes.</summary>
internal sealed class ToolCallCache
{
    public const int StartBytes = 256 * 1024;
    public const int MaxBytes = 4 * 1024 * 1024;

    readonly Dictionary<string, (long Ticks, long Size, IReadOnlyList<ToolCall> Calls)> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToolCall> LastCalls(string path)
    {
        if (path.Length == 0) return Array.Empty<ToolCall>();
        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            if (!fi.Exists) return Array.Empty<ToolCall>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return Array.Empty<ToolCall>(); }
        _touched.Add(path);
        long ticks = fi.LastWriteTimeUtc.Ticks, size = fi.Length;
        if (_entries.TryGetValue(path, out var hit) && hit.Ticks == ticks && hit.Size == size) return hit.Calls;
        var calls = Read(path);
        _entries[path] = (ticks, size, calls);
        return calls;
    }

    /// <summary>Forget transcripts no session read this pass.</summary>
    public void EndPass()
    {
        foreach (var key in _entries.Keys.Where(k => !_touched.Contains(k)).ToList()) _entries.Remove(key);
        _touched.Clear();
    }

    internal static IReadOnlyList<ToolCall> Read(string path, int start = StartBytes, int max = MaxBytes)
    {
        try
        {
            for (int bytes = start; ; bytes *= 2)
            {
                var (lines, whole) = Tail(path, Math.Min(bytes, max));
                var calls = PrAttribution.ToolCalls(lines);
                if (calls.Count >= PrAttribution.Window || whole || bytes >= max)
                    return calls.Count > PrAttribution.Window ? calls.GetRange(calls.Count - PrAttribution.Window, PrAttribution.Window) : calls;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<ToolCall>(); }
    }

    static (string[] Lines, bool Whole) Tail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return (sr.ReadToEnd().Split('\n'), start == 0);
    }
}
