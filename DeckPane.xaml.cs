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
        public string Glyph => View.Host.Provider == "Codex" ? "◆" : "✳";

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

    /// <summary>A column: its tabs, the one it shows, and its share of the width.</summary>
    internal sealed class DeckGroup
    {
        public ObservableCollection<DeckTab> Tabs { get; } = new();
        public DeckTab? Active { get; set; }
        public double Fraction { get; set; } = 1;
    }

    /// <summary>Every tab, in column order then strip order. Kept for callers that only care which hosts are open.</summary>
    public ObservableCollection<DeckTab> Tabs { get; } = new();
    internal List<DeckGroup> Groups { get; } = new();
    readonly DeckBrowser _browser;
    int _focused;
    bool _dark = true;
    Point _dragStart;
    DeckTab? _dragCandidate;

    public event Action? TabsChanged;

    /// <summary>Columns, tabs, active tabs or widths changed; the window persists <see cref="Layout"/>.</summary>
    public event Action? LayoutChanged;

    /// <summary>The tab's menu asked for a restart; the window owns the resume logic.</summary>
    public event Action<DeckTab>? RestartRequested;

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

    DeckGroup? FocusedGroup => Groups.Count == 0 ? null : Groups[Math.Clamp(_focused, 0, Groups.Count - 1)];
    DeckGroup? GroupOf(DeckTab tab) => Groups.FirstOrDefault(g => g.Tabs.Contains(tab));

    public DeckTab? FindBySession(string sessionId) =>
        string.IsNullOrEmpty(sessionId) ? null
        : Tabs.FirstOrDefault(t => string.Equals(t.View.Host.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    public DeckTab? FindByHost(string hostId) =>
        Tabs.FirstOrDefault(t => t.View.Host.Id == hostId);

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
            if (g != null && Groups.IndexOf(g) != _focused) { _focused = Groups.IndexOf(g); Mark(); }
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
            focused = _focused,
        });
    }

    void Mark()
    {
        for (int i = 0; i < Groups.Count; i++)
            foreach (var t in Groups[i].Tabs)
            {
                t.IsActive = t == Groups[i].Active;
                t.IsForeground = t.IsActive && i == _focused;
            }
    }

    void RebuildFlat()
    {
        Tabs.Clear();
        foreach (var g in Groups) foreach (var t in g.Tabs) Tabs.Add(t);
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

    /// <summary>The current columns, for saving.</summary>
    public DeckLayout Layout => new()
    {
        Focused = _focused,
        Columns = Groups.Select(g => new DeckColumn
        {
            Hosts = g.Tabs.Select(t => t.View.Host.Id).ToList(),
            Active = g.Active?.View.Host.Id ?? "",
            Fraction = g.Fraction,
        }).ToList(),
    };

    /// <summary>
    /// Rebuild the columns from a saved layout for the hosts still alive. Hosts the layout does not
    /// mention go into the last column; columns left with no live host are dropped.
    /// </summary>
    public void Restore(DeckLayout layout, IEnumerable<HostRecord> hosts)
    {
        var byId = hosts.ToDictionary(h => h.Id);
        var placed = new HashSet<string>();
        Groups.Clear();
        foreach (var col in layout.Columns)
        {
            var g = new DeckGroup { Fraction = col.Fraction > 0 ? col.Fraction : 1 };
            foreach (var id in col.Hosts)
                if (byId.TryGetValue(id, out var host) && placed.Add(id)) g.Tabs.Add(NewTab(host));
            if (g.Tabs.Count == 0) continue;
            g.Active = g.Tabs.FirstOrDefault(t => t.View.Host.Id == col.Active) ?? g.Tabs[^1];
            Groups.Add(g);
        }
        var rest = hosts.Where(h => !placed.Contains(h.Id)).ToList();
        if (rest.Count > 0)
        {
            if (Groups.Count == 0) Groups.Add(new DeckGroup());
            var last = Groups[^1];
            foreach (var host in rest) last.Tabs.Add(NewTab(host));
            last.Active ??= last.Tabs[^1];
        }
        _focused = Math.Clamp(layout.Focused, 0, Math.Max(0, Groups.Count - 1));
        foreach (var g in Groups) foreach (var t in g.Tabs) t.View.Open();
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

    /// <summary>Open (or focus) a tab attached to <paramref name="host"/>, in the focused column.</summary>
    public DeckTab Open(HostRecord host, bool activate = true)
    {
        var existing = FindByHost(host.Id);
        if (existing != null) { if (activate) Activate(existing); return existing; }

        var tab = NewTab(host);
        if (Groups.Count == 0) Groups.Add(new DeckGroup());
        var group = FocusedGroup!;
        group.Tabs.Add(tab);
        tab.View.Open();
        if (activate || group.Active == null) { group.Active = tab; _focused = Groups.IndexOf(group); }
        Changed();
        if (activate) tab.View.FocusTerminal();
        return tab;
    }

    /// <summary>Show the tab in its column and make that column the focused one.</summary>
    public void Activate(DeckTab tab)
    {
        var group = GroupOf(tab);
        if (group == null) return;
        group.Active = tab;
        _focused = Groups.IndexOf(group);
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

    /// <summary>
    /// Move the tab into a new column inserted at <paramref name="insertAt"/>, taking half the width of
    /// the column at <paramref name="takeFrom"/> (the one the tab was dropped beside). A tab that was
    /// alone in its column just moves that column.
    /// </summary>
    void SplitAt(DeckTab tab, int insertAt, int takeFrom)
    {
        var from = GroupOf(tab);
        if (from == null) return;
        var donor = takeFrom >= 0 && takeFrom < Groups.Count ? Groups[takeFrom] : from;
        if (from.Tabs.Count == 1 && donor == from) { Activate(tab); return; }
        var to = new DeckGroup();
        Groups.Insert(Math.Clamp(insertAt, 0, Groups.Count), to);
        Detach(tab, from);
        if (Groups.Contains(donor) && donor != from) { to.Fraction = donor.Fraction / 2; donor.Fraction /= 2; }
        else if (Groups.Contains(from)) { to.Fraction = from.Fraction / 2; from.Fraction /= 2; }
        else to.Fraction = from.Fraction;
        to.Tabs.Add(tab);
        to.Active = tab;
        _focused = Groups.IndexOf(to);
        Changed();
        tab.View.FocusTerminal();
    }

    /// <summary>Move the tab into <paramref name="to"/> at <paramref name="index"/> and show it there.</summary>
    void MoveTo(DeckTab tab, DeckGroup to, int index)
    {
        var from = GroupOf(tab);
        if (from == null) return;
        if (from == to)
        {
            int cur = to.Tabs.IndexOf(tab);
            index = Math.Clamp(index, 0, to.Tabs.Count - 1);
            if (cur != index) to.Tabs.Move(cur, index);
        }
        else
        {
            Detach(tab, from);
            to.Tabs.Insert(Math.Clamp(index, 0, to.Tabs.Count), tab);
        }
        to.Active = tab;
        _focused = Groups.IndexOf(to);
        Changed();
        tab.View.FocusTerminal();
    }

    /// <summary>Take the tab out of its column; a column left empty goes away and its width joins a neighbour.</summary>
    void Detach(DeckTab tab, DeckGroup from)
    {
        from.Tabs.Remove(tab);
        if (from.Active == tab) from.Active = from.Tabs.LastOrDefault();
        if (from.Tabs.Count == 0 && Groups.Count > 1)
        {
            int i = Groups.IndexOf(from);
            Groups.RemoveAt(i);
            Groups[Math.Max(0, i - 1)].Fraction += from.Fraction;
            if (_focused >= Groups.Count) _focused = Groups.Count - 1;
        }
    }

    /// <summary>Cycle the focused column through its own tabs.</summary>
    public void CycleActive(int delta)
    {
        var g = FocusedGroup;
        if (g == null || g.Tabs.Count == 0) return;
        int idx = g.Active == null ? 0 : g.Tabs.IndexOf(g.Active);
        Activate(g.Tabs[((idx + delta) % g.Tabs.Count + g.Tabs.Count) % g.Tabs.Count]);
    }

    /// <summary>Detach the tab. The host keeps running; the row on the left still knows it.</summary>
    public void Close(DeckTab tab)
    {
        var group = GroupOf(tab);
        if (group == null) return;
        Detach(tab, group);
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

    public void Move(DeckTab tab, int toIndex)
    {
        var g = GroupOf(tab);
        if (g != null) MoveTo(tab, g, toIndex);
    }

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
    static DeckGroup? GroupOfStrip(object sender) => sender is FrameworkElement { DataContext: DeckGroup g } ? g : null;

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
