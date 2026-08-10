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
    public ObservableCollection<UsageMeter> Meters { get; } = new();
    List<UsageMeter>? _liveMeters;      // last successful API fetch; null until one lands
    DateTime _liveAt;                   // when that fetch succeeded
    object? _appliedMeters;             // value signature of what's currently mirrored into Meters
    string _accountText = "";
    string _usageState = "";
    string _accountTip = "";

    /// <summary>
    /// How long after our last successful poll the numbers stop being presentable as current.
    /// Unlike the thresholds tried against the on-disk cache, this one is derived rather than
    /// guessed: we own the interval, so this is "two polls in a row failed".
    /// </summary>
    static readonly TimeSpan StaleAfter = UsageApi.PollInterval * 2.5;
    internal bool AllowClose;

    public SessionsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        DataContext = this;

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
        ShowInTaskbar = _app.Settings.ShowInTaskbar;
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
        StartCodexProbe();
        Refresh();
        StartDesktopResolver();
        StartUsagePolling();
    }

    /// <summary>
    /// Background loop that keeps <see cref="CodexScanner"/>'s rollout-&gt;PID map current.
    ///
    /// Codex publishes no live registry, so liveness has to be resolved by asking the OS which process
    /// holds each rollout file open — ~50ms a call, far too slow for the UI tick. This thread absorbs
    /// that cost; the scan itself then just reads the map. There's deliberately no FileSystemWatcher on
    /// the rollout tree either: Codex writes to it constantly while a turn runs, and every one of those
    /// events would trigger a tail re-read. The 2s poll is the right cadence here.
    /// </summary>
    void StartCodexProbe()
    {
        var t = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try { CodexScanner.Probe(); } catch { }
                System.Threading.Thread.Sleep(3000);
            }
        }) { IsBackground = true, Name = "codex-probe" };
        t.Start();
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

    void NewSessionButton_Click(object sender, RoutedEventArgs e) =>
        SessionLauncher.LaunchNew(null, _app.Settings.ResumeFlags, LaunchTarget.NewWindow);

    void NewTabButton_Click(object sender, RoutedEventArgs e) =>
        SessionLauncher.LaunchNew(null, _app.Settings.ResumeFlags, LaunchTarget.LastWindow);

    // Codex has no --name, so there are no named counterparts to these two — see NewCodexCommand.
    void NewCodexSessionButton_Click(object sender, RoutedEventArgs e) =>
        SessionLauncher.LaunchNewCodex(_app.Settings.CodexFlags, LaunchTarget.NewWindow);

    void NewCodexTabButton_Click(object sender, RoutedEventArgs e) =>
        SessionLauncher.LaunchNewCodex(_app.Settings.CodexFlags, LaunchTarget.LastWindow);

    void NewNamedSessionButton_Click(object sender, RoutedEventArgs e) =>
        PromptThenLaunch(LaunchTarget.NewWindow);

    void NewNamedTabButton_Click(object sender, RoutedEventArgs e) =>
        PromptThenLaunch(LaunchTarget.LastWindow);

    void PromptThenLaunch(LaunchTarget target)
    {
        var prompt = new NamePromptWindow(this);
        if (prompt.ShowDialog() == true)
            SessionLauncher.LaunchNew(prompt.SessionName, _app.Settings.ResumeFlags, target);
    }

    void SettingsButton_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();

    /// <summary>Reveal the title-bar update button once a new version is staged.</summary>
    public void ShowUpdateReady(string tag)
    {
        UpdateButton.ToolTip = $"Update to {tag} ready — click to restart";
        UpdateButton.Visibility = Visibility.Visible;
    }

    void UpdateButton_Click(object sender, RoutedEventArgs e) => _app.ApplyUpdate();

    /// <summary>Called after a theme swap so the Context% color converter re-runs against the new palette.</summary>
    public void RefreshAfterThemeChange() => SessionsGrid.Items.Refresh();

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
        // Same list, same sort — told apart by the provider mark. Headless Codex threads (companion
        // second opinions, exec runs) are folded onto the Claude session that started them rather than
        // listed separately: they have no terminal, so a row for one is a row you can't act on.
        var live = CodexAttribution.Fold(SessionScanner.Scan(), CodexScanner.Scan(), Native.BuildParentMap());
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

        // Keep the restore registry in sync with the live *interactive* set of each CLI. Codex's
        // equivalent of Claude's "interactive" is the TUI: a `codex exec` thread is a headless one-shot
        // fired by a script or an agent, so reopening one in a terminal would restart somebody's
        // automation, not restore your work.
        SessionRegistry.Snapshot(live.Where(IsRestorable).ToList());
        _sessionsSnapshot = live.ToArray();

        // The count stays short and the provider split goes in the tooltip: the legend beside it
        // already runs to the window edge at the 460px minimum, so a longer label overlaps it.
        int codex = live.Count(s => s.Provider == SessionProvider.Codex);
        LiveLabel.Text = $"{live.Count} live session{(live.Count == 1 ? "" : "s")}";
        LiveLabel.ToolTip = codex > 0 ? $"{live.Count - codex} Claude · {codex} Codex" : null;

        RefreshAccount();
    }

    /// <summary>Is this a session a human is sitting in front of, and could therefore want back?</summary>
    static bool IsRestorable(SessionInfo s) => s.Provider == SessionProvider.Codex
        ? s.Kind == "tui"
        : s.Kind == "interactive";

    /// <summary>Poll the usage endpoint now, then on <see cref="UsageApi.PollInterval"/>.</summary>
    void StartUsagePolling()
    {
        _ = PollUsageAsync();
        var timer = new DispatcherTimer { Interval = UsageApi.PollInterval };
        timer.Tick += (_, _) => { _ = PollUsageAsync(); };
        timer.Start();
    }

    async System.Threading.Tasks.Task PollUsageAsync()
    {
        var meters = await UsageApi.FetchAsync();
        // A failed call keeps the previous numbers rather than blanking the bar; RefreshAccount
        // ages them, and two consecutive misses is what surfaces as stale.
        if (meters == null || meters.Count == 0) return;
        _liveMeters = meters;
        _liveAt = DateTime.Now;
        RefreshAccount();
    }

    /// <summary>
    /// Repopulate the account/usage bar. The meters are rebuilt only when the source list actually
    /// changes — rebuilding every tick would restart the bindings under the cursor and kill any
    /// open tooltip.
    /// </summary>
    void RefreshAccount()
    {
        var acct = AccountScanner.Read();

        // Live numbers win. The on-disk cache is the fallback for a failed/never-run poll, and is
        // labelled as cached rather than passed off as current — its age can't be interpreted.
        bool live = _liveMeters != null;
        var chosen = live ? _liveMeters! : acct.Meters;

        // Codex's limits ride along in its rollout logs, so they cost nothing to add and are always
        // first-hand — no API call, no credentials, no cache to second-guess. They come last so the
        // Claude meters keep their established positions.
        var combined = new List<UsageMeter>(chosen);
        combined.AddRange(CodexScanner.PlanMeters);

        // Compared by value, not by reference: the Codex meters are rebuilt from the rollout on every
        // read, so a reference check would rebuild the bar every tick and kill any tooltip under the
        // cursor. Percentages are whole numbers, so this settles almost immediately.
        string sig = string.Join("|", combined.ConvertAll(m => $"{m.Label}:{m.Percent}:{m.Severity}"));
        if (!string.Equals(sig, _appliedMeters as string, StringComparison.Ordinal))
        {
            _appliedMeters = sig;
            Meters.Clear();
            foreach (var m in combined) Meters.Add(m);
        }

        var age = DateTime.Now - _liveAt;
        bool stale = live && age > StaleAfter;

        var text = acct.Email.Length > 0 ? acct.Email : "Not signed in";
        if (text != _accountText) { _accountText = text; AccountLabel.Text = text; }

        var state = stale ? $"· {AccountInfo.AgeText(age)} old" : !live ? "· cached" : "";
        if (state != _usageState) { _usageState = state; UsageStateLabel.Text = state; }

        var tip = acct.Email.Length > 0 ? acct.Email : "Not signed in";
        if (acct.Organization.Length > 0) tip += $"\n{acct.Organization}";
        tip += live
            ? $"\nUsage updated {_liveAt:h:mm tt}"
            : acct.FetchedAt == DateTime.MinValue
                ? "\nNo usage data available"
                : $"\nFrom Claude Code's cache, written {acct.FetchedAt:h:mm tt}";
        if (tip != _accountTip) { _accountTip = tip; AccountGroup.ToolTip = tip; }

        UsageBar.Opacity = stale ? 0.5 : 1.0;
    }

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not SessionRow row) return;
        // A background agent has no terminal window by construction — hunting for one would only
        // produce the "could not find a window" error for a row that is behaving normally.
        if (row.IsBackgroundAgent)
        {
            LiveLabel.Text = "Background agent — it has no terminal window to focus.";
            return;
        }
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
