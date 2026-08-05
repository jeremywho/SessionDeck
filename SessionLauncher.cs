using System.Diagnostics;

namespace ClaudeSessionMonitor;

/// <summary>Where a launched session's terminal should land.</summary>
internal enum LaunchTarget
{
    /// <summary>Its own new Windows Terminal window.</summary>
    NewWindow,

    /// <summary>A new tab in the terminal window you used last.</summary>
    LastWindow,
}

/// <summary>Opens claude sessions in Windows Terminal — resumed or brand-new.</summary>
internal static class SessionLauncher
{
    /// <summary>
    /// Reopen a saved session with its own CLI. <paramref name="extraFlags"/> must already be the
    /// right set for that provider — the two CLIs share no flag spelling (Claude's
    /// <c>--dangerously-skip-permissions</c> vs Codex's <c>--dangerously-bypass-approvals-and-sandbox</c>),
    /// so passing one to the other just makes it exit on an unknown argument.
    /// </summary>
    public static bool Resume(SavedSession s, string extraFlags)
    {
        string cwd = string.IsNullOrWhiteSpace(s.Cwd)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : s.Cwd;

        // Restores always get their own window: a restore can fire several at once, and stacking
        // them as tabs in whatever window you were using would bury it.
        return Start(cwd, ResumeCommand(s, extraFlags), LaunchTarget.NewWindow);
    }

    /// <summary>The shell command that reopens a saved session. Split out from <see cref="Resume"/>
    /// so the two CLIs' very different invocations can be asserted without launching anything.</summary>
    public static string ResumeCommand(SavedSession s, string extraFlags)
    {
        // `codex resume <id>` takes the thread id and nothing else — there is no --name to pass,
        // because on Codex's side the name is already attached to the thread.
        string cmd = s.Provider == SessionProvider.Codex
            ? $"codex resume {s.Id}"
            : ClaudeResumeCommand(s);

        if (!string.IsNullOrWhiteSpace(extraFlags))
            cmd += " " + extraFlags.Trim();
        return cmd;
    }

    static string ClaudeResumeCommand(SavedSession s)
    {
        string cmd = $"claude --resume {s.Id}";
        if (!string.IsNullOrWhiteSpace(s.Name) && !IsShortId(s))
            cmd += $" --name '{s.Name.Replace("'", "''")}'";   // pwsh single-quote escaping
        return cmd;
    }

    /// <summary>Start a brand-new claude session (optionally named) in the user's home directory.</summary>
    public static bool LaunchNew(string? name, string extraFlags, LaunchTarget target) =>
        Start(Home, NewCommand(name, extraFlags), target);

    /// <summary>Start a brand-new codex session in the user's home directory.</summary>
    public static bool LaunchNewCodex(string extraFlags, LaunchTarget target) =>
        Start(Home, NewCodexCommand(extraFlags), target);

    /// <summary>Test seam: the command a new-session button runs.</summary>
    public static string NewCommand(string? name, string extraFlags)
    {
        string cmd = "claude";
        if (!string.IsNullOrWhiteSpace(name))
            cmd += $" --name '{name.Replace("'", "''")}'";
        if (!string.IsNullOrWhiteSpace(extraFlags))
            cmd += " " + extraFlags.Trim();
        return cmd;
    }

    /// <summary>
    /// Test seam: the command a new-Codex-session button runs. There is deliberately no name
    /// parameter — <c>codex</c> has no <c>--name</c> (it rejects the argument outright); a Codex thread
    /// is named from inside the TUI, after it starts. That's why the Codex launcher row is two buttons
    /// where Claude's is four.
    /// </summary>
    public static string NewCodexCommand(string extraFlags)
    {
        string cmd = "codex";
        if (!string.IsNullOrWhiteSpace(extraFlags))
            cmd += " " + extraFlags.Trim();
        return cmd;
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Run `pwsh -NoExit -Command <cmd>` in a terminal at cwd. The pwsh wrapper keeps the
    /// tab open (and shows any error) after claude exits.</summary>
    static bool Start(string cwd, string cmd, LaunchTarget target)
    {
        // Preferred: Windows Terminal. `-w -1` forces a new window; `-w 0` targets its most recently
        // used window — verified to follow the last *focused* terminal, not the invoking one, which
        // is what makes it the right answer when the click comes from this app's window.
        try
        {
            string window = target == LaunchTarget.NewWindow ? "-1" : "0";
            var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            foreach (var a in new[] { "-w", window, "new-tab", "-d", cwd, "pwsh", "-NoExit", "-Command", cmd })
                wt.ArgumentList.Add(a);
            Process.Start(wt);
            return true;
        }
        catch { /* Windows Terminal not available — fall back to a bare pwsh window */ }

        // No Windows Terminal means no tabs to open, so a LastWindow request degrades to a window.

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
