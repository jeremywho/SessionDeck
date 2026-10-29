using System.Diagnostics;

namespace SessionDeck;

internal sealed record ScanResult(
    List<SessionInfo> Sessions,
    List<Host.HostRecord> Hosts,
    Dictionary<int, int> Parents,
    List<SessionInfo> Unowned,
    int ClaudeDiscovered,
    int CodexDiscovered,
    long ElapsedMs);

/// <summary>
/// The one way the session list is built: scan every CLI's own records, fold headless Codex threads
/// onto their owners, discover the deck's hosts, pair each host with the session it runs, and let the
/// host's hooks and agy's log fill in what the scanners cannot see. The window, the <c>--list</c>
/// diagnostic and the gateway all call this, so there is one answer to "what state is this session in".
/// </summary>
internal static class SessionPipeline
{
    /// <summary>
    /// Codex publishes no live registry: which process holds each rollout open is learned by
    /// <see cref="CodexScanner.Probe"/>, and <see cref="CodexScanner.Scan"/> returns nothing until that map
    /// exists. The window keeps it warm on a thread of its own; a headless caller runs this once before
    /// its first scan and <see cref="CodexScanner.Probe"/> once per cycle after.
    /// </summary>
    public static void WarmUp()
    {
        for (int i = 0; i < 4; i++)
        {
            CodexScanner.Probe();
            if (i < 3) Thread.Sleep(3000);
        }
    }

    public static ScanResult Scan(bool prune = true)
    {
        var total = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();
        var claude = SessionScanner.Scan();
        long claudeMs = phase.ElapsedMilliseconds;
        phase.Restart();
        var codex = CodexScanner.Scan();
        long codexMs = phase.ElapsedMilliseconds;
        phase.Restart();
        var parents = Native.BuildParentMap();
        long processesMs = phase.ElapsedMilliseconds;
        phase.Restart();
        var unowned = new List<SessionInfo>();
        var found = CodexAttribution.Fold(claude, codex, parents, unowned);
        long attributionMs = phase.ElapsedMilliseconds;
        phase.Restart();
        var hosts = HostManager.Discover(prune);
        long registryMs = phase.ElapsedMilliseconds;
        total.Stop();
        if (DwmDiagnosticOptions.Enabled || total.ElapsedMilliseconds >= 1000)
            PerformanceLog.Write($"session-scan {total.ElapsedMilliseconds}ms claude={claudeMs} codex={codexMs} " +
                $"processes={processesMs} attribution={attributionMs} hosts={registryMs} sessions={found.Count}");
        DwmDiagnostics.Mark("session-scan", $"elapsedMs={total.ElapsedMilliseconds}; claudeMs={claudeMs}; codexMs={codexMs}; processesMs={processesMs}; attributionMs={attributionMs}; registryMs={registryMs}; sessions={found.Count}");
        return new ScanResult(found, hosts, parents, unowned, claude.Count, codex.Count, total.ElapsedMilliseconds);
    }

    /// <summary>
    /// One row per host, whether or not a tab is open. A scanned session belongs to a host when its id
    /// matches (Claude, whose id the deck chose) or when its process descends from the host's child
    /// (Codex, which names its own thread). Sessions that belong to no host land in <paramref name="claimed"/>'s
    /// complement for the caller to remember; they never become rows.
    /// </summary>
    public static List<(Host.HostRecord Host, SessionInfo Info)> Pair(ScanResult scan, HashSet<SessionInfo>? claimed = null)
    {
        claimed ??= new HashSet<SessionInfo>();
        var byHost = HostPairing.Pair(scan.Hosts, scan.Sessions, (s, h) => DescendsFrom(s.Pid, h.ChildPid, scan.Parents), claimed);
        foreach (var (h, info) in byHost) ApplyHookState(h, info);
        ApplyAgyNames(byHost, SessionScanner.ProcessTable);
        return byHost;
    }

    internal static void ApplyHookState(Host.HostRecord h, SessionInfo info)
    {
        if (h.StatusAt is not DateTime at) return;
        if (h.AgentStatus == "ended" || h.HasExited) return;
        bool newer = at > info.StatusUpdatedAt.ToUniversalTime();
        // Hosts from before protocol 2 called Claude's idle notification "waiting"; read it as idle.
        string agentStatus = h.Protocol < 2 && h.AgentStatus == "waiting" && h.LastEvent == "Notification" ? "idle" : h.AgentStatus;
        // Hosts before protocol 3 pinned a turn that ran a background shell as "scheduled"; the app
        // sees live background work through its output files, so read the hook's word as idle.
        if (h.Protocol < 3 && agentStatus == "scheduled") agentStatus = "idle";
        // A SessionStart mid-turn (Codex compaction) must not downgrade a turn the CLI's own record says is open.
        if (h.LastEvent == "SessionStart" && agentStatus == "idle" && info.Status == "busy") return;
        // The CLI's own registry says "shell" while a background shell it started still runs, long
        // after the turn that started it ended and the session began waiting for the person. The
        // hook's word on the turn wins; the shell shows as a badge, not as the session working.
        if (agentStatus.Length > 0 && (newer || info.Status.Length == 0 || info.Status == "shell"))
        {
            info.Status = agentStatus;
            info.StatusUpdatedAt = at.ToLocalTime();
        }
        // The host stamps StatusAt only when the status changes, so mid-turn the two are equal: the
        // hook still owns the status then, and its tool name is the live one.
        bool hookOwns = at >= info.StatusUpdatedAt.ToUniversalTime();
        if (h.LastTool.Length > 0 && (hookOwns || info.LastTool.Length == 0)) info.LastTool = h.LastTool;
        if (info.SessionId.Length == 0 && h.SessionId.Length > 0) info.SessionId = h.SessionId;
        if (info.TranscriptPath.Length == 0 && h.TranscriptPath.Length > 0) info.TranscriptPath = h.TranscriptPath;
        if (info.Model.Length == 0 && h.Model.Length > 0) info.Model = h.Model;
        if (info.Effort.Length == 0) info.Effort = SessionRow.EffortOfModel(info.Model);
    }

    /// <summary>An agy pane is named by its conversation's own /rename, like Claude and Codex panes are named by their CLIs.</summary>
    internal static void ApplyAgyNames(List<(Host.HostRecord Host, SessionInfo Info)> byHost, IReadOnlyDictionary<int, (int Parent, string Name)> table)
    {
        if (!byHost.Any(x => x.Host.Provider == "Agy")) return;
        foreach (var (h, info) in byHost)
        {
            if (h.Provider != "Agy" || h.HasExited) continue;
            string name = h.SessionId.Length > 0 ? AgyScanner.NameForConversation(h.SessionId) : "";
            if (h.SessionId.Length == 0)
            {
                int agyPid = AgyScanner.AgyPidUnder(h.ChildPid, table);
                if (agyPid == 0) continue;
                // A pane started before hooks were wired still gets its conversation from agy's log, so
                // it can be restarted (onto hooks) and comes back after a reboot.
                if (info.SessionId.Length == 0) info.SessionId = AgyScanner.ConversationFor(agyPid);
                name = AgyScanner.NameFor(agyPid);
            }
            if (name.Length > 0) info.Name = name;
        }
    }

    internal static bool DescendsFrom(int pid, int ancestor, Dictionary<int, int> parents)
    {
        for (int hops = 0; hops < 12 && pid > 0; hops++)
        {
            if (pid == ancestor) return true;
            if (!parents.TryGetValue(pid, out pid)) return false;
        }
        return false;
    }
}
