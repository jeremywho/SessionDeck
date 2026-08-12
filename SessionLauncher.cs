using System.Diagnostics;
using System.IO;

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

    // ---------------------------------------------------------------- inherited environment

    /// <summary>
    /// Variables that switch colored output OFF in the CLIs we launch. We start terminals with
    /// <c>UseShellExecute=false</c>, so the child inherits OUR environment — meaning anything that set
    /// one of these on this app silently strips the color out of every session it opens.
    /// <para>This is not hypothetical: an agent harness that sets <c>NO_COLOR=1</c> for clean tool
    /// output relaunched this app as a child, and from then on every terminal it opened was monochrome.
    /// The app's own code was untouched, which is exactly why it was hard to see.</para>
    /// </summary>
    static readonly string[] ColorKillSwitches = { "NO_COLOR" };

    /// <summary>
    /// True when a variable is set on THIS process but is not persisted for the user or the machine —
    /// i.e. whatever launched us injected it, and it is not a preference anyone chose.
    /// <para>The distinction is the whole point: someone who genuinely sets <c>NO_COLOR</c> in their
    /// user environment wants no color and must keep getting none, so only the injected case is dropped.</para>
    /// </summary>
    public static bool WasInjectedIntoThisProcess(string? processValue, string? userValue, string? machineValue)
        => !string.IsNullOrEmpty(processValue)
           && string.IsNullOrEmpty(userValue)
           && string.IsNullOrEmpty(machineValue);

    /// <summary>Which color kill-switches our own process picked up from whatever launched it.</summary>
    static IEnumerable<string> InjectedColorKillSwitches()
    {
        foreach (var name in ColorKillSwitches)
        {
            string? process, user = null, machine = null;
            try
            {
                process = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(process)) continue;
                user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                machine = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            }
            catch { continue; }   // registry read denied — leave the child's environment alone
            if (WasInjectedIntoThisProcess(process, user, machine)) yield return name;
        }
    }

    /// <summary>Remove variables from the child's copy of the environment. Named explicitly so tests
    /// can drive it without depending on the machine's real environment.</summary>
    public static void StripFromChild(ProcessStartInfo psi, IEnumerable<string> names)
    {
        foreach (var name in names) psi.Environment.Remove(name);
    }

    // ---------------------------------------------------------------- process start

    /// <summary>
    /// Resolve a shell by absolute path instead of relying on the tray app's inherited PATH. An
    /// installed app can start before a PATH update or inherit Explorer's stale environment, while
    /// the executable is already present and usable. Prefer PowerShell 7, then Windows' built-in
    /// PowerShell; the bare name is only a last resort for nonstandard installations.
    /// </summary>
    internal static string ResolvePowerShell(Func<string, bool>? exists = null,
        string? programFiles = null, string? systemDirectory = null)
    {
        exists ??= File.Exists;
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        systemDirectory ??= Environment.SystemDirectory;

        string pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (exists(pwsh)) return pwsh;
        string windowsPowerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return exists(windowsPowerShell) ? windowsPowerShell : "pwsh.exe";
    }

    /// <summary>
    /// The Windows Terminal invocation: `wt -w <window> new-tab -d <cwd> pwsh -NoExit -Command <cmd>`.
    /// `-w -1` forces a new window; `-w 0` targets its most recently used window — verified to follow
    /// the last *focused* terminal, not the invoking one, which is what makes it the right answer when
    /// the click comes from this app's window. The pwsh wrapper keeps the tab open (and shows any
    /// error) after the CLI exits.
    /// </summary>
    public static ProcessStartInfo BuildTerminalStart(string cwd, string cmd, LaunchTarget target,
                                                      IEnumerable<string>? strip = null,
                                                      string? shellExecutable = null)
    {
        string window = target == LaunchTarget.NewWindow ? "-1" : "0";
        var psi = new ProcessStartInfo("wt.exe")
        {
            UseShellExecute = false,
            // Hygiene for launching a console helper from a GUI process, and nothing more. It was
            // added believing it fixed a window flashing before the terminal appeared; it does not.
            // Measured with a WinEvent hook, launching wt.exe creates ZERO windows either way — that
            // flash came from a Claude SessionStart hook spawning `gh` without windowsHide, in a
            // different repo entirely. Don't read this line as load-bearing.
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-w", window, "new-tab", "-d", cwd,
                                  shellExecutable ?? ResolvePowerShell(), "-NoExit", "-Command", cmd })
            psi.ArgumentList.Add(a);
        StripFromChild(psi, strip ?? InjectedColorKillSwitches());
        return psi;
    }

    /// <summary>
    /// Fallback for machines without Windows Terminal: a bare pwsh window. <c>UseShellExecute</c> is
    /// FALSE here even though nothing is redirected — it's the only mode that lets us edit the child's
    /// environment, and a console app started this way from a GUI process still gets its own window.
    /// </summary>
    public static ProcessStartInfo BuildFallbackStart(string cwd, string cmd, IEnumerable<string>? strip = null,
                                                       string? shellExecutable = null)
    {
        var psi = new ProcessStartInfo(shellExecutable ?? ResolvePowerShell())
            { UseShellExecute = false, WorkingDirectory = cwd };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(cmd);
        StripFromChild(psi, strip ?? InjectedColorKillSwitches());
        return psi;
    }

    static bool Start(string cwd, string cmd, LaunchTarget target)
    {
        try
        {
            Process.Start(BuildTerminalStart(cwd, cmd, target));
            return true;
        }
        catch { /* Windows Terminal not available — fall back to a bare pwsh window */ }

        // No Windows Terminal means no tabs to open, so a LastWindow request degrades to a window.
        try
        {
            Process.Start(BuildFallbackStart(cwd, cmd));
            return true;
        }
        catch { return false; }
    }

    static bool IsShortId(SavedSession s) =>
        s.Name.Length == 8 && s.Id.StartsWith(s.Name, StringComparison.OrdinalIgnoreCase);
}
