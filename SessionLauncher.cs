using System.Diagnostics;

namespace ClaudeSessionMonitor;

/// <summary>Opens claude sessions in a new Windows Terminal window — resumed or brand-new.</summary>
internal static class SessionLauncher
{
    public static bool Resume(SavedSession s, string extraFlags)
    {
        string cwd = string.IsNullOrWhiteSpace(s.Cwd)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : s.Cwd;

        string cmd = $"claude --resume {s.Id}";
        if (!string.IsNullOrWhiteSpace(s.Name) && !IsShortId(s))
            cmd += $" --name '{s.Name.Replace("'", "''")}'";   // pwsh single-quote escaping
        if (!string.IsNullOrWhiteSpace(extraFlags))
            cmd += " " + extraFlags.Trim();

        return Start(cwd, cmd);
    }

    /// <summary>Start a brand-new claude session (optionally named) in the user's home directory.</summary>
    public static bool LaunchNew(string? name, string extraFlags)
    {
        string cmd = "claude";
        if (!string.IsNullOrWhiteSpace(name))
            cmd += $" --name '{name.Replace("'", "''")}'";
        if (!string.IsNullOrWhiteSpace(extraFlags))
            cmd += " " + extraFlags.Trim();

        return Start(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cmd);
    }

    /// <summary>Run `pwsh -NoExit -Command <cmd>` in a new terminal window at cwd. The pwsh
    /// wrapper keeps the tab open (and shows any error) after claude exits.</summary>
    static bool Start(string cwd, string cmd)
    {
        // Preferred: a new Windows Terminal window.
        try
        {
            var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            foreach (var a in new[] { "-w", "-1", "new-tab", "-d", cwd, "pwsh", "-NoExit", "-Command", cmd })
                wt.ArgumentList.Add(a);
            Process.Start(wt);
            return true;
        }
        catch { /* Windows Terminal not available — fall back to a bare pwsh window */ }

        try
        {
            var ps = new ProcessStartInfo("pwsh.exe") { UseShellExecute = true, WorkingDirectory = cwd };
            ps.ArgumentList.Add("-NoExit");
            ps.ArgumentList.Add("-Command");
            ps.ArgumentList.Add(cmd);
            Process.Start(ps);
            return true;
        }
        catch { return false; }
    }

    static bool IsShortId(SavedSession s) =>
        s.Name.Length == 8 && s.Id.StartsWith(s.Name, StringComparison.OrdinalIgnoreCase);
}
