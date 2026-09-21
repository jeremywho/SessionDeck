using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SessionDeck.Host;

namespace SessionDeck;

/// <summary>A tab strip over a stack of <see cref="TerminalView"/>s, one per attached host.</summary>
internal partial class DeckPane : UserControl
{
    internal sealed class DeckTab : INotifyPropertyChanged
    {
        public TerminalView View { get; }
        public DeckTab(TerminalView v) { View = v; }

        public string Title => View.Title;
        public string Glyph => View.Host.Provider == "Codex" ? "◆" : "✳";
        public Brush GlyphBrush =>
            Application.Current.TryFindResource(View.Host.Provider == "Codex" ? "CodexMarkBrush" : "ClaudeMarkBrush") as Brush ?? Brushes.Gray;
        public Visibility ExitedVisibility => View.Exited ? Visibility.Visible : Visibility.Collapsed;

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
    DeckTab? _active;
    bool _dark = true;

    public event Action? TabsChanged;

    public DeckPane()
    {
        InitializeComponent();
        DataContext = this;
    }

    public bool HasTabs => Tabs.Count > 0;
    public IEnumerable<HostRecord> OpenHosts => Tabs.Select(t => t.View.Host);

    public DeckTab? FindBySession(string sessionId) =>
        Tabs.FirstOrDefault(t => string.Equals(t.View.Host.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    public DeckTab? FindByHost(string hostId) =>
        Tabs.FirstOrDefault(t => t.View.Host.Id == hostId);

    /// <summary>Open (or focus) a tab attached to <paramref name="host"/>.</summary>
    public DeckTab Open(HostRecord host)
    {
        var existing = FindByHost(host.Id);
        if (existing != null) { Activate(existing); return existing; }

        var view = new TerminalView(host, _dark) { Visibility = Visibility.Collapsed };
        var tab = new DeckTab(view);
        view.TitleChanged += _ => tab.Raise(nameof(DeckTab.Title));
        view.ExitedChanged += _ => tab.Raise(nameof(DeckTab.ExitedVisibility));
        Body.Children.Add(view);
        Tabs.Add(tab);
        Activate(tab);
        TabsChanged?.Invoke();
        return tab;
    }

    public void Activate(DeckTab tab)
    {
        if (_active == tab) { tab.View.FocusTerminal(); return; }
        foreach (var t in Tabs)
        {
            t.IsActive = t == tab;
            t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        }
        _active = tab;
        Placeholder.Visibility = Visibility.Collapsed;
        tab.View.Fit();
        tab.View.FocusTerminal();
    }

    /// <summary>Detach the tab. The host keeps running; the row on the left still knows it.</summary>
    public void Close(DeckTab tab)
    {
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        Tabs.RemoveAt(idx);
        Body.Children.Remove(tab.View);
        tab.View.Shutdown();
        if (_active == tab)
        {
            _active = null;
            if (Tabs.Count > 0) Activate(Tabs[Math.Min(idx, Tabs.Count - 1)]);
            else Placeholder.Visibility = Visibility.Visible;
        }
        TabsChanged?.Invoke();
    }

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        foreach (var t in Tabs) t.View.ApplyTheme(dark);
    }

    public void FocusActive() => _active?.View.FocusTerminal();

    void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeckTab tab }) Activate(tab);
    }

    void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { Tag: DeckTab tab }) Close(tab);
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeckTab tab }) Close(tab);
        e.Handled = true;
    }
}
