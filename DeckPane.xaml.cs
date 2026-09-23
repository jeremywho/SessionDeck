using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SessionDeck.Host;

namespace SessionDeck;

/// <summary>A tab strip over one <see cref="DeckBrowser"/>, which shows one attached host at a time.</summary>
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
        public bool IsActive
        {
            get => _active;
            set { if (_active != value) { _active = value; Raise(nameof(IsActive)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public ObservableCollection<DeckTab> Tabs { get; } = new();
    readonly DeckBrowser _browser;
    DeckTab? _active;
    bool _dark = true;
    Point _dragStart;
    DeckTab? _dragCandidate;

    public event Action? TabsChanged;

    public DeckPane()
    {
        InitializeComponent();
        DataContext = this;
        _browser = new DeckBrowser(_dark) { Margin = new Thickness(0, 0, 6, 6) };
        _browser.Message += Route;
        _browser.Ready += ReopenAll;
        Body.Children.Add(_browser);
        Loaded += (_, _) => _browser.Start();
    }

    public bool HasTabs => Tabs.Count > 0;
    public DeckTab? Active => _active;
    public IEnumerable<HostRecord> OpenHosts => Tabs.Select(t => t.View.Host);

    public DeckTab? FindBySession(string sessionId) =>
        string.IsNullOrEmpty(sessionId) ? null
        : Tabs.FirstOrDefault(t => string.Equals(t.View.Host.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    public DeckTab? FindByHost(string hostId) =>
        Tabs.FirstOrDefault(t => t.View.Host.Id == hostId);

    void Route(string hostId, string type, JsonElement root) => FindByHost(hostId)?.View.OnMessage(type, root);

    /// <summary>The page has (re)loaded: hand it every open tab again and put the active one back.</summary>
    void ReopenAll()
    {
        foreach (var t in Tabs) t.View.Open();
        _active?.View.Show();
    }

    /// <summary>Open (or focus) a tab attached to <paramref name="host"/>.</summary>
    public DeckTab Open(HostRecord host, bool activate = true)
    {
        var existing = FindByHost(host.Id);
        if (existing != null) { if (activate) Activate(existing); return existing; }

        var view = new TerminalView(_browser, host);
        var tab = new DeckTab(view);
        view.TitleChanged += _ => { tab.Raise(nameof(DeckTab.Title)); tab.Raise(nameof(DeckTab.Tooltip)); };
        view.ExitedChanged += _ => tab.Raise(nameof(DeckTab.ExitedVisibility));
        Tabs.Add(tab);
        view.Open();
        if (activate || _active == null) Activate(tab);
        TabsChanged?.Invoke();
        return tab;
    }

    public void Activate(DeckTab tab)
    {
        if (_active == tab) { tab.View.FocusTerminal(); return; }
        _active = tab;
        foreach (var t in Tabs) t.IsActive = t == tab;
        tab.View.Show();
        tab.View.FocusTerminal();
    }

    public void CycleActive(int delta)
    {
        if (Tabs.Count == 0) return;
        int idx = _active == null ? 0 : Tabs.IndexOf(_active);
        Activate(Tabs[((idx + delta) % Tabs.Count + Tabs.Count) % Tabs.Count]);
    }

    /// <summary>Detach the tab. The host keeps running; the row on the left still knows it.</summary>
    public void Close(DeckTab tab)
    {
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        Tabs.RemoveAt(idx);
        tab.View.Close();
        if (_active == tab)
        {
            _active = null;
            if (Tabs.Count > 0) Activate(Tabs[Math.Min(idx, Tabs.Count - 1)]);
        }
        TabsChanged?.Invoke();
    }

    public void CloseActive()
    {
        if (_active != null) Close(_active);
    }

    /// <summary>Stop the session behind the tab. The tab stays so the exit is visible; close it after.</summary>
    public void Stop(DeckTab tab) => tab.View.Kill();

    public void Move(DeckTab tab, int toIndex)
    {
        int from = Tabs.IndexOf(tab);
        if (from < 0) return;
        toIndex = Math.Clamp(toIndex, 0, Tabs.Count - 1);
        if (from == toIndex) return;
        Tabs.Move(from, toIndex);
        TabsChanged?.Invoke();
    }

    /// <summary>
    /// The browser stops 6 px short of the right and bottom edges so the window keeps a resize grip
    /// there (a child window swallows the hit-test). That band is painted as terminal surface so it
    /// reads as padding, not as a border.
    /// </summary>
    void PaintSurface() => Body.Background = DeckBrowser.SurfaceBrush(_dark);

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

    public void FocusActive() => _browser.FocusPage();

    static DeckTab? TabOf(object sender) => sender is FrameworkElement { Tag: DeckTab tab } ? tab : null;

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
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(DeckTab), tab), DragDropEffects.Move);
    }

    void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && TabOf(sender) is { } tab) Close(tab);
    }

    void Tab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DeckTab)) is not DeckTab dragged || TabOf(sender) is not { } target) return;
        int to = Tabs.IndexOf(target);
        if (sender is FrameworkElement fe && e.GetPosition(fe).X > fe.ActualWidth / 2 && to < Tabs.IndexOf(dragged)) to++;
        else if (sender is FrameworkElement fe2 && e.GetPosition(fe2).X <= fe2.ActualWidth / 2 && to > Tabs.IndexOf(dragged)) to--;
        Move(dragged, to);
        e.Handled = true;
    }

    void Strip_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DeckTab)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void Strip_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DeckTab)) is DeckTab dragged) Move(dragged, Tabs.Count - 1);
        e.Handled = true;
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Close(tab);
        e.Handled = true;
    }

    void CloseTabMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) Close(tab); }
    void StopMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) { Stop(tab); Close(tab); } }
    void CopyIdMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) TrySetClipboard(tab.View.Host.SessionId); }
    void CopyCwdMenu_Click(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) TrySetClipboard(tab.View.Host.Cwd); }

    static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }
}
