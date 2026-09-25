using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SessionDeck.Host;

namespace SessionDeck;

/// <summary>
/// Editor-group layout over one <see cref="DeckBrowser"/>: columns ("groups"), each with its own tab
/// strip and one visible tab. The page positions every group's visible terminal side by side and
/// owns the draggable dividers; it reports focus and divider moves back so the strips follow.
/// </summary>
internal partial class DeckPane : UserControl
{
    internal sealed class DeckTab : INotifyPropertyChanged
    {
        public TerminalView View { get; }
        public DeckTab(TerminalView v) { View = v; }

        public string Title => StripMark(View.Title);
        public string Glyph => View.Host.Provider switch { "Codex" => "◆", "Shell" => ">", _ => "✳" };

        /// <summary>Claude titles its own window "✳ …"; the tab already leads with that mark.</summary>
        internal static string StripMark(string t)
        {
            t = t.Trim();
            foreach (var mark in new[] { "✳", "◆", "✻" })
                if (t.StartsWith(mark, StringComparison.Ordinal)) return t[mark.Length..].TrimStart();
            if (t.Contains('\\') && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.GetFileNameWithoutExtension(t);
            return t;
        }
        public Brush GlyphBrush =>
            Application.Current.TryFindResource(View.Host.Provider == "Codex" ? "CodexMarkBrush" : "ClaudeMarkBrush") as Brush ?? Brushes.Gray;
        public Visibility ExitedVisibility => View.Exited ? Visibility.Visible : Visibility.Collapsed;
        public string Tooltip => $"{StripMark(View.Title)}\n{View.Host.Cwd}\n{View.Host.Provider} · session {View.Host.SessionId}\nhost pid {View.Host.HostPid} · child pid {View.Host.ChildPid}";

        bool _active;
        /// <summary>The tab its group is showing.</summary>
        public bool IsActive
        {
            get => _active;
            set { if (_active != value) { _active = value; Raise(nameof(IsActive)); } }
        }

        bool _foreground;
        /// <summary>The active tab of the focused group: where typing goes.</summary>
        public bool IsForeground
        {
            get => _foreground;
            set { if (_foreground != value) { _foreground = value; Raise(nameof(IsForeground)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Every tab, in column order then strip order. Kept for callers that only care which hosts are open.</summary>
    public ObservableCollection<DeckTab> Tabs { get; } = new();
    readonly DeckModel<DeckTab> _model = new(t => t.View.Host);
    List<DeckGroup<DeckTab>> Groups => _model.Groups;
    readonly DeckBrowser _browser;
    bool _dark = true;
    Point _dragStart;
    DeckTab? _dragCandidate;

    public event Action? TabsChanged;

    /// <summary>Columns, tabs, active tabs or widths changed; the window persists <see cref="Layout"/>.</summary>
    public event Action? LayoutChanged;

    /// <summary>The tab's menu asked for a restart; the window owns the resume logic.</summary>
    public event Action<DeckTab>? RestartRequested;

    /// <summary>A column's "+" asked for a new session there: "claude", "codex" or "shell". The column is focused first.</summary>
    public event Action<string>? NewSessionRequested;

    void RequestNew(object sender, string kind)
    {
        if (sender is FrameworkElement { Tag: DeckGroup<DeckTab> g } && Groups.Contains(g)) { _model.Focused = Groups.IndexOf(g); Mark(); }
        NewSessionRequested?.Invoke(kind);
    }

    void NewInGroup_Click(object sender, RoutedEventArgs e) => RequestNew(sender, "claude");
    void NewInGroup_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu }) { menu.PlacementTarget = (UIElement)sender; Dispatcher.BeginInvoke(() => menu.IsOpen = true); }
        e.Handled = true;
    }
    void NewClaudeMenu_Click(object sender, RoutedEventArgs e) => RequestNew(sender, "claude");
    void NewCodexMenu_Click(object sender, RoutedEventArgs e) => RequestNew(sender, "codex");
    void NewShellMenu_Click(object sender, RoutedEventArgs e) => RequestNew(sender, "shell");

    public DeckPane()
    {
        InitializeComponent();
        DataContext = this;
        _browser = new DeckBrowser(_dark) { Margin = new Thickness(3, 0, 6, 6) };
        _browser.Message += Route;
        _browser.Ready += ReopenAll;
        Body.Children.Add(_browser);
        Loaded += (_, _) => _browser.Start();
    }

    public bool HasTabs => Tabs.Count > 0;
    public DeckTab? Active => FocusedGroup?.Active;
    public IEnumerable<HostRecord> OpenHosts => Tabs.Select(t => t.View.Host);

    DeckGroup<DeckTab>? FocusedGroup => _model.FocusedGroup;
    DeckGroup<DeckTab>? GroupOf(DeckTab tab) => _model.GroupOf(tab);

    public DeckTab? FindBySession(string sessionId) =>
        string.IsNullOrEmpty(sessionId) ? null
        : Tabs.FirstOrDefault(t => string.Equals(t.View.Host.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    public DeckTab? FindByHost(string hostId) => _model.FindByHost(hostId);

    void Route(string hostId, string type, JsonElement root)
    {
        if (type == "fractions")
        {
            var fracs = root.GetProperty("fracs").EnumerateArray().Select(f => f.GetDouble()).ToList();
            for (int i = 0; i < Groups.Count && i < fracs.Count; i++) Groups[i].Fraction = fracs[i];
            BuildStrips();
            LayoutChanged?.Invoke();
            return;
        }
        var tab = FindByHost(hostId);
        if (tab == null) return;
        if (type == "dropTab")
        {
            int gi = Math.Clamp(root.GetProperty("group").GetInt32(), 0, Math.Max(0, Groups.Count - 1));
            switch (root.GetProperty("side").GetString())
            {
                case "left": SplitAt(tab, gi, gi); break;
                case "right": SplitAt(tab, gi + 1, gi); break;
                default: if (Groups.Count > 0) MoveTo(tab, Groups[gi], Groups[gi].Tabs.Count); break;
            }
            return;
        }
        if (type == "focused")
        {
            var g = GroupOf(tab);
            if (g != null && Groups.IndexOf(g) != _model.Focused) { _model.Focused = Groups.IndexOf(g); Mark(); LayoutChanged?.Invoke(); }
            return;
        }
        tab.View.OnMessage(type, root);
    }

    /// <summary>The page has (re)loaded: hand it every open tab again and lay the columns out.</summary>
    void ReopenAll()
    {
        foreach (var t in Tabs) t.View.Open();
        SendLayout();
    }

    // ---------------- layout: model → strips + page ----------------

    void SendLayout()
    {
        _browser.Post(new
        {
            type = "layout",
            groups = Groups.Select(g => new { id = g.Active?.View.Host.Id, frac = g.Fraction }).ToArray(),
            focused = _model.Focused,
        });
    }

    void Mark()
    {
        for (int i = 0; i < Groups.Count; i++)
            foreach (var t in Groups[i].Tabs)
            {
                t.IsActive = t == Groups[i].Active;
                t.IsForeground = t.IsActive && i == _model.Focused;
            }
    }

    void RebuildFlat()
    {
        Tabs.Clear();
        foreach (var t in _model.AllTabs) Tabs.Add(t);
    }

    /// <summary>One strip per group, widths as star shares matching the page's columns, a hairline between.</summary>
    void BuildStrips()
    {
        StripGrid.Children.Clear();
        StripGrid.ColumnDefinitions.Clear();
        var template = (DataTemplate)FindResource("GroupStripTemplate");
        for (int i = 0; i < Groups.Count; i++)
        {
            if (i > 0)
            {
                StripGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                var line = new Border { Background = (Brush)FindResource("Border2Brush"), Width = 1 };
                Grid.SetColumn(line, StripGrid.ColumnDefinitions.Count - 1);
                StripGrid.Children.Add(line);
            }
            StripGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.05, Groups[i].Fraction), GridUnitType.Star) });
            var strip = new ContentPresenter { Content = Groups[i], ContentTemplate = template };
            Grid.SetColumn(strip, StripGrid.ColumnDefinitions.Count - 1);
            StripGrid.Children.Add(strip);
        }
    }

    /// <summary>The current columns, for saving; <paramref name="sessionOf"/> names the conversation each host runs now.</summary>
    public DeckLayout LayoutFor(Func<HostRecord, string> sessionOf, Func<HostRecord, bool> isLive) => _model.Snapshot(sessionOf, isLive);

    /// <summary>Rebuild the columns from a saved layout for the hosts alive now; see <see cref="DeckModel{TTab}.Restore"/>.</summary>
    public void Restore(DeckLayout layout, IReadOnlyCollection<HostRecord> hosts)
    {
        _model.Restore(layout, hosts, NewTab);
        foreach (var t in _model.AllTabs) t.View.Open();
        Changed();
        FocusedGroup?.Active?.View.FocusTerminal();
    }

    DeckTab NewTab(HostRecord host)
    {
        var view = new TerminalView(_browser, host);
        var tab = new DeckTab(view);
        view.TitleChanged += _ => { tab.Raise(nameof(DeckTab.Title)); tab.Raise(nameof(DeckTab.Tooltip)); };
        view.ExitedChanged += _ => tab.Raise(nameof(DeckTab.ExitedVisibility));
        return tab;
    }

    void Changed()
    {
        RebuildFlat();
        Mark();
        BuildStrips();
        SendLayout();
        TabsChanged?.Invoke();
        LayoutChanged?.Invoke();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, ScrollActiveIntoView);
    }

    /// <summary>Each column's strip scrolls so its active tab is visible, as VS Code does on switch.</summary>
    void ScrollActiveIntoView()
    {
        foreach (var border in Descendants<Border>(StripGrid))
            if (border.Name == "Tab" && border.Tag is DeckTab tab && tab.IsActive) border.BringIntoView();
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>The wheel scrolls the strip sideways; there is nothing vertical to scroll.</summary>
    void Strip_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        var sv = Descendants<ScrollViewer>(d).FirstOrDefault();
        if (sv == null || sv.ScrollableWidth <= 0) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.5);
        e.Handled = true;
    }

    // ---------------- model edits ----------------

    /// <summary>Open (or focus) a tab attached to <paramref name="host"/>: back where it was if it was closed here, else in the focused column.</summary>
    public DeckTab Open(HostRecord host, bool activate = true)
    {
        var existing = FindByHost(host.Id);
        if (existing != null) { if (activate) Activate(existing); return existing; }

        var tab = NewTab(host);
        _model.Add(tab, activate);
        tab.View.Open();
        Changed();
        if (activate) tab.View.FocusTerminal();
        return tab;
    }

    /// <summary>The host behind a tab was replaced by <paramref name="host"/> (a restart); see <see cref="DeckModel{TTab}.ReplaceHost"/>.</summary>
    public void ReplaceHost(string oldHostId, HostRecord host)
    {
        if (_model.ReplaceHost(oldHostId, host, NewTab) is not { } swap) { LayoutChanged?.Invoke(); return; }
        var (old, tab) = swap;
        old.View.Close();
        tab.View.Open();
        Changed();
        if (old.IsForeground) tab.View.FocusTerminal();
    }

    /// <summary>Show the tab in its column and make that column the focused one.</summary>
    public void Activate(DeckTab tab)
    {
        if (!_model.Activate(tab)) return;
        Changed();
        tab.View.FocusTerminal();
    }

    /// <summary>Move the tab into a new column to the right of its own, taking half that column's width.</summary>
    public void SplitRight(DeckTab tab)
    {
        var from = GroupOf(tab);
        if (from == null) return;
        int i = Groups.IndexOf(from);
        SplitAt(tab, i + 1, i);
    }

    /// <summary>Move the tab into a new column at <paramref name="insertAt"/>, beside the column at <paramref name="takeFrom"/>.</summary>
    void SplitAt(DeckTab tab, int insertAt, int takeFrom)
    {
        if (!_model.SplitAt(tab, insertAt, takeFrom)) return;
        Changed();
        tab.View.FocusTerminal();
    }

    /// <summary>Move the tab into <paramref name="to"/> at <paramref name="index"/> and show it there.</summary>
    void MoveTo(DeckTab tab, DeckGroup<DeckTab> to, int index)
    {
        if (!_model.MoveTo(tab, to, index)) return;
        Changed();
        tab.View.FocusTerminal();
    }

    /// <summary>Cycle the focused column through its own tabs.</summary>
    public void CycleActive(int delta)
    {
        if (_model.CycleTarget(delta) is { } next) Activate(next);
    }

    /// <summary>Detach the tab. The host keeps running; the row on the left still knows it, and reopening puts the tab back here.</summary>
    public void Close(DeckTab tab)
    {
        if (!_model.Close(tab)) return;
        tab.View.Close();
        Changed();
        FocusedGroup?.Active?.View.FocusTerminal();
    }

    public void CloseActive()
    {
        if (Active is { } tab) Close(tab);
    }

    /// <summary>Stop the session behind the tab. The tab stays so the exit is visible; close it after.</summary>
    public void Stop(DeckTab tab) => tab.View.Kill();

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        _browser.ApplyTheme(dark);
        PaintSurface();
    }

    public void ApplyLook()
    {
        _browser.ApplyLook();
        PaintSurface();
    }

    /// <summary>
    /// The browser stops 6 px short of the right and bottom edges so the window keeps a resize grip
    /// there, and 3 px short of the left so the list splitter's handle has something to grab (a child
    /// window swallows the hit-test). Those bands are painted as terminal surface so they read as
    /// padding, not as a border.
    /// </summary>
    void PaintSurface() => Body.Background = DeckBrowser.SurfaceBrush(_dark);

    public void FocusActive() => _browser.FocusPage();

    // ---------------- strip interaction ----------------

    static DeckTab? TabOf(object sender) => sender is FrameworkElement { Tag: DeckTab tab } ? tab : null;
    static DeckGroup<DeckTab>? GroupOfStrip(object sender) => sender is FrameworkElement { DataContext: DeckGroup<DeckTab> g } ? g : null;

    void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var tab = TabOf(sender);
        if (tab == null) return;
        Activate(tab);
        _dragStart = e.GetPosition(this);
        _dragCandidate = tab;
    }

    void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var tab = _dragCandidate;
        _dragCandidate = null;
        var data = new DataObject(typeof(DeckTab), tab);
        data.SetText("sessiondeck-tab:" + tab.View.Host.Id);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && TabOf(sender) is { } tab) Close(tab);
    }

    /// <summary>Drop on a tab: land before or after it, in that tab's column.</summary>
    void Tab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DeckTab)) is not DeckTab dragged || TabOf(sender) is not { } target) return;
        var group = GroupOf(target);
        if (group == null) return;
        int to = group.Tabs.IndexOf(target);
        bool after = sender is FrameworkElement fe && e.GetPosition(fe).X > fe.ActualWidth / 2;
        if (GroupOf(dragged) == group)
        {
            int from = group.Tabs.IndexOf(dragged);
            if (after && to < from) to++;
            else if (!after && to > from) to--;
        }
        else if (after) to++;
        MoveTo(dragged, group, to);
        e.Handled = true;
    }

    void Strip_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DeckTab)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Drop on a strip's empty space: append to that column.</summary>
    void Strip_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DeckTab)) is DeckTab dragged && GroupOfStrip(sender) is { } group)
            MoveTo(dragged, group, group.Tabs.Count);
        e.Handled = true;
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Close(tab);
        e.Handled = true;
    }

    void CloseTabMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) Close(tab); }
    /// <summary>Tabs only; every session keeps running.</summary>
    void CloseOthersMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } keep) foreach (var t in Tabs.Where(t => t != keep).ToList()) Close(t); }
    void CloseAllMenu_Click(object sender, RoutedEventArgs e) { foreach (var t in Tabs.ToList()) Close(t); }
    void StopMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) { Stop(tab); Close(tab); } }
    void RestartMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) RestartRequested?.Invoke(tab); }
    void SplitMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) SplitRight(tab); }
    void CopyIdMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) TrySetClipboard(tab.View.Host.SessionId); }
    void CopyCwdMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) TrySetClipboard(tab.View.Host.Cwd); }

    static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }
}
