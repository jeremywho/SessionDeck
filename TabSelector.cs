using System.Text;
using System.Windows.Automation;

namespace ClaudeSessionMonitor;

/// <summary>
/// UI Automation matching of Windows Terminal tabs. WT exposes every tab (including inactive ones)
/// as a TabItem whose accessible Name is the tab title Claude Code set. We match that against the
/// session's name/title to find both the host window AND the tab to activate.
/// </summary>
internal static class TabSelector
{
    /// <summary>Across the given top-level windows, find a tab whose UIA name matches a candidate.</summary>
    public static (IntPtr WindowHwnd, AutomationElement Tab)? FindTab(
        IEnumerable<IntPtr> windows, IReadOnlyList<string> normalizedCandidates)
    {
        var all = new List<(IntPtr Hwnd, string Norm, AutomationElement El)>();
        foreach (var hwnd in windows)
        {
            AutomationElement? root;
            try { root = AutomationElement.FromHandle(hwnd); } catch { continue; }
            if (root == null) continue;

            AutomationElementCollection tabs;
            try
            {
                tabs = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            }
            catch { continue; }

            foreach (AutomationElement t in tabs)
            {
                string n = Normalize(t.Current.Name);
                if (n.Length > 0) all.Add((hwnd, n, t));
            }
        }

        foreach (var c in normalizedCandidates)            // exact match first
            foreach (var e in all)
                if (e.Norm == c) return (e.Hwnd, e.El);
        foreach (var c in normalizedCandidates)            // then containment (handles glyph prefixes)
            foreach (var e in all)
                if (e.Norm.Contains(c) || c.Contains(e.Norm)) return (e.Hwnd, e.El);
        return null;
    }

    /// <summary>Every tab whose accessible name matches <paramref name="normalized"/> exactly. Returns
    /// them all rather than the first, because "how many" is the question that decides whether a match
    /// can be trusted — two tabs with the same title cannot be told apart by title.</summary>
    public static List<(IntPtr WindowHwnd, AutomationElement Tab)> FindTabsExact(
        IEnumerable<IntPtr> windows, string normalized)
    {
        var hits = new List<(IntPtr, AutomationElement)>();
        if (normalized.Length == 0) return hits;

        foreach (var hwnd in windows)
        {
            AutomationElement? root;
            try { root = AutomationElement.FromHandle(hwnd); } catch { continue; }
            if (root == null) continue;

            AutomationElementCollection tabs;
            try
            {
                tabs = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            }
            catch { continue; }

            foreach (AutomationElement t in tabs)
                if (Normalize(t.Current.Name) == normalized) hits.Add((hwnd, t));
        }
        return hits;
    }

    /// <summary>All TabItem accessible names (normalized) across the given windows, paired with their window.
    /// One UIA pass — used by the virtual-desktop resolver to map every session's tab to its host window.</summary>
    public static List<(IntPtr Hwnd, string Norm)> EnumerateTabs(IEnumerable<IntPtr> windows)
    {
        var all = new List<(IntPtr, string)>();
        foreach (var hwnd in windows)
        {
            AutomationElement? root;
            try { root = AutomationElement.FromHandle(hwnd); } catch { continue; }
            if (root == null) continue;
            AutomationElementCollection tabs;
            try
            {
                tabs = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            }
            catch { continue; }
            foreach (AutomationElement t in tabs)
            {
                string n = Normalize(t.Current.Name);
                if (n.Length > 0) all.Add((hwnd, n));
            }
        }
        return all;
    }

    public static void Select(AutomationElement tab)
    {
        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sip)) ((SelectionItemPattern)sip).Select();
        else if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out object ip)) ((InvokePattern)ip).Invoke();
    }

    /// <summary>The class name Windows Terminal gives the control that actually holds the session.</summary>
    const string TerminalPaneClass = "TermControl";

    /// <summary>
    /// Put keyboard focus in the terminal itself so you can start typing.
    ///
    /// Selecting a tab focuses its HEADER — the tab is visibly highlighted, keystrokes go to the tab
    /// strip, and you have to click the terminal before typing does anything. In WT's UIA tree the
    /// session lives in a sibling of the tab strip, a keyboard-focusable element classed
    /// <c>TermControl</c>; focusing that is what makes double-click land you at the prompt.
    ///
    /// Call AFTER the window is foregrounded — SetFocus on a background window is refused.
    /// </summary>
    public static void FocusTerminal(IntPtr windowHwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(windowHwnd);
            if (root == null) return;

            // Re-queried after the tab switch rather than cached: WT realizes the content of the
            // active tab, so this resolves to the session we just brought forward.
            var pane = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, TerminalPaneClass),
                new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true)));
            pane?.SetFocus();
        }
        catch { }   // best-effort: the window is already foreground, which is most of the win
    }

    /// <summary>Lowercase, alphanumerics only — strips the spinner/check glyph prefixes WT shows.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
