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

        // Single instance: a second launch just exits.
        using var mutex = new Mutex(true, "ClaudeSessionMonitor_SingleInstance", out bool isNew);
        if (!isNew) return;

        var app = new App();
        app.Run();
    }
}
