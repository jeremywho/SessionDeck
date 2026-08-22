using System.Diagnostics;
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
    public const int StatusAnimationFrameRate = 10;

    readonly App _app;
    readonly DispatcherTimer _timer;
    CredentialsWatcher? _credentialsWatcher;
    readonly SingleFlight _usagePoll;
    volatile SessionInfo[] _sessionsSnapshot = Array.Empty<SessionInfo>();
    volatile bool _windowVisible;        // read by the desktop-resolver thread
    ContextMenu _columnsMenu = new();

    public ObservableCollection<SessionRow> Rows { get; } = new();
    public bool StatusAnimationsEnabled => !DwmDiagnosticOptions.DisableAnimations;
    readonly Dictionary<string, SessionRow> _rowsById = new();
    public ObservableCollection<UsageMeter> Meters { get; } = new();
    List<UsageMeter>? _liveMeters;      // last successful API fetch; null until one lands
    DateTime _liveAt;                   // when that fetch succeeded
    object? _appliedMeters;             // value signature of what's currently mirrored into Meters
    string _accountText = "";
    string _usageState = "";
    string _accountTip = "";
    string? _accountUuid;               // last account seen; null until the first reading
    int _scanRunning;                   // one background discovery pass at a time
    long _lastScanMs;                   // adaptive input for the UIA desktop resolver

    /// <summary>
    /// How long after our last successful poll the numbers stop being presentable as current.
    /// Unlike the thresholds tried against the on-disk cache, this one is derived rather than
    /// guessed: we own the interval, so this is "two polls in a row failed".
    /// </summary>
    static readonly TimeSpan StaleAfter = UsageApi.PollInterval * 2.5;

    public SessionsWindow(App app)
    {
        _app = app;
        _usagePoll = new SingleFlight(PollUsageAsync);
        InitializeComponent();
        if (DwmDiagnosticOptions.DisableBackdrop)
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
        DataContext = this;
        SourceInitialized += (_, _) => DwmDiagnostics.Mark("window-source", $"backdrop={WindowBackdropType}; hwnd={new System.Windows.Interop.WindowInteropHelper(this).Handle}");
        Loaded += (_, _) => DwmDiagnostics.Mark("window-loaded", $"backdrop={WindowBackdropType}; size={ActualWidth:0}x{ActualHeight:0}; rows={Rows.Count}");
        IsVisibleChanged += (_, _) =>
        {
            _windowVisible = IsVisible;
            DwmDiagnostics.Mark("window-visible", IsVisible.ToString());
        };

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

        // A full discovery pass is intentionally capped at one start every five seconds. Claude
        // rewrites its registry files on heartbeats; using those events to trigger scans allowed a
        // continuously busy set of sessions to drive nearly eight full scans per second.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => RequestRefresh();
        if (!DwmDiagnosticOptions.DisableBackgroundWork)
        {
            _timer.Start();
            StartCodexProbe();
            StartDesktopResolver();
            StartUsagePolling();
            StartAccountWatcher();
        }
        else DwmDiagnostics.Mark("background-work", "disabled; one initial discovery pass only");

        // Keep one initial pass in backdrop-only mode so the visual tree is representative rather than
        // testing an empty window. Only the recurring work is disabled.
        RequestRefresh();
        DwmDiagnostics.Mark("window-config", $"backdrop={WindowBackdropType}; animations={StatusAnimationsEnabled}; animationFps={(StatusAnimationsEnabled ? StatusAnimationFrameRate : 0)}; backgroundWork={!DwmDiagnosticOptions.DisableBackgroundWork}");
    }

    /// <summary>
    /// Background loop that keeps <see cref="CodexScanner"/>'s rollout-&gt;PID map current.
    ///
    /// Codex publishes no live registry, so liveness has to be resolved by asking the OS which process
    /// holds each rollout file open — ~50ms a call, far too slow for the UI tick. This thread absorbs
    /// that cost; the scan itself then just reads the map. There's deliberately no FileSystemWatcher on
    /// the rollout tree either: Codex writes to it constantly while a turn runs, and every one of those
    /// events would trigger a tail re-read. The capped 5s poll is the right cadence here.
    /// </summary>
    void StartCodexProbe()
    {
        var t = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try
                {
                    var timer = Stopwatch.StartNew();
                    CodexScanner.Probe();
                    timer.Stop();
                    if (DwmDiagnosticOptions.Enabled || timer.ElapsedMilliseconds >= 2000)
                        PerformanceLog.Write($"codex-probe {timer.ElapsedMilliseconds}ms");
                    DwmDiagnostics.Mark("codex-probe", $"elapsedMs={timer.ElapsedMilliseconds}");
                }
                catch (Exception ex)
                {
                    DwmDiagnostics.Mark("codex-probe-error", ex.GetType().Name + ": " + ex.Message);
                }
                System.Threading.Thread.Sleep(3000);
            }
        }) { IsBackground = true, Name = "codex-probe" };
        t.Start();
    }

    // --- virtual-desktop resolver: tag each row with the desktop its terminal window is on ---

    /// <summary>
    /// Background MTA loop: map each session to its terminal window (UI Automation) and that window's
    /// virtual desktop, then tag the rows. Throttled — desktop moves are rare — and off the UI thread
    /// because the resolution is UIA-heavy. Background thread, so it dies with the process.
    /// <para>MTA on purpose, and load-bearing: UIA client calls belong on an MTA thread, and this
    /// loop used to run STA while sleeping between passes — an STA that never pumps messages, which
    /// both violates COM's pumping contract and gives a stalled UIA/COM call no way to ever
    /// complete. Skipped entirely while the window is hidden: the desktop pips this feeds aren't
    /// visible, and the UIA sweep is the app's most expensive recurring touch of other processes.</para>
    /// </summary>
    void StartDesktopResolver()
    {
        var t = new System.Threading.Thread(() =>
        {
            int delayMs = 15000;
            while (true)
            {
                System.Threading.Thread.Sleep(delayMs);
                try
                {
                    var snap = _sessionsSnapshot;
                    if (_windowVisible && snap.Length > 0 &&
                        System.Threading.Volatile.Read(ref _scanRunning) == 0)
                    {
                        var timer = Stopwatch.StartNew();
                        var map = ResolveDesktops(snap);
                        timer.Stop();
                        delayMs = DesktopResolverDelayMs(timer.ElapsedMilliseconds,
                            System.Threading.Interlocked.Read(ref _lastScanMs));
                        if (DwmDiagnosticOptions.Enabled || timer.ElapsedMilliseconds >= 2000)
                            PerformanceLog.Write($"desktop-uia {timer.ElapsedMilliseconds}ms sessions={snap.Length} next={delayMs}ms");
                        DwmDiagnostics.Mark("desktop-uia", $"elapsedMs={timer.ElapsedMilliseconds}; sessions={snap.Length}; nextMs={delayMs}");
                        Dispatcher.InvokeAsync(() => ApplyDesktops(map));
                    }
                    else delayMs = 5000; // hidden, empty, or scanning: cheap retry without touching UIA
                }
                catch (Exception ex)
                {
                    DwmDiagnostics.Mark("desktop-uia-error", ex.GetType().Name + ": " + ex.Message);
                }
            }
        }) { IsBackground = true, Name = "vd-resolver" };
        t.Start();
    }

    internal static int DesktopResolverDelayMs(long uiaMs, long scanMs) =>
        uiaMs >= 5000 || scanMs >= 3000 ? 60000 :
        uiaMs >= 2000 || scanMs >= 1000 ? 30000 : 15000;

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

    /// <summary>
    /// Discover and enrich sessions away from the dispatcher. If a slow pass is still running when
    /// the timer ticks, that tick is dropped so scans never overlap or build a backlog.
    /// </summary>
    async void RequestRefresh()
    {
        if (System.Threading.Interlocked.Exchange(ref _scanRunning, 1) == 1) return;
        try
        {
            var live = await System.Threading.Tasks.Task.Run(() =>
            {
                // Same list, same sort — told apart by the provider mark. Headless threads are folded
                // onto their interactive owner because they have no terminal of their own.
                var total = Stopwatch.StartNew();
                var phase = Stopwatch.StartNew();
                var claude = SessionScanner.Scan();
                long claudeMs = phase.ElapsedMilliseconds;
                phase.Restart();
                var codex = CodexScanner.Scan();
                long codexMs = phase.ElapsedMilliseconds;
                phase.Restart();
                var parents = Native.BuildParentMap();
                long processesMs = phase.ElapsedMilliseconds;
                phase.Restart();
                var found = CodexAttribution.Fold(claude, codex, parents);
                long attributionMs = phase.ElapsedMilliseconds;
                phase.Restart();
                SessionRegistry.Snapshot(found.Where(IsRestorable).ToList());
                long registryMs = phase.ElapsedMilliseconds;
                total.Stop();
                System.Threading.Interlocked.Exchange(ref _lastScanMs, total.ElapsedMilliseconds);
                if (DwmDiagnosticOptions.Enabled || total.ElapsedMilliseconds >= 1000)
                    PerformanceLog.Write($"session-scan {total.ElapsedMilliseconds}ms claude={claudeMs} codex={codexMs} " +
                        $"processes={processesMs} attribution={attributionMs} registry={registryMs} sessions={found.Count}");
                DwmDiagnostics.Mark("session-scan", $"elapsedMs={total.ElapsedMilliseconds}; claudeMs={claudeMs}; codexMs={codexMs}; processesMs={processesMs}; attributionMs={attributionMs}; registryMs={registryMs}; sessions={found.Count}");
                return found;
            });
            var apply = Stopwatch.StartNew();
            var changes = ApplyRefresh(live);
            apply.Stop();
            if (DwmDiagnosticOptions.Enabled || apply.ElapsedMilliseconds >= 250)
                PerformanceLog.Write($"ui-apply {apply.ElapsedMilliseconds}ms sessions={live.Count}");
            DwmDiagnostics.Mark("ui-apply", $"elapsedMs={apply.ElapsedMilliseconds}; sessions={live.Count}; added={changes.Added}; removed={changes.Removed}; propertyChanges={changes.PropertyChanges}");
        }
        catch (Exception ex)
        {
            // Best-effort: retain the last good snapshot after a transient read failure. Diagnostic
            // launches still need the reason, otherwise a stalled/failed scanner is indistinguishable
            // from a quiet one in the correlation log.
            DwmDiagnostics.Mark("refresh-error", ex.GetType().Name + ": " + ex.Message);
        }
        finally { System.Threading.Interlocked.Exchange(ref _scanRunning, 0); }
    }

    /// <summary>Apply a completed discovery snapshot to WPF-bound state on the dispatcher.</summary>
    (int Added, int Removed, int PropertyChanges) ApplyRefresh(List<SessionInfo> live)
    {
        var seen = new HashSet<string>();
        int added = 0;
        int removed = 0;
        int propertyChanges = 0;
        foreach (var s in live)
        {
            seen.Add(s.SessionId);
            if (_rowsById.TryGetValue(s.SessionId, out var row)) propertyChanges += row.Update(s);
            else { row = new SessionRow(s); _rowsById[s.SessionId] = row; Rows.Add(row); added++; }
        }
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!seen.Contains(Rows[i].SessionId))
            {
                _rowsById.Remove(Rows[i].SessionId);
                Rows.RemoveAt(i);
                removed++;
            }

        _sessionsSnapshot = live.ToArray();

        // The count stays short and the provider split goes in the tooltip: the legend beside it
        // already runs to the window edge at the 460px minimum, so a longer label overlaps it.
        int codex = live.Count(s => s.Provider == SessionProvider.Codex);
        LiveLabel.Text = $"{live.Count} live session{(live.Count == 1 ? "" : "s")}";
        LiveLabel.ToolTip = codex > 0 ? $"{live.Count - codex} Claude · {codex} Codex" : null;

        RefreshAccount();
        return (added, removed, propertyChanges);
    }

    /// <summary>Is this a session a human is sitting in front of, and could therefore want back?</summary>
    static bool IsRestorable(SessionInfo s) => s.Provider == SessionProvider.Codex
        ? s.Kind == "tui"
        : s.Kind == "interactive";

    /// <summary>Poll the usage endpoint now, then on <see cref="UsageApi.PollInterval"/>.</summary>
    void StartUsagePolling()
    {
        _ = _usagePoll.RunAsync();
        var timer = new DispatcherTimer { Interval = UsageApi.PollInterval };
        timer.Tick += (_, _) => { _ = _usagePoll.RunAsync(); };
        timer.Start();
    }

    /// <summary>
    /// Watch the credentials file so switching accounts updates the bar now rather than at the next
    /// 15-minute poll. The periodic poll is untouched — this is an extra trigger, not a replacement.
    /// Marshalled to the dispatcher on arrival, same as the sessions-dir watcher.
    /// </summary>
    void StartAccountWatcher()
    {
        _credentialsWatcher = new CredentialsWatcher(UsageApi.CredentialsFile);
        _credentialsWatcher.Changed += () => Dispatcher.InvokeAsync(OnCredentialsChanged);
        _credentialsWatcher.Start();
    }

    /// <summary>
    /// The credentials file was replaced, and by now <c>~/.claude.json</c> has caught up. Re-read the
    /// identity (which drops the previous account's numbers, see <see cref="RefreshAccount"/>) and
    /// ask the API for this account's. A failed call — an expired token right after a switch is the
    /// obvious way — leaves the ordinary fallback on screen: the config's cache, labelled `· cached`.
    /// </summary>
    void OnCredentialsChanged()
    {
        RefreshAccount();
        _ = _usagePoll.RunAsync();
    }

    async System.Threading.Tasks.Task PollUsageAsync()
    {
        var startedFor = _accountUuid;
        var meters = await UsageApi.FetchAsync();
        // A failed call keeps the previous numbers rather than blanking the bar; RefreshAccount
        // ages them, and two consecutive misses is what surfaces as stale.
        if (meters == null || meters.Count == 0) return;
        // The account changed while this was in the air, so whose numbers these are is anybody's
        // guess — the token was re-read from disk mid-switch. Throw them away; the switch queued a
        // re-run behind this one, and that call is unambiguously the new account's.
        if (!string.Equals(startedFor, _accountUuid, StringComparison.Ordinal)) return;
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

        // A different account is signed in than the numbers in hand were fetched for, so those
        // numbers are now somebody else's. Drop them: the bar falls back to whatever the config
        // caches for the new account (the switcher swaps that too), labelled `· cached`, until the
        // poll this change also triggers comes back.
        bool switched = AccountScanner.AccountChanged(_accountUuid, acct.AccountUuid);
        _accountUuid = acct.AccountUuid;
        if (switched)
        {
            _liveMeters = null;
            _liveAt = default;
            // The identity change is the trigger, wherever it was noticed — the watcher is only the
            // fast path to noticing it. Coalesced, so this can't stack with the call the watcher
            // makes for the same switch. (Set _accountUuid first: the poll reads it on entry.)
            _ = _usagePoll.RunAsync();
        }

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
        // Only act when the double-click landed on an actual row: the grid raises this for the
        // header and the empty area below the rows too, where SelectedItem is just whatever row
        // was clicked last — focusing that window would look like a misfire.
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow) dep = VisualTreeHelper.GetParent(dep);
        if (dep is not DataGridRow hit || hit.Item is not SessionRow row) return;
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

        // Tray app: the X always hides. Exit lives in the tray menu and hard-exits the process,
        // so no close path ever needs this window to genuinely close.
        e.Cancel = true;
        Hide();
    }
}
