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

    /// <summary>Day-folders stat'd on EVERY probe pass. Two, not one: a rollout lives in the
    /// day-folder its session STARTED in, so a live session that crossed midnight is in yesterday's.</summary>
    const int FastWindowDays = 2;

    /// <summary>
    /// How often the FULL 30-day catalog is re-enumerated — discovery of resumed old threads, plus
    /// cache pruning. Every 3s pass used to do this, stat'ing every historical rollout each time
    /// (~800 files / 4GB observed on a real machine); between full scans a pass now stats only the
    /// fast window (where new rollouts appear) and the files already known to be live.
    /// </summary>
    static readonly TimeSpan CatalogRescan = TimeSpan.FromSeconds(60);
    static DateTime _catalogAt = DateTime.MinValue;   // probe thread only

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
    static readonly Dictionary<string, string> _subParent = new(StringComparer.OrdinalIgnoreCase); // subagent rollout -> ROOT thread id
    static readonly Dictionary<string, long> _probed = new(StringComparer.OrdinalIgnoreCase); // rollout -> size when last probed
    static readonly Dictionary<string, int> _misses = new(StringComparer.OrdinalIgnoreCase);  // consecutive "nobody holds this" results
    static readonly Dictionary<string, DateTime> _verified = new(StringComparer.OrdinalIgnoreCase); // last owner recheck
    static bool _firstPass = true;

    static readonly TimeSpan VerifyInterval = TimeSpan.FromSeconds(30);
    const int VerifyPerPass = 8;

    /// <summary>
    /// When each rollout was last seen to GROW. Windows does not reliably refresh the mtime of a file
    /// a process is holding open — a <c>codex exec</c> rollout was measured sitting at a 14-minute-old
    /// timestamp while gaining 7KB in 12 seconds — so "is this still being written?" is answered by
    /// size, which always moves. (The TUI's own rollout does update its mtime; only trusting mtime for
    /// both is what silently pinned exec sessions at "idle".)
    /// </summary>
    static readonly Dictionary<string, (long Size, DateTime At)> _growth = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, DateTime> _grewAt = new(StringComparer.OrdinalIgnoreCase);

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
                Kind = KindFor(head.Originator),
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

    /// <summary>
    /// Is this specific thread running right now? Answers the restore question — "was this saved
    /// session still live?" — without waiting for a probe pass.
    ///
    /// The orphan check runs at startup, before the background probe has mapped anything, so consulting
    /// the ownership map there would report every live Codex session as dead and offer to restore
    /// sessions that are on screen. This costs one Restart Manager call for one known id instead of a
    /// full pass, which is what makes it usable on the startup path.
    /// </summary>
    public static bool IsThreadLive(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        // Already known live? Then skip the call entirely.
        lock (_gate)
            foreach (var kv in _owner)
                if (_heads.TryGetValue(kv.Key, out var h) && h.Id == sessionId) return IsAlive(kv.Value);

        string path = RolloutFor(sessionId);
        return path.Length > 0 && FileHolders.OwnerPid(path, "codex") != 0;
    }

    /// <summary>
    /// What is driving this thread, from the header's <c>originator</c>. "tui" = a terminal a human
    /// sits in front of; "exec" = a headless one-shot; "companion" = a thread another app drives
    /// through the codex app server — the Claude Code codex plugin stamps <c>"Claude Code"</c> here
    /// for its "Codex Companion Task" second-opinion threads, and IDE extensions land in the same
    /// bucket. A companion is a real live thread (it holds its rollout open like any other), but it
    /// has no terminal window to focus and is nothing a restore should reopen, so the distinction is
    /// what the UI keys every terminal-shaped affordance on. An empty/unknown originator stays "tui":
    /// misreading a real terminal as a companion would silently break focus and restore for it, which
    /// is the expensive direction to be wrong in.
    /// </summary>
    internal static string KindFor(string originator) =>
        originator.Contains("exec", StringComparison.OrdinalIgnoreCase) ? "exec"
        : originator.Length == 0 || originator.StartsWith("codex", StringComparison.OrdinalIgnoreCase) ? "tui"
        : "companion";

    /// <summary>The rollout file for a thread id — its filename ends with the id.</summary>
    static string RolloutFor(string sessionId)
    {
        try
        {
            foreach (var f in RecentRollouts(WindowDays))
                if (Path.GetFileNameWithoutExtension(f.Path)
                        .EndsWith(sessionId, StringComparison.OrdinalIgnoreCase)) return f.Path;
        }
        catch { }
        return "";
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
        // Before the liveness check, not after: the plan meter should show even with no Codex running.
        if (!_seeded) { _seeded = true; SeedPlanUsage(); }

        bool anyCodex = false;
        try
        {
            var procs = Process.GetProcessesByName("codex");
            anyCodex = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
        }
        catch { }

        if (!anyCodex)
        {
            lock (_gate)
            {
                _owner.Clear(); _subParent.Clear(); _probed.Clear(); _misses.Clear(); _verified.Clear();
            }
            return;
        }

        Dictionary<string, int> owned;
        lock (_gate) owned = new Dictionary<string, int>(_owner, StringComparer.OrdinalIgnoreCase);

        // Full catalog on the slow cadence; the fast window + known-live files otherwise.
        bool fullScan = DateTime.UtcNow - _catalogAt > CatalogRescan;
        if (fullScan) _catalogAt = DateTime.UtcNow;
        var files = fullScan ? RecentRollouts(WindowDays) : FastRollouts(owned);
        if (files.Count == 0) return;

        foreach (var f in files) ReadHead(f.Path);   // cached; only new files cost anything
        TrackGrowth(files);

        int budget = _firstPass ? FirstProbeBudget : ProbeBudget;

        // Reserve a small part of the pass for rotating known-owner verification. The old code
        // verified every known rollout in one burst before checking the budget, so a large live set
        // could issue an unbounded run of Restart Manager calls and starve new-session discovery.
        var now = DateTime.UtcNow;
        List<string> due;
        lock (_gate)
            due = DueForVerification(owned.Keys, _verified, now);
        int verifyReserved = Math.Min(budget, due.Count);

        // Then probe candidates newest-first: anything never probed, or written since we last looked.
        foreach (var f in files)
        {
            if (budget <= verifyReserved) break;
            if (owned.ContainsKey(f.Path)) continue;
            lock (_gate)
            {
                if (_probed.TryGetValue(f.Path, out var seen) && seen == f.Size) continue;
                _probed[f.Path] = f.Size;
            }
            budget--;

            int pid = FileHolders.OwnerPid(f.Path, "codex");
            if (pid == 0) continue;
            lock (_gate)
            {
                _owner[f.Path] = pid; _misses.Remove(f.Path); _verified[f.Path] = now;
            }
        }

        // Rechecking ownership is what notices a closed rollout when its codex process remains alive.
        // At most eight are checked per pass, rotating naturally because successful and failed checks
        // are timestamped. A miss still needs two checks 30s apart before the session is forgotten.
        foreach (var path in due)
        {
            if (budget <= 0) break;
            budget--;
            int pid = FileHolders.OwnerPid(path, "codex");
            lock (_gate)
            {
                _verified[path] = now;
                if (pid != 0) { _owner[path] = pid; _misses.Remove(path); continue; }

                _misses[path] = _misses.TryGetValue(path, out var m) ? m + 1 : 1;
                if (_misses[path] >= 2) Forget(path);
            }
        }

        _firstPass = false;
        RebuildSubagentMap(files, fullScan);
        if (fullScan) Prune(files, owned);
    }

    internal static List<string> DueForVerification(IEnumerable<string> owned,
        IReadOnlyDictionary<string, DateTime> verified, DateTime nowUtc) =>
        owned.Where(path => !verified.TryGetValue(path, out var at) || nowUtc - at >= VerifyInterval)
             .Take(VerifyPerPass).ToList();

    /// <summary>The fast window's rollouts plus stats for owned files outside it — what a between-
    /// full-scans pass looks at instead of the whole catalog.</summary>
    static List<(string Path, long Mtime, long Size)> FastRollouts(Dictionary<string, int> owned)
    {
        var list = RecentRollouts(FastWindowDays);
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in list) have.Add(f.Path);
        foreach (var path in owned.Keys)
        {
            if (have.Contains(path)) continue;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) list.Add((path, fi.LastWriteTimeUtc.Ticks, fi.Length));
            }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// Drop cache entries for rollouts that have left the catalog window (unless still owned).
    /// Runs on the full-scan cadence. Without it, <see cref="_heads"/> and the growth maps grew for
    /// the life of the process — the only unbounded memory in the app.
    /// </summary>
    static void Prune(List<(string Path, long Mtime, long Size)> catalog, Dictionary<string, int> owned)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in catalog) keep.Add(f.Path);
        foreach (var p in owned.Keys) keep.Add(p);

        var dead = new List<string>();
        foreach (var k in _heads.Keys) if (!keep.Contains(k)) dead.Add(k);
        foreach (var k in dead) _heads.TryRemove(k, out _);

        dead.Clear();
        foreach (var k in _growth.Keys) if (!keep.Contains(k)) dead.Add(k);
        foreach (var k in dead) _growth.Remove(k);

        lock (_gate)
        {
            PruneDict(_grewAt, keep);
            PruneDict(_probed, keep);
            PruneDict(_misses, keep);
            PruneDict(_verified, keep);
        }
    }

    static void PruneDict<T>(Dictionary<string, T> map, HashSet<string> keep)
    {
        var dead = new List<string>();
        foreach (var k in map.Keys) if (!keep.Contains(k)) dead.Add(k);
        foreach (var k in dead) map.Remove(k);
    }

    /// <summary>Drop a rollout from the live map. Clearing the probe record too is the point: it's what
    /// lets the file be reconsidered later even if it never changes size again.</summary>
    static void Forget(string path)
    {
        _owner.Remove(path);
        _probed.Remove(path);
        _misses.Remove(path);
        _verified.Remove(path);
    }

    /// <summary>Rollout files from the last <paramref name="days"/> day-folders, newest write first.</summary>
    static List<(string Path, long Mtime, long Size)> RecentRollouts(int days)
    {
        var result = new List<(string, long, long)>();
        if (!Directory.Exists(SessionsDir)) return result;

        var today = DateTime.Now.Date;
        for (int i = 0; i < days; i++)
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
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

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
            seen[f.Path] = g.At;
        }

        // Merged, not replaced: a fast pass only carries the fast-window files, and replacing the
        // map would blank the growth stamps — and so the subagent-activity window — for the rest.
        // Entries for aged-out files are dropped by Prune on the full-scan cadence.
        lock (_gate) foreach (var kv in seen) _grewAt[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Map every known subagent rollout to the session it belongs to. Codex nests — a subagent can
    /// spawn its own — but the header's <c>session_id</c> is already the ROOT thread for every depth,
    /// so the whole tree rolls up to the row you actually see without walking any parent chain.
    /// </summary>
    static void RebuildSubagentMap(List<(string Path, long Mtime, long Size)> files, bool full)
    {
        lock (_gate)
        {
            // A fast pass merges (it only saw the fast window); the periodic full pass rebuilds,
            // which is also what drops entries for deleted or aged-out rollouts.
            if (full) _subParent.Clear();
            foreach (var f in files)
            {
                if (!_heads.TryGetValue(f.Path, out var h) || !h.IsSubagent) continue;
                if (h.RootId.Length > 0) _subParent[f.Path] = h.RootId;
            }
        }
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
                if (p.TryGetProperty("info", out var info))
                {
                    // last_token_usage is THIS turn's prompt — i.e. what's actually in the window.
                    // total_token_usage is cumulative for the thread (tens of millions) and is not context.
                    if (info.TryGetProperty("last_token_usage", out var last))
                        d.Ctx = Num(last, "input_tokens");
                    long w = Num(info, "model_context_window");
                    if (w > 0) d.Window = w;
                }

                // The same record carries the account's plan limits — which is why the Codex meter
                // needs no network call, unlike the Claude one.
                if (p.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
                    NotePlanUsage(rl, stamp);
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

    // ================================================================ plan usage

    /// <summary>Codex plan limits, newest first-hand reading wins. Written from both threads.</summary>
    static List<UsageMeter> _planMeters = new();
    static DateTime _planAt = DateTime.MinValue;
    static string _planType = "";
    static bool _seeded;

    /// <summary>
    /// The account's Codex plan usage, or an empty list if none has been seen. Free — Codex stamps
    /// <c>rate_limits</c> onto every <c>token_count</c> record, so unlike the Claude meters this needs
    /// no API call and no credentials.
    /// </summary>
    public static List<UsageMeter> PlanMeters { get { lock (_gate) return new List<UsageMeter>(_planMeters); } }

    /// <summary>When the reading we're showing was written by Codex (not when we read it).</summary>
    public static DateTime PlanUpdatedAt { get { lock (_gate) return _planAt; } }

    public static string PlanType { get { lock (_gate) return _planType; } }

    /// <summary>
    /// Record a <c>rate_limits</c> block. Keeps the newest by the record's own timestamp: limits are
    /// per-account, so every live session reports the same thing and the freshest reading wins.
    /// </summary>
    static void NotePlanUsage(JsonElement rl, DateTime at)
    {
        lock (_gate)
        {
            if (at != DateTime.MinValue && _planAt != DateTime.MinValue && at < _planAt) return;

            var meters = BuildPlanMeters(rl, at);
            if (meters.Count == 0) return;

            _planMeters = meters;
            _planAt = at;
            _planType = Str(rl, "plan_type");
        }
    }

    /// <summary>Test seam: build the meters from a <c>rate_limits</c> object's JSON.</summary>
    public static List<UsageMeter> ParsePlanUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? BuildPlanMeters(doc.RootElement, DateTime.MinValue)
                : new List<UsageMeter>();
        }
        catch (JsonException) { return new List<UsageMeter>(); }
    }

    static List<UsageMeter> BuildPlanMeters(JsonElement rl, DateTime at)
    {
        var meters = new List<UsageMeter>();
        AddWindow(meters, rl, "primary");
        AddWindow(meters, rl, "secondary");
        if (meters.Count == 0) return meters;

        // Two windows need telling apart; a lone one doesn't, and "Codex" is the shorter label —
        // which matters, the usage bar is already full at the 460px minimum width.
        if (meters.Count > 1)
            foreach (var m in meters) m.Label = $"Codex {m.Label}";
        else
            meters[0].Label = "Codex";

        string plan = Str(rl, "plan_type");
        foreach (var m in meters)
        {
            m.Note = plan.Length > 0 ? $"Codex {PlanName(plan)} plan" : "Codex";
            m.ReadAt = at;
        }
        return meters;
    }

    static void AddWindow(List<UsageMeter> into, JsonElement rl, string name)
    {
        if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return;
        if (!w.TryGetProperty("used_percent", out var up) || up.ValueKind != JsonValueKind.Number) return;

        int pct = (int)Math.Round(up.GetDouble(), MidpointRounding.AwayFromZero);
        var meter = new UsageMeter
        {
            Label = WindowLabel(Num(w, "window_minutes")),
            Percent = Math.Clamp(pct, 0, 100),
            // Codex reports no severity of its own — unlike the Claude limits, where inventing a
            // threshold would be overriding the server. Reuse the app's existing context thresholds
            // so one number doesn't mean two different colours in the same window.
            Severity = pct > 85 ? "critical" : pct > 70 ? "warning" : "normal",
        };
        long resets = Num(w, "resets_at");
        if (resets > 0) meter.ResetsAt = DateTimeOffset.FromUnixTimeSeconds(resets).LocalDateTime;
        into.Add(meter);
    }

    /// <summary>Tidy the plan id for display. An unknown value renders as-is rather than vanishing —
    /// the set is theirs to grow, not ours to enumerate.</summary>
    static string PlanName(string plan) => plan.ToLowerInvariant() switch
    {
        "prolite" => "Pro Lite",
        "pro" => "Pro",
        "plus" => "Plus",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        _ => plan,
    };

    static string WindowLabel(long minutes) => minutes switch
    {
        <= 0 => "",
        10080 => "wk",
        300 => "5h",
        < 1440 => $"{minutes / 60}h",
        _ => $"{minutes / 1440}d",
    };

    /// <summary>
    /// Read the plan limits off the most recent rollouts, once, at startup. Without this the meter
    /// would stay blank until you next ran Codex — but "how much budget do I have left" is a question
    /// you ask *before* starting, so it's worth one tail read of a file we're not otherwise touching.
    /// </summary>
    static void SeedPlanUsage()
    {
        try
        {
            var files = RecentRollouts(WindowDays);
            for (int i = 0; i < files.Count && i < 3; i++)
            {
                var d = new Detail();
                foreach (var line in ReadTail(files[i].Path, 256 * 1024)) ParseLine(line, d);
                lock (_gate) if (_planMeters.Count > 0) return;
            }
        }
        catch { }
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
