using System.Linq;

namespace ClaudeSessionMonitor;

/// <summary>
/// Brings a session's terminal window+tab to the foreground.
///
/// Windows Terminal hosts sessions as tabs that may be spread across several top-level windows,
/// all under ONE process — so Process.MainWindowHandle is useless here. We enumerate the host
/// process's windows, then use UI Automation to find the TAB whose title matches the session
/// (across all those windows). That yields both the correct window and the correct tab.
/// </summary>
internal static class WindowActivator
{
    public static bool Activate(SessionInfo s)
    {
        var byPid = Native.TopWindowsByPid();
        int hostPid = FindHostPid(s.Pid, byPid);
        if (hostPid == 0 || !byPid.TryGetValue(hostPid, out var windows) || windows.Count == 0)
            return false;

        var candidates = Candidates(s);

        // Primary: match a TAB by its UIA name across all host windows (covers multi-tab windows).
        if (candidates.Count > 0)
        {
            var hit = TabSelector.FindTab(windows.Select(w => w.Hwnd), candidates);
            if (hit.HasValue)
            {
                try { TabSelector.Select(hit.Value.Tab); } catch { }
                ForceForeground(hit.Value.WindowHwnd);
                return true;
            }
        }

        // Fallback: match the window title (single-tab windows where UIA didn't enumerate tabs).
        IntPtr byTitle = MatchByTitle(windows, candidates);
        if (byTitle != IntPtr.Zero) { ForceForeground(byTitle); return true; }

        // Last resort: exactly one window -> use it; otherwise don't focus the wrong one.
        if (windows.Count == 1) { ForceForeground(windows[0].Hwnd); return true; }
        return false;
    }

    static List<string> Candidates(SessionInfo s) =>
        new[] { s.Name, s.Title, s.DisplayName }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(TabSelector.Normalize)
            .Where(x => x.Length > 0)
            .Distinct()
            .OrderByDescending(x => x.Length)
            .ToList();

    /// <summary>First ancestor process (from the session PID upward) that owns a visible titled window.</summary>
    static int FindHostPid(int sessionPid, Dictionary<int, List<(IntPtr Hwnd, string Title)>> byPid)
    {
        var parents = Native.BuildParentMap();
        int cur = sessionPid;
        for (int i = 0; i < 32; i++)
        {
            if (byPid.ContainsKey(cur)) return cur;
            if (!parents.TryGetValue(cur, out int par) || par == 0 || par == cur) break;
            cur = par;
        }
        return 0;
    }

    static IntPtr MatchByTitle(List<(IntPtr Hwnd, string Title)> windows, List<string> candidates)
    {
        foreach (var c in candidates)
            foreach (var w in windows)
                if (TabSelector.Normalize(w.Title) == c) return w.Hwnd;
        foreach (var c in candidates)
            foreach (var w in windows)
                if (TabSelector.Normalize(w.Title).Contains(c)) return w.Hwnd;
        return IntPtr.Zero;
    }

    /// <summary>Diagnostic (used by `--windows`): describe what a session resolves to, no foregrounding.</summary>
    public static string DebugPick(SessionInfo s, Dictionary<int, List<(IntPtr Hwnd, string Title)>> byPid)
    {
        int hostPid = FindHostPid(s.Pid, byPid);
        if (hostPid == 0 || !byPid.TryGetValue(hostPid, out var windows) || windows.Count == 0)
            return "(no host window)";

        var candidates = Candidates(s);
        var hit = TabSelector.FindTab(windows.Select(w => w.Hwnd), candidates);
        if (hit.HasValue)
        {
            string winTitle = windows.First(w => w.Hwnd == hit.Value.WindowHwnd).Title;
            return $"tab '{hit.Value.Tab.Current.Name}' in window '{winTitle}' (hwnd {hit.Value.WindowHwnd.ToInt64()})";
        }
        IntPtr bt = MatchByTitle(windows, candidates);
        if (bt != IntPtr.Zero) return $"window-title match (hwnd {bt.ToInt64()})";
        return windows.Count == 1 ? $"single window (hwnd {windows[0].Hwnd.ToInt64()})" : "(no match)";
    }

    /// <summary>SetForegroundWindow with the AttachThreadInput dance to bypass focus-stealing limits.</summary>
    static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        IntPtr fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint ourThread = Native.GetCurrentThreadId();

        bool attached = fgThread != ourThread && Native.AttachThreadInput(ourThread, fgThread, true);
        Native.BringWindowToTop(hwnd);
        Native.SetForegroundWindow(hwnd);
        if (attached) Native.AttachThreadInput(ourThread, fgThread, false);
    }
}
