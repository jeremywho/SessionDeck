using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeSessionMonitor;

/// <summary>
/// Discovers live Codex CLI sessions from <c>~/.codex/</c>.
///
/// The shape of the problem is different from Claude's. Claude publishes a live registry
/// (<c>sessions/&lt;pid&gt;.json</c>) and we enrich each entry from its transcript. Codex publishes
/// no registry at all: there is only the append-only rollout log per thread
/// (<c>sessions/YYYY/MM/DD/rollout-&lt;ts&gt;-&lt;id&gt;.jsonl</c>). And because <c>codex resume</c>
/// appends to the ORIGINAL file, the filename timestamp, the day folder, and even the mtime all lie
/// about whether a thread is live — a thread started last week can be the one running right now.
///
/// So liveness is answered the only way that can't lie: a running Codex holds its rollout file open,
/// and <see cref="FileHolders"/> turns that into a PID. That call costs ~50ms, so it runs on a
/// background probe thread (<see cref="Probe"/>) with a budget, while <see cref="Scan"/> — called on
/// the UI tick — only reads the resulting map and parses tails.
///
/// Everything here reads undocumented Codex files; all the parsing lives in this one file, so a
/// schema change on their side is a one-file fix (same contract as SessionScanner).
/// </summary>
internal static class CodexScanner
{
    static readonly string Home = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    static readonly string SessionsDir = Path.Combine(Home, "sessions");
    static readonly string IndexFile = Path.Combine(Home, "session_index.jsonl");

    /// <summary>The rollout tree — watched for change-driven refresh.</summary>
    public static string SessionsDirectory => SessionsDir;

    /// <summary>How far back to look for rollout files that could still be open.</summary>
    const int WindowDays = 30;

    /// <summary>Restart Manager calls allowed per probe pass (first pass gets more — see Probe).</summary>
    const int ProbeBudget = 40;
    const int FirstProbeBudget = 150;

    const double SubagentActiveSeconds = 30;

    // ---------------------------------------------------------------- header facts (immutable)

    /// <summary>
    /// First-line <c>session_meta</c> facts. A rollout's header never changes, so this is parsed once
    /// per file, ever.
    /// <para>Watch the two id fields — they are not what the names suggest. <c>id</c> is THIS thread's
    /// own id; <c>session_id</c> is the id of the ROOT thread, so on a subagent's rollout they differ
    /// and <c>session_id</c> points at the session you actually see in the list. Reading <c>session_id</c>
    /// as "this thread" makes every subagent masquerade as its parent.</para>
    /// </summary>
    sealed class Head
    {
        public string Id = "";           // this thread
        public string RootId = "";       // the top-level session this thread belongs to
        public string Cwd = "";
        public string Version = "";
        public string Originator = "";   // "codex-tui" | "codex_exec" | …
        public string AgentName = "";    // subagent nickname ("Laplace")
        public bool IsSubagent;
    }

    // Written by the probe thread, read by the UI scan — hence concurrent rather than plain.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Head> _heads =
        new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- live ownership (probe thread)

    static readonly object _gate = new();
    static Dictionary<string, int> _owner = new(StringComparer.OrdinalIgnoreCase);      // rollout -> codex pid
    static Dictionary<string, string> _subParent = new(StringComparer.OrdinalIgnoreCase); // subagent rollout -> ROOT thread id
    static readonly Dictionary<string, long> _probed = new(StringComparer.OrdinalIgnoreCase); // rollout -> size when last probed
    static readonly Dictionary<string, int> _misses = new(StringComparer.OrdinalIgnoreCase);  // consecutive "nobody holds this" results
    static bool _firstPass = true;
    static DateTime _lastVerify = DateTime.MinValue;

    /// <summary>
    /// When each rollout was last seen to GROW. Windows does not reliably refresh the mtime of a file
    /// a process is holding open — a <c>codex exec</c> rollout was measured sitting at a 14-minute-old
    /// timestamp while gaining 7KB in 12 seconds — so "is this still being written?" is answered by
    /// size, which always moves. (The TUI's own rollout does update its mtime; only trusting mtime for
    /// both is what silently pinned exec sessions at "idle".)
    /// </summary>
    static readonly Dictionary<string, (long Size, DateTime At)> _growth = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, DateTime> _grewAt = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- tail-parse cache

    sealed class Detail
    {
        public string Model = "", Effort = "", LastTool = "", Status = "", Title = "";
        public long Ctx, Window;
        public DateTime StatusAt;    // when the last status-changing record was written
        public DateTime LastAt;      // timestamp of the last record of any kind
        public long Mtime, Size;
        public bool Full;   // a whole-file pass has been done at least once
    }

    static readonly Dictionary<string, Detail> _detail = new();

    // ---------------------------------------------------------------- names

    static Dictionary<string, string> _names = new();
    static long _namesMtime = -1;

    // ================================================================ scan (UI thread)

    /// <summary>
    /// Live Codex sessions, from the ownership map the probe thread maintains. Cheap: a liveness check
    /// per PID plus a tail read per session, mirroring <see cref="SessionScanner"/>.
    /// </summary>
    public static List<SessionInfo> Scan()
    {
        var list = new List<SessionInfo>();

        Dictionary<string, int> owners;
        Dictionary<string, string> subs;
        Dictionary<string, DateTime> grewAt;
        lock (_gate)
        {
            if (_owner.Count == 0) { _detail.Clear(); return list; }
            owners = new Dictionary<string, int>(_owner, StringComparer.OrdinalIgnoreCase);
            subs = new Dictionary<string, string>(_subParent, StringComparer.OrdinalIgnoreCase);
            grewAt = new Dictionary<string, DateTime>(_grewAt, StringComparer.OrdinalIgnoreCase);
        }

        var names = Names();
        var dead = new List<string>();

        foreach (var kv in owners)
        {
            if (!IsAlive(kv.Value)) { dead.Add(kv.Key); continue; }
            if (!_heads.TryGetValue(kv.Key, out var head) || head.IsSubagent) continue;

            var s = new SessionInfo
            {
                Provider = SessionProvider.Codex,
                Pid = kv.Value,
                SessionId = head.Id,
                Cwd = head.Cwd,
                Version = head.Version,
                Kind = head.Originator.Contains("exec", StringComparison.OrdinalIgnoreCase) ? "exec" : "tui",
                TranscriptPath = kv.Key,
                Name = names.TryGetValue(head.Id, out var n) ? n : "",
            };

            Enrich(s);
            CountSubagents(s, subs, grewAt);
            list.Add(s);
        }

        // A dead PID is unambiguous (unlike a Restart Manager miss), so no second strike needed.
        if (dead.Count > 0)
            lock (_gate) foreach (var p in dead) Forget(p);

        // forget tail-parse detail for sessions that are no longer live
        var liveIds = new HashSet<string>(list.ConvertAll(x => x.SessionId));
        foreach (var key in new List<string>(_detail.Keys))
            if (!liveIds.Contains(key)) _detail.Remove(key);

        return list;
    }

    /// <summary>PID is alive AND is actually a codex process (guards against PID reuse).</summary>
    static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("codex", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ================================================================ probe (background thread)

    /// <summary>
    /// Refresh the rollout-&gt;PID map. Runs off the UI thread: each <see cref="FileHolders.OwnerPid"/>
    /// call is ~50ms, so passes are budgeted and probe newest-first. Files already owned are re-verified
    /// on a slower cadence (a Codex TUI that switches threads closes the old file while the process lives on).
    /// </summary>
    public static void Probe()
    {
        bool anyCodex;
        try { anyCodex = Process.GetProcessesByName("codex").Length > 0; }
        catch { anyCodex = false; }

        if (!anyCodex)
        {
            lock (_gate) { _owner.Clear(); _subParent.Clear(); _probed.Clear(); _misses.Clear(); }
            return;
        }

        var files = RecentRollouts();
        if (files.Count == 0) return;

        foreach (var f in files) ReadHead(f.Path);   // cached; only new files cost anything
        TrackGrowth(files);

        Dictionary<string, int> owned;
        lock (_gate) owned = new Dictionary<string, int>(_owner, StringComparer.OrdinalIgnoreCase);

        int budget = _firstPass ? FirstProbeBudget : ProbeBudget;

        // Re-verify what we already hold, every ~30s. Cheap (one call per live session) and it's the
        // only thing that notices a thread being closed by a process that stays alive.
        bool verify = (DateTime.UtcNow - _lastVerify).TotalSeconds > 30;
        if (verify)
        {
            _lastVerify = DateTime.UtcNow;
            foreach (var path in owned.Keys)
            {
                int pid = FileHolders.OwnerPid(path, "codex");
                budget--;
                lock (_gate)
                {
                    if (pid != 0) { _owner[path] = pid; _misses.Remove(path); continue; }

                    // Two strikes, not one. A single "nobody holds this" is not worth a session
                    // blinking out of the list — and dropping a QUIET session is the expensive kind of
                    // mistake, because rediscovery is driven by the file growing.
                    _misses[path] = _misses.TryGetValue(path, out var m) ? m + 1 : 1;
                    if (_misses[path] >= 2) Forget(path);
                }
            }
        }

        // Then probe candidates newest-first: anything never probed, or written since we last looked.
        foreach (var f in files)
        {
            if (budget <= 0) break;
            if (owned.ContainsKey(f.Path)) continue;
            lock (_gate)
            {
                if (_probed.TryGetValue(f.Path, out var seen) && seen == f.Size) continue;
                _probed[f.Path] = f.Size;
            }
            budget--;

            int pid = FileHolders.OwnerPid(f.Path, "codex");
            if (pid == 0) continue;
            lock (_gate) { _owner[f.Path] = pid; _misses.Remove(f.Path); }
        }

        _firstPass = false;
        RebuildSubagentMap(files);
    }

    /// <summary>Drop a rollout from the live map. Clearing the probe record too is the point: it's what
    /// lets the file be reconsidered later even if it never changes size again.</summary>
    static void Forget(string path)
    {
        _owner.Remove(path);
        _probed.Remove(path);
        _misses.Remove(path);
    }

    /// <summary>Rollout files from the last <see cref="WindowDays"/> day-folders, newest write first.</summary>
    static List<(string Path, long Mtime, long Size)> RecentRollouts()
    {
        var result = new List<(string, long, long)>();
        if (!Directory.Exists(SessionsDir)) return result;

        var today = DateTime.Now.Date;
        for (int i = 0; i < WindowDays; i++)
        {
            var d = today.AddDays(-i);
            string dir = Path.Combine(SessionsDir, d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd"));
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "rollout-*.jsonl"))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        result.Add((f, fi.LastWriteTimeUtc.Ticks, fi.Length));
                    }
                    catch { }
                }
            }
            catch { }
        }

        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return result;
    }

    /// <summary>
    /// Note which rollouts have grown since the previous pass. A file we're seeing for the first time
    /// is stamped with its mtime, not "now" — otherwise every rollout in the window would look like it
    /// was written this instant on the first pass, and every session would show phantom live subagents.
    /// </summary>
    static void TrackGrowth(List<(string Path, long Mtime, long Size)> files)
    {
        var now = DateTime.UtcNow;
        var snapshot = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            if (_growth.TryGetValue(f.Path, out var g))
            {
                if (f.Size != g.Size) g = (f.Size, now);
            }
            else
            {
                g = (f.Size, new DateTime(f.Mtime, DateTimeKind.Utc));
            }
            _growth[f.Path] = g;
            snapshot[f.Path] = g.At;
        }

        lock (_gate) _grewAt = snapshot;
    }

    /// <summary>
    /// Map every known subagent rollout to the session it belongs to. Codex nests — a subagent can
    /// spawn its own — but the header's <c>session_id</c> is already the ROOT thread for every depth,
    /// so the whole tree rolls up to the row you actually see without walking any parent chain.
    /// </summary>
    static void RebuildSubagentMap(List<(string Path, long Mtime, long Size)> files)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (!_heads.TryGetValue(f.Path, out var h) || !h.IsSubagent) continue;
            if (h.RootId.Length > 0) map[f.Path] = h.RootId;
        }

        lock (_gate) _subParent = map;
    }

    // ================================================================ parsing

    static Head ReadHead(string path)
    {
        if (_heads.TryGetValue(path, out var cached)) return cached;

        var head = new Head();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line = sr.ReadLine();
            if (line != null)
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("payload", out var p))
                {
                    head.Id = Str(p, "id");
                    head.RootId = Str(p, "session_id");
                    if (head.RootId.Length == 0) head.RootId = head.Id;
                    if (head.Id.Length == 0) head.Id = head.RootId;
                    head.Cwd = Clean(Str(p, "cwd"));
                    head.Version = Str(p, "cli_version");
                    head.Originator = Str(p, "originator");

                    // thread_source is the plain string "user" or "subagent"; the spawn detail
                    // (parent, depth, nickname) is over in `source`.
                    head.IsSubagent = Str(p, "thread_source") == "subagent";
                    if (p.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object &&
                        src.TryGetProperty("subagent", out var sa) &&
                        sa.TryGetProperty("thread_spawn", out var sp))
                    {
                        head.IsSubagent = true;
                        head.AgentName = Str(sp, "agent_nickname");
                    }
                }
            }
        }
        catch { }

        _heads[path] = head;
        return head;
    }

    /// <summary>Codex writes extended-length paths (<c>\\?\C:\…</c>); strip the prefix for display.</summary>
    static string Clean(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;

    /// <summary>
    /// Pull model / effort / context / status / last tool from the rollout.
    ///
    /// First pass over a session reads the whole file: <c>turn_context</c> (which carries the model)
    /// is written once per turn, so on a long thread the last one can sit megabytes back — a tail-only
    /// read would show no model at all. After that, an unchanged file costs a stat and a changed one
    /// costs a tail read, same as the Claude path.
    /// </summary>
    static void Enrich(SessionInfo s)
    {
        long mtime = 0, size = 0;
        try
        {
            var fi = new FileInfo(s.TranscriptPath);
            if (fi.Exists) { mtime = fi.LastWriteTimeUtc.Ticks; size = fi.Length; }
        }
        catch { }

        _detail.TryGetValue(s.SessionId, out var prev);

        if (prev != null && prev.Full && prev.Mtime == mtime && prev.Size == size)
        {
            Apply(s, prev);
            return;
        }

        var d = new Detail
        {
            Model = prev?.Model ?? "", Effort = prev?.Effort ?? "", LastTool = prev?.LastTool ?? "",
            Status = prev?.Status ?? "", Title = prev?.Title ?? "", Ctx = prev?.Ctx ?? 0,
            Window = prev?.Window ?? 0, StatusAt = prev?.StatusAt ?? DateTime.MinValue,
            LastAt = prev?.LastAt ?? DateTime.MinValue,
            Mtime = mtime, Size = size, Full = true,
        };

        try
        {
            bool full = prev == null || !prev.Full;
            foreach (var line in full ? ReadAll(s.TranscriptPath) : ReadTail(s.TranscriptPath, 512 * 1024))
                ParseLine(line, d);
        }
        catch { d.Full = prev?.Full ?? false; }

        _detail[s.SessionId] = d;
        Apply(s, d);
    }

    static void Apply(SessionInfo s, Detail d)
    {
        s.Model = d.Model;
        s.Effort = d.Effort;
        s.LastTool = d.LastTool;
        s.Status = d.Status;
        s.ContextTokens = d.Ctx;
        s.ContextWindow = d.Window;
        s.Title = d.Title;

        // The rollout stamps every record, so the last one is the true "last activity" — and unlike
        // the file's mtime it is correct for exec sessions too.
        s.UpdatedAt = d.LastAt != DateTime.MinValue
            ? d.LastAt
            : d.Mtime > 0 ? new DateTime(d.Mtime, DateTimeKind.Utc).ToLocalTime() : DateTime.MinValue;
        s.StatusUpdatedAt = d.StatusAt != DateTime.MinValue ? d.StatusAt : s.UpdatedAt;
    }

    /// <summary>
    /// Forward scan, last-write-wins — the final value of each field is the most recent one in the file.
    /// Cheap string guards first: rollout lines run to ~68KB (encrypted reasoning blobs), so parsing
    /// every one as JSON would be the expensive part.
    /// </summary>
    static void ParseLine(string line, Detail d)
    {
        if (line.Length < 16) return;

        var stamp = LineStamp(line);
        if (stamp != DateTime.MinValue) d.LastAt = stamp;

        if (line.Contains("\"type\":\"task_started\"", StringComparison.Ordinal))
        {
            d.Status = "busy";
            if (stamp != DateTime.MinValue) d.StatusAt = stamp;
            return;
        }
        if (line.Contains("\"type\":\"task_complete\"", StringComparison.Ordinal) ||
            line.Contains("\"type\":\"turn_aborted\"", StringComparison.Ordinal))
        {
            d.Status = "idle";
            if (stamp != DateTime.MinValue) d.StatusAt = stamp;
            return;
        }

        if (line.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal))
        {
            Try(line, p =>
            {
                var m = Str(p, "model"); if (m.Length > 0) d.Model = m;
                var e = Str(p, "effort"); if (e.Length > 0) d.Effort = e;
            });
            return;
        }

        // Written when you change model/effort mid-thread — the only record of the switch until the
        // next turn starts, so it has to win over the turn_context that came before it.
        if (line.Contains("\"type\":\"thread_settings_applied\"", StringComparison.Ordinal))
        {
            Try(line, p =>
            {
                if (!p.TryGetProperty("thread_settings", out var t)) return;
                var m = Str(t, "model"); if (m.Length > 0) d.Model = m;
                var e = Str(t, "reasoning_effort"); if (e.Length > 0) d.Effort = e;
            });
            return;
        }

        if (line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal))
        {
            Try(line, p =>
            {
                if (!p.TryGetProperty("info", out var info)) return;
                // last_token_usage is THIS turn's prompt — i.e. what's actually in the window.
                // total_token_usage is cumulative for the thread (tens of millions) and is not context.
                if (info.TryGetProperty("last_token_usage", out var last))
                    d.Ctx = Num(last, "input_tokens");
                long w = Num(info, "model_context_window");
                if (w > 0) d.Window = w;
            });
            return;
        }

        if (line.Contains("\"type\":\"custom_tool_call\"", StringComparison.Ordinal) ||
            line.Contains("\"type\":\"function_call\"", StringComparison.Ordinal))
        {
            Try(line, p =>
            {
                var t = Str(p, "type");
                if (t != "custom_tool_call" && t != "function_call") return;   // not the _output variants
                var n = Str(p, "name");
                if (n.Length > 0) d.LastTool = n;
            });
            return;
        }

        if (d.Title.Length == 0 && line.Contains("\"type\":\"user_message\"", StringComparison.Ordinal))
            Try(line, p =>
            {
                if (d.Title.Length > 0) return;
                var m = Str(p, "message").Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (m.Length > 90) m = m.Substring(0, 90).TrimEnd() + "…";
                d.Title = m;
            });
    }

    static void Try(string line, Action<JsonElement> act)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("payload", out var p)) act(p);
        }
        catch { }
    }

    const string StampPrefix = "{\"timestamp\":\"";

    /// <summary>
    /// The record's timestamp, read straight off the front of the line. Every rollout record starts
    /// with it, so this avoids parsing the JSON of lines we'd otherwise skip — and those lines run to
    /// ~68KB apiece.
    /// </summary>
    static DateTime LineStamp(string line)
    {
        if (!line.StartsWith(StampPrefix, StringComparison.Ordinal)) return DateTime.MinValue;
        int end = line.IndexOf('"', StampPrefix.Length);
        if (end <= StampPrefix.Length) return DateTime.MinValue;
        return DateTime.TryParse(line.AsSpan(StampPrefix.Length, end - StampPrefix.Length), null,
                   System.Globalization.DateTimeStyles.AdjustToUniversal |
                   System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToLocalTime()
            : DateTime.MinValue;
    }

    // ================================================================ subagents

    /// <summary>
    /// Count this session's subagent threads — total spawned, plus how many grew in the last ~30s
    /// (still streaming = working now). Each Codex subagent is a rollout of its own. "Grew" rather than
    /// "was modified" is load-bearing here: see <see cref="_growth"/>.
    /// </summary>
    static void CountSubagents(SessionInfo s, Dictionary<string, string> subs, Dictionary<string, DateTime> grewAt)
    {
        var now = DateTime.UtcNow;
        int total = 0, active = 0;
        foreach (var kv in subs)
        {
            if (!string.Equals(kv.Value, s.SessionId, StringComparison.OrdinalIgnoreCase)) continue;
            total++;
            if (grewAt.TryGetValue(kv.Key, out var at) && (now - at).TotalSeconds < SubagentActiveSeconds) active++;
        }
        s.SubagentsTotal = total;
        s.SubagentsActive = active;
    }

    // ================================================================ names

    /// <summary>
    /// Thread names from <c>session_index.jsonl</c> — Codex's equivalent of the Claude session name,
    /// set by <c>/name</c> or by the companion. Re-read only when the file changes.
    /// </summary>
    static Dictionary<string, string> Names()
    {
        try
        {
            var fi = new FileInfo(IndexFile);
            if (!fi.Exists) return _names;
            long mtime = fi.LastWriteTimeUtc.Ticks;
            if (mtime == _namesMtime) return _names;

            var map = new Dictionary<string, string>();
            using (var fs = new FileStream(IndexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length < 8) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        string id = Str(r, "id"), name = Str(r, "thread_name");
                        if (id.Length > 0 && name.Length > 0) map[id] = name;   // last line wins (renames)
                    }
                    catch { }
                }
            }
            _namesMtime = mtime;
            _names = map;
        }
        catch { }
        return _names;
    }

    // ================================================================ io / json helpers

    static IEnumerable<string> ReadAll(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) != null) yield return line;
    }

    static string[] ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        // The first line is likely a fragment from the seek; it just fails to parse and is skipped.
        return sr.ReadToEnd().Split('\n');
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
