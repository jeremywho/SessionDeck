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
                TabSelector.FocusTerminal(hit.Value.WindowHwnd);
                return true;
            }
        }

        // Fallback: match the window title (single-tab windows where UIA didn't enumerate tabs).
        IntPtr byTitle = MatchByTitle(windows, candidates);
        if (byTitle != IntPtr.Zero) { ForceForeground(byTitle); TabSelector.FocusTerminal(byTitle); return true; }

        // Last resort: exactly one window -> use it; otherwise don't focus the wrong one.
        if (windows.Count == 1) { ForceForeground(windows[0].Hwnd); TabSelector.FocusTerminal(windows[0].Hwnd); return true; }
        return false;
    }

    /// <summary>
    /// Strings that might be the terminal's tab title, longest first.
    ///
    /// <see cref="SessionInfo.SessionId"/> is in here for Codex: it sets the terminal title to its
    /// thread UUID rather than to anything human-readable, so without the id nothing matched and
    /// double-clicking a Codex row did nothing at all. A UUID is specific enough that it can't
    /// collide with a Claude tab's title, which is why it's safe to try for both.
    /// </summary>
    static List<string> Candidates(SessionInfo s) =>
        new[] { s.Name, s.Title, s.DisplayName, s.SessionId }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(TabSelector.Normalize)
            .Where(x => x.Length > 0)
            .Distinct()
            .OrderByDescending(x => x.Length)
            .ToList();

    /// <summary>First ancestor process (from the session PID upward) that owns a visible titled window.</summary>
    static int FindHostPid(int sessionPid, Dictionary<int, List<(IntPtr Hwnd, string Title)>> byPid)
        => FindHostPid(sessionPid, byPid, Native.BuildParentMap());

    static int FindHostPid(int sessionPid, Dictionary<int, List<(IntPtr Hwnd, string Title)>> byPid, Dictionary<int, int> parents)
    {
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

    /// <summary>
    /// Batch-resolve each session to the top-level window hosting its tab — one UIA pass over all the
    /// host windows, not one per session. Returns sessionId -> window hwnd. Used by the desktop resolver.
    /// </summary>
    public static Dictionary<string, IntPtr> ResolveWindows(IReadOnlyCollection<SessionInfo> sessions)
    {
        var result = new Dictionary<string, IntPtr>();
        if (sessions.Count == 0) return result;

        var byPid = Native.TopWindowsByPid();
        var parents = Native.BuildParentMap();

        var hostWindows = new HashSet<IntPtr>();
        var perSession = new Dictionary<string, List<(IntPtr Hwnd, string Title)>>();
        foreach (var s in sessions)
        {
            int host = FindHostPid(s.Pid, byPid, parents);
            if (host != 0 && byPid.TryGetValue(host, out var ws))
            {
                perSession[s.SessionId] = ws;
                foreach (var w in ws) hostWindows.Add(w.Hwnd);
            }
        }
        if (hostWindows.Count == 0) return result;

        var tabs = TabSelector.EnumerateTabs(hostWindows);
        foreach (var s in sessions)
        {
            if (!perSession.TryGetValue(s.SessionId, out var ws)) continue;
            var cands = Candidates(s);
            IntPtr hwnd = MatchTab(tabs, cands);
            if (hwnd == IntPtr.Zero) hwnd = MatchByTitle(ws, cands);
            if (hwnd == IntPtr.Zero && ws.Count == 1) hwnd = ws[0].Hwnd;
            if (hwnd != IntPtr.Zero) result[s.SessionId] = hwnd;
        }
        return result;
    }

    static IntPtr MatchTab(List<(IntPtr Hwnd, string Norm)> tabs, List<string> cands)
    {
        foreach (var c in cands) foreach (var t in tabs) if (t.Norm == c) return t.Hwnd;
        foreach (var c in cands) foreach (var t in tabs) if (t.Norm.Contains(c) || c.Contains(t.Norm)) return t.Hwnd;
        return IntPtr.Zero;
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
