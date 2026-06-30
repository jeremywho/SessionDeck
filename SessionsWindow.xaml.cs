using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeSessionMonitor;

internal partial class SessionsWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly App _app;
    readonly DispatcherTimer _timer;
    readonly DispatcherTimer _pushTimer;
    FileSystemWatcher? _watcher;
    volatile SessionInfo[] _sessionsSnapshot = Array.Empty<SessionInfo>();
    ContextMenu _columnsMenu = new();

    public ObservableCollection<SessionRow> Rows { get; } = new();
    internal bool AllowClose;

    public SessionsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        DataContext = this;

        SessionRow.ContextWindow = _app.Settings.ContextWindowTokens > 0 ? _app.Settings.ContextWindowTokens : 1_000_000;

        // One-time reset of the pre-redesign (wide) window size, then persist normally.
        if (_app.Settings.LayoutVersion < 1)
        {
            _app.Settings.LayoutVersion = 1;
            _app.Settings.WindowWidth = 0;
            _app.Settings.WindowHeight = 0;
            _app.Settings.Save();
        }
        if (_app.Settings.WindowWidth > 300) Width = _app.Settings.WindowWidth;
        if (_app.Settings.WindowHeight > 200) Height = _app.Settings.WindowHeight;
        RestorePosition();

        Topmost = _app.Settings.AlwaysOnTop;
        UpdateThemeButton();
        UpdateOnTopButton();
        SetZoom(_app.Settings.Zoom);

        InitColumns();
        SetupSort();

        // Fallback poll — catches the idle-time counters and anything the watcher misses; the
        // FileSystemWatcher (below) drives the real-time updates, so this can be slow.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        // Debounce: coalesce a burst of change events into one refresh ~120ms later.
        _pushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _pushTimer.Tick += (_, _) => { _pushTimer.Stop(); Refresh(); };

        StartWatcher();
        Refresh();
        StartDesktopResolver();
    }

    /// <summary>Coalesce a burst of change events into one refresh ~120ms later.</summary>
    public void PushRefresh() { if (!_pushTimer.IsEnabled) _pushTimer.Start(); }

    /// <summary>
    /// Watch the sessions registry dir: Claude rewrites &lt;pid&gt;.json on every status change (and
    /// heartbeat), so this gives change-driven, near-instant updates at ~0 idle CPU — no polling loop,
    /// no hooks, no edits to the user's files. The 2s fallback timer covers anything the watcher drops.
    /// </summary>
    void StartWatcher()
    {
        try
        {
            var dir = SessionScanner.SessionsDirectory;
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Dispatcher.InvokeAsync(() => PushRefresh());
            _watcher.Created += (_, _) => Dispatcher.InvokeAsync(() => PushRefresh());
            _watcher.Deleted += (_, _) => Dispatcher.InvokeAsync(() => PushRefresh());
            _watcher.Renamed += (_, _) => Dispatcher.InvokeAsync(() => PushRefresh());
        }
        catch { }   // best-effort; the fallback poll still works without it
    }

    // --- virtual-desktop resolver: tag each row with the desktop its terminal window is on ---

    /// <summary>
    /// Background STA loop: map each session to its terminal window (UI Automation) and that window's
    /// virtual desktop, then tag the rows. Throttled — desktop moves are rare — and off the UI thread
    /// because the resolution is UIA-heavy. Background thread, so it dies with the process.
    /// </summary>
    void StartDesktopResolver()
    {
        var t = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try
                {
                    var snap = _sessionsSnapshot;
                    if (snap.Length > 0)
                    {
                        var map = ResolveDesktops(snap);
                        Dispatcher.InvokeAsync(() => ApplyDesktops(map));
                    }
                }
                catch { }
                System.Threading.Thread.Sleep(8000);
            }
        }) { IsBackground = true, Name = "vd-resolver" };
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
    }

    static Dictionary<string, (int Index, bool Current)> ResolveDesktops(SessionInfo[] sessions)
    {
        var result = new Dictionary<string, (int, bool)>();
        var windows = WindowActivator.ResolveWindows(sessions);
        if (windows.Count == 0) return result;
        var (order, current) = VirtualDesktop.Layout();
        foreach (var kv in windows)
        {
            var g = VirtualDesktop.DesktopOf(kv.Value);
            if (g == Guid.Empty) continue;
            result[kv.Key] = (order.IndexOf(g), g == current);
        }
        return result;
    }

    void ApplyDesktops(Dictionary<string, (int Index, bool Current)> map)
    {
        foreach (var row in Rows)
        {
            if (map.TryGetValue(row.SessionId, out var d)) row.SetDesktop(d.Index, d.Current);
            else row.SetDesktop(-1, true);
        }
    }

    // --- Content zoom: Ctrl + mouse wheel (like a browser), Ctrl+0 resets. ---
    double _zoom = 1.0;

    void SetZoom(double z)
    {
        _zoom = Math.Round(Math.Clamp(z <= 0 ? 1.0 : z, 0.6, 2.5), 2);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        _app.Settings.Zoom = _zoom;   // persisted with the rest on close
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SetZoom(_zoom + (e.Delta > 0 ? 0.1 : -0.1));
            e.Handled = true;   // don't also scroll the grid
            return;
        }
        base.OnPreviewMouseWheel(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            SetZoom(1.0);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Attention-first live sort (Error → Awaiting → Working → Completed → Idle); within each
    /// group, most-recently-changed first, then name.</summary>
    void SetupSort()
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.SortPriority), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.LastChanged), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.Name), ListSortDirection.Ascending));
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(SessionRow.SortPriority));
        view.LiveSortingProperties.Add(nameof(SessionRow.LastChanged));
        view.LiveSortingProperties.Add(nameof(SessionRow.Name));
    }

    // ---------------- columns ----------------

    sealed record Col(string Key, string Label, DataGridColumn Column, bool Toggleable, bool DefaultVisible);
    Col[] _columns = Array.Empty<Col>();

    void InitColumns()
    {
        _columns = new[]
        {
            new Col("Glyph", "", ColGlyph, false, true),
            new Col("Session", "Session", ColSession, false, true),
            new Col("CtxPct", "Context", ColCtxPct, false, true),
            new Col("Idle", "Idle", ColIdle, false, true),
            new Col("Status", "Status", ColStatus, true, false),
            new Col("Pid", "PID", ColPid, true, false),
            new Col("Id", "Id", ColId, true, false),
            new Col("ContextTokens", "Context tokens", ColContextTokens, true, false),
            new Col("LastTool", "Last Tool", ColLastTool, true, false),
            new Col("Cwd", "CWD", ColCwd, true, false),
            new Col("Version", "Version", ColVersion, true, false),
        };

        foreach (var c in _columns)
        {
            bool vis = !c.Toggleable
                ? true
                : _app.Settings.ColumnVisible.TryGetValue(c.Key, out var v) ? v : c.DefaultVisible;
            c.Column.Visibility = vis ? Visibility.Visible : Visibility.Collapsed;
            // Only the optional (toggleable) columns persist a user-set width. Curated columns keep
            // their designed XAML widths — so the * Session stays flexible and the right columns pin right.
            if (c.Toggleable && _app.Settings.ColumnWidth.TryGetValue(c.Key, out var w) && w > 20)
                c.Column.Width = new DataGridLength(w);
        }

        ApplyColumnOrder();

        _columnsMenu = new ContextMenu();
        foreach (var c in _columns.Where(c => c.Toggleable))
        {
            var mi = new MenuItem { Header = c.Label, IsCheckable = true, IsChecked = c.Column.Visibility == Visibility.Visible, Tag = c };
            mi.Click += ColumnMenuItem_Click;
            _columnsMenu.Items.Add(mi);
        }

        SessionsGrid.ColumnReordered += (_, _) => { PersistLayout(); _app.Settings.Save(); };
        SessionsGrid.PreviewMouseRightButtonUp += OnHeaderRightClick;   // right-click a header to choose columns
    }

    void ApplyColumnOrder()
    {
        if (_app.Settings.ColumnOrder.Count == 0) return;
        var ordered = _columns
            .OrderBy(c => _app.Settings.ColumnOrder.TryGetValue(c.Key, out var i) ? i : int.MaxValue)
            .ToList();
        for (int i = 0; i < ordered.Count; i++)
            try { ordered[i].Column.DisplayIndex = i; } catch { /* reflow */ }
    }

    void ColumnMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        var c = (Col)mi.Tag;
        c.Column.Visibility = mi.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        _app.Settings.ColumnVisible[c.Key] = mi.IsChecked;
        _app.Settings.Save();
    }

    void OnHeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        // open the column menu only when a column header (not a cell/row) was right-clicked
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridColumnHeader) dep = VisualTreeHelper.GetParent(dep);
        if (dep is not DataGridColumnHeader) return;

        _columnsMenu.PlacementTarget = SessionsGrid;
        _columnsMenu.Placement = PlacementMode.MousePoint;
        _columnsMenu.IsOpen = true;
        e.Handled = true;
    }

    void PersistLayout()
    {
        foreach (var c in _columns)
        {
            _app.Settings.ColumnOrder[c.Key] = c.Column.DisplayIndex;
            if (c.Toggleable && c.Column.ActualWidth > 0) _app.Settings.ColumnWidth[c.Key] = c.Column.ActualWidth;
        }
    }

    // ---------------- theme / on-top ----------------

    void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _app.ToggleTheme();
        UpdateThemeButton();
        SessionsGrid.Items.Refresh();   // re-run the Context% color converter against the new palette
    }

    void UpdateThemeButton()
    {
        bool dark = !string.Equals(_app.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        ThemeButton.Icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = dark ? Wpf.Ui.Controls.SymbolRegular.WeatherMoon24 : Wpf.Ui.Controls.SymbolRegular.WeatherSunny24
        };
        ThemeButton.ToolTip = dark ? "Theme: Dark — click for Light" : "Theme: Light — click for Dark";
    }

    void OnTopButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _app.Settings.AlwaysOnTop = Topmost;
        _app.Settings.Save();
        UpdateOnTopButton();
    }

    void UpdateOnTopButton()
    {
        OnTopButton.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24 };
        OnTopButton.Appearance = Topmost
            ? Wpf.Ui.Controls.ControlAppearance.Primary          // accented = pinned on top
            : Wpf.Ui.Controls.ControlAppearance.Transparent;     // blends into the title bar when off
        OnTopButton.ToolTip = Topmost ? "Always on top: ON (click to turn off)" : "Keep window always on top";
    }

    // ---------------- docking ----------------

    void RestorePosition()
    {
        // accept any saved position on the virtual desktop (multi-monitor)
        if (_app.Settings.WindowLeft is double l && _app.Settings.WindowTop is double t &&
            l >= SystemParameters.VirtualScreenLeft - 100 &&
            t >= SystemParameters.VirtualScreenTop - 100 &&
            l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            Left = l;
            Top = t;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
    }

    /// <summary>Work area (in DIPs) of the monitor the window is currently on.</summary>
    Rect CurrentScreenWorkArea()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;   // device pixels
                var dpi = VisualTreeHelper.GetDpi(this);
                return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY,
                                wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
            }
        }
        catch { }
        return SystemParameters.WorkArea;   // fallback: primary
    }

    /// <summary>One-shot snap to a corner/edge on the current monitor, then remember the position.</summary>
    public void SnapTo(string pos)
    {
        var wa = CurrentScreenWorkArea();
        switch (pos)
        {
            case "LeftEdge":    Left = wa.Left;          Top = wa.Top;             Height = wa.Height; break;
            case "RightEdge":   Left = wa.Right - Width; Top = wa.Top;             Height = wa.Height; break;
            case "TopLeft":     Left = wa.Left;          Top = wa.Top;             break;
            case "TopRight":    Left = wa.Right - Width; Top = wa.Top;             break;
            case "BottomLeft":  Left = wa.Left;          Top = wa.Bottom - Height; break;
            case "BottomRight": Left = wa.Right - Width; Top = wa.Bottom - Height; break;
            default: return;
        }
        SavePosition();
    }

    void SavePosition()
    {
        _app.Settings.WindowWidth = Width;
        _app.Settings.WindowHeight = Height;
        _app.Settings.WindowLeft = Left;
        _app.Settings.WindowTop = Top;
        _app.Settings.Save();
    }

    // ---------------- data ----------------

    void Refresh()
    {
        var live = SessionScanner.Scan();
        var seen = new HashSet<string>();
        foreach (var s in live)
        {
            seen.Add(s.SessionId);
            var row = Rows.FirstOrDefault(r => r.SessionId == s.SessionId);
            if (row == null) Rows.Add(new SessionRow(s));
            else row.Update(s);
        }
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!seen.Contains(Rows[i].SessionId)) Rows.RemoveAt(i);

        // keep the restore registry in sync with the live interactive set
        SessionRegistry.Snapshot(live.Where(s => s.Kind == "interactive").ToList());
        _sessionsSnapshot = live.ToArray();

        LiveLabel.Text = $"{live.Count} live session{(live.Count == 1 ? "" : "s")}";
    }

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not SessionRow row) return;
        if (!WindowActivator.Activate(row.Info))
            LiveLabel.Text = $"Could not find a window for PID {row.Info.Pid}.";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _app.Settings.WindowWidth = Width;
        _app.Settings.WindowHeight = Height;
        _app.Settings.WindowLeft = Left;
        _app.Settings.WindowTop = Top;
        PersistLayout();
        _app.Settings.Save();

        if (!AllowClose) { e.Cancel = true; Hide(); return; }
        base.OnClosing(e);
    }
}
