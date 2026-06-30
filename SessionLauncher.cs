using System.Diagnostics;

namespace ClaudeSessionMonitor;

/// <summary>Reopens a saved session in a new Windows Terminal window via `claude --resume`.</summary>
internal static class SessionLauncher
{
    public static bool Resume(SavedSession s, string extraFlags)
    {
        string cwd = string.IsNullOrWhiteSpace(s.Cwd)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : s.Cwd;

        // pwsh wrapper keeps the tab open (and shows any error) after claude exits.
        string cmd = $"claude --resume {s.Id}";
        if (!string.IsNullOrWhiteSpace(s.Name) && !IsShortId(s))
            cmd += $" --name '{s.Name.Replace("'", "''")}'";   // pwsh single-quote escaping
        if (!string.IsNullOrWhiteSpace(extraFlags))
            cmd += " " + extraFlags.Trim();

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
