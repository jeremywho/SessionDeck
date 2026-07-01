namespace ClaudeSessionMonitor;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Headless diagnostic: dump live sessions to a temp file and exit (verification / CLI use).
        if (args.Length > 0 && args[0] == "--list")
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in SessionScanner.Scan())
                sb.AppendLine($"{s.Pid}\t{(s.ApiError ? "ERROR" : s.Status)}\t{s.ShortId}\t{s.Model}\t{s.ContextDisplay}\t{s.LastTool}\t{s.DisplayName}\t{s.Cwd}");
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude-sessions-dump.txt"),
                sb.ToString());
            return;
        }

        // Headless diagnostic: which terminal window each session maps to.
        if (args.Length > 0 && args[0] == "--windows")
        {
            var byPid = Native.TopWindowsByPid();
            var sb = new System.Text.StringBuilder();
            foreach (var s in SessionScanner.Scan())
                sb.AppendLine($"{s.DisplayName}\t(pid {s.Pid})\t-> {WindowActivator.DebugPick(s, byPid)}");
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
