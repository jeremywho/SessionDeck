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

    static Mutex? AcquireSingleInstance(bool afterUpdate)
    {
        int retries = afterUpdate ? 80 : 0;   // ~8s of 100ms retries to let the old instance release the mutex
        for (int i = 0; ; i++)
        {
            var m = new Mutex(true, "SessionDeck_SingleInstance", out bool isNew);
            if (isNew) return m;
            m.Dispose();
            if (i >= retries) return null;     // held and out of retries -> a genuine second instance, exit
            Thread.Sleep(100);
        }
    }
}
