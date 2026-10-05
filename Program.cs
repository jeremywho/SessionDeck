namespace SessionDeck;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // One pseudoconsole per process: `--host` is the session host the app spawns and reattaches to.
        if (args.Length > 0 && args[0] == "--host")
        {
            Environment.Exit(SessionDeck.Host.HostProgram.Run());
            return;
        }

        // A CLI hook: forward the event JSON on stdin to this session's host. Never fails the CLI:
        // any error is swallowed and the exit code is 0.
        if (args.Length == 3 && args[0] == "--hook")
        {
            try
            {
                string body = Console.In.ReadToEnd();
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
                http.PostAsync($"http://127.0.0.1:{args[1]}/hook?token={args[2]}", content).GetAwaiter().GetResult();
            }
            catch { }
            Environment.Exit(0);
            return;
        }

        // Headless diagnostic: dump live sessions to a temp file and exit (verification / CLI use).
        if (args.Length > 0 && args[0] == "--list")
        {
            // Codex liveness is resolved by the background probe, which the UI normally runs; do it
            // inline so the headless dump sees what the window would. Several passes, because "which
            // subagents are working now" is a size DELTA — one pass has nothing to compare against, and
            // Codex writes in bursts with quiet stretches of several seconds between them.
            for (int i = 0; i < 4; i++)
            {
                CodexScanner.Probe();
                if (i < 3) Thread.Sleep(3000);
            }
            // Printed BEFORE the scan on purpose: at this point nothing has parsed a live session, so
            // anything here came from the startup seed — the path that has to work when no Codex is running.
            var sb = new System.Text.StringBuilder();
            foreach (var m in CodexScanner.PlanMeters)
                sb.AppendLine($"PLAN\t{m.Label}\t{m.Percent}%\t{m.Severity}\tresets {m.ResetsAt:g}\t{m.Note}");

            // Folded, like the window: headless Codex threads roll onto the session that started them.
            var all = CodexAttribution.Fold(SessionScanner.Scan(), CodexScanner.Scan(), Native.BuildParentMap());

            foreach (var s in all)
                sb.AppendLine($"{s.Provider}\t{s.Pid}\t{(s.ApiError ? "ERROR" : s.Status)}\t{s.ShortId}\t{s.Model}\t{s.Effort}\t{s.ContextDisplay}\t{s.SubagentsActive}/{s.SubagentsTotal}\tcodex-tasks:{s.BackgroundTasks}\t{s.LastTool}\t{s.DisplayName}\t{s.Cwd}");
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sessiondeck-dump.txt"),
                sb.ToString());
            return;
        }

        // Headless diagnostic: build the pull requests board once and dump it (verification / CLI use).
        if (args.Length > 0 && args[0] == "--prs")
        {
            var settings = Settings.Load();
            var snap = PrSource.FetchAsync(a => GhCli.RunAsync(a, TimeSpan.FromSeconds(60)), DateTime.UtcNow, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var (worktrees, local) = snap.Status == PrSourceStatus.Ok
                ? WorktreeIndex.Scan(snap.Prs, settings.PrRepoRoots)
                : (new List<Worktree>(), new Dictionary<string, LocalState>());
            CodexScanner.Probe();
            var unowned = new List<SessionInfo>();
            var sessions = CodexAttribution.Fold(SessionScanner.Scan(), CodexScanner.Scan(), Native.BuildParentMap(), unowned);
            var links = PrAttribution.Attribute(PrPaneController.SourcesFrom(sessions.Concat(unowned)), PrTargets.From(snap.Prs, worktrees), new ToolCallCache(), DateTime.UtcNow);
            var byId = sessions.Where(s => s.SessionId.Length > 0).GroupBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var unownedById = unowned.Where(s => s.SessionId.Length > 0).GroupBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            AgentView? Describe(string id) =>
                byId.TryGetValue(id, out var s)
                    ? new AgentView(s.DisplayName, s.Provider.ToString(), PrPaneController.StateToken(s.ApiError ? SessionState.Error : SessionStateMap.FromStatus(s.Status)))
                    : unownedById.TryGetValue(id, out var u) ? PrPaneController.DescribeUnowned(u) : null;
            var board = PrBoardBuilder.Build(snap, "", worktrees, local, links, Describe,
                new HashSet<string>(settings.PrExpandedSeries, StringComparer.Ordinal),
                new HashSet<string>(settings.PrCollapsedSections, StringComparer.Ordinal));
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sessiondeck-prs.json"),
                System.Text.Json.JsonSerializer.Serialize(board, new System.Text.Json.JsonSerializerOptions(PrPaneController.BoardJson) { WriteIndented = true }));
            return;
        }

        // Headless diagnostic: which terminal window each session maps to.
        if (args.Length > 0 && args[0] == "--windows")
        {
            CodexScanner.Probe();
            var byPid = Native.TopWindowsByPid();
            // Folded, like the window — so this dump reflects the rows you'd actually see. Anything
            // still reporting "no window" here is a row that shouldn't exist.
            var all = CodexAttribution.Fold(SessionScanner.Scan(), CodexScanner.Scan(), Native.BuildParentMap());

            var sb = new System.Text.StringBuilder();
            foreach (var s in all)
                sb.AppendLine($"{s.Provider}\t{s.DisplayName}\t(pid {s.Pid})\t-> {WindowActivator.DebugPick(s, byPid)}");
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sessiondeck-windows-test.txt"), sb.ToString());
            return;
        }

        // First run from anywhere but the install dir -> copy self to %LOCALAPPDATA%\Programs and relaunch.
        if (Installer.MaybeSelfInstall(args)) return;

        // If a freshly-updated build crash-looped last time, revert to the previous exe before we start.
        Updater.CheckRollbackOnStartup();

        // Single instance. After an update relaunch the old instance may still be exiting -> brief retry.
        var mutex = AcquireSingleInstance(args.Contains("--updated"));
        if (mutex == null) return;
        using (mutex)
        {
            var app = new App();
            app.Run();
        }
    }

    /// <summary>
    /// One instance at a time. A relaunch right behind an exiting instance waits for the mutex
    /// (longer after an update, which hard-exits); a launch while another instance is up and staying
    /// up is logged, so a deck that "never appeared" can be read from the performance log.
    /// </summary>
    static Mutex? AcquireSingleInstance(bool afterUpdate)
    {
        int retries = afterUpdate ? 80 : 30;   // 100 ms steps: ~8 s after an update, ~3 s otherwise
        for (int i = 0; ; i++)
        {
            var m = new Mutex(true, Settings.InstanceMutexName, out bool isNew);
            if (isNew) return m;
            m.Dispose();
            if (i >= retries)
            {
                PerformanceLog.Write($"launch: another instance holds the mutex after {retries / 10} s; exiting (afterUpdate={afterUpdate})");
                return null;
            }
            Thread.Sleep(100);
        }
    }
}
