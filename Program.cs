namespace ClaudeSessionMonitor;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
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

            var all = SessionScanner.Scan();
            all.AddRange(CodexScanner.Scan());

            foreach (var s in all)
                sb.AppendLine($"{s.Provider}\t{s.Pid}\t{(s.ApiError ? "ERROR" : s.Status)}\t{s.ShortId}\t{s.Model}\t{s.Effort}\t{s.ContextDisplay}\t{s.SubagentsActive}/{s.SubagentsTotal}\t{s.LastTool}\t{s.DisplayName}\t{s.Cwd}");
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude-sessions-dump.txt"),
                sb.ToString());
            return;
        }

        // Headless diagnostic: which terminal window each session maps to.
        if (args.Length > 0 && args[0] == "--windows")
        {
            CodexScanner.Probe();
            var byPid = Native.TopWindowsByPid();
            var all = SessionScanner.Scan();
            all.AddRange(CodexScanner.Scan());   // focus is provider-agnostic — it only needs a PID

            var sb = new System.Text.StringBuilder();
            foreach (var s in all)
                sb.AppendLine($"{s.Provider}\t{s.DisplayName}\t(pid {s.Pid})\t-> {WindowActivator.DebugPick(s, byPid)}");
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude-windows-test.txt"), sb.ToString());
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
            var m = new Mutex(true, "ClaudeSessionMonitor_SingleInstance", out bool isNew);
            if (isNew) return m;
            m.Dispose();
            if (i >= retries) return null;     // held and out of retries -> a genuine second instance, exit
            Thread.Sleep(100);
        }
    }
}
