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

namespace SessionDeck;

internal partial class SessionsWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly App _app;
    readonly DispatcherTimer _timer;
    CredentialsWatcher? _credentialsWatcher;
    readonly SingleFlight _usagePoll;
    volatile SessionInfo[] _sessionsSnapshot = Array.Empty<SessionInfo>();
    volatile bool _windowVisible;        // read by the desktop-resolver thread
    ContextMenu _columnsMenu = new();

    public ObservableCollection<SessionRow> Rows { get; } = new();
    readonly Dictionary<string, SessionRow> _rowsById = new();
    volatile SessionInfo[] _externalSnapshot = Array.Empty<SessionInfo>();
    public ObservableCollection<UsageMeter> Meters { get; } = new();
    List<UsageMeter>? _liveMeters;      // last successful API fetch; null until one lands
    DateTime _liveAt;                   // when that fetch succeeded
    CpaUsageSnapshot? _cpaSnapshot;     // last successful sanitized CPA dashboard reading
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
    static readonly TimeSpan DirectStaleAfter = UsageApi.PollInterval * 2.5;

    public SessionsWindow(App app)
    {
        _app = app;
        _usagePoll = new SingleFlight(PollUsageAsync);
        InitializeComponent();
        if (DwmDiagnosticOptions.DisableBackdrop)
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
        if (string.Equals(_app.Settings.WindowBackdrop, "Acrylic", StringComparison.OrdinalIgnoreCase))
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Acrylic;
        IsolationTelemetry.Start();
        SessionsGrid.LoadingRow += (_, _) => IsolationTelemetry.RowLoaded();
        SessionsGrid.UnloadingRow += (_, _) => IsolationTelemetry.RowUnloaded();
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
        // The app is usually ended by a kill or by the updater's relaunch, never a graceful close,
        // so geometry is saved shortly after every move or resize rather than on closing.
        var geometry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        geometry.Tick += (_, _) => { geometry.Stop(); if (WindowState == WindowState.Normal && IsLoaded) SavePosition(); };
        LocationChanged += (_, _) => { geometry.Stop(); geometry.Start(); };
        SizeChanged += (_, _) => { geometry.Stop(); geometry.Start(); };

        Topmost = _app.Settings.AlwaysOnTop;
        ShowInTaskbar = _app.Settings.ShowInTaskbar;
        UpdateOnTopButton();
        SetZoom(_app.Settings.Zoom);

        InitColumns();
        SetupSort();
        DeckBrowser.UseSettings(_app.Settings);
        Deck.ApplyTheme(!string.Equals(_app.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase));
        Loaded += (_, _) => ReattachHosts();

        if (ExperimentOptions.Mode != "normal")
            Title = $"{Title} — experiment: {ExperimentOptions.Mode}";

        // A full discovery pass is intentionally capped at one start every five seconds. Claude
        // rewrites its registry files on heartbeats; using those events to trigger scans allowed a
        // continuously busy set of sessions to drive nearly eight full scans per second.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => { RequestRefresh(); if (DateTime.UtcNow - InstalledVersions.CheckedAt > TimeSpan.FromMinutes(10)) InstalledVersions.Refresh(); };
        InstalledVersions.Changed += () => Dispatcher.BeginInvoke(RequestRefresh);
        InstalledVersions.Refresh();
        var layoutSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        layoutSave.Tick += (_, _) => { layoutSave.Stop(); _app.Settings.Deck = Deck.Layout; _app.Settings.Save(); };
        Deck.LayoutChanged += () => { layoutSave.Stop(); layoutSave.Start(); };
        Deck.NewSessionRequested += kind =>
        {
            switch (kind)
            {
                case "codex": NewCodexSessionButton_Click(this, new RoutedEventArgs()); break;
                case "shell": SpawnShellIntoDeck(); break;
                default: NewSessionButton_Click(this, new RoutedEventArgs()); break;
            }
        };
        Deck.RestartRequested += tab =>
        {
            var row = Rows.FirstOrDefault(r => r.Host.Id == tab.View.Host.Id);
            if (row != null) _ = RestartSession(row);
        };
        if (!DwmDiagnosticOptions.DisableBackgroundWork)
        {
            _timer.Start();
            StartRegistryWatcher();
            StartCodexProbe();
            if (!ExperimentOptions.DisableDesktopUia)
                StartDesktopResolver();
            StartUsagePolling();
            StartAccountWatcher();
        }
        else DwmDiagnostics.Mark("background-work", "disabled; one initial discovery pass only");

        // Keep one initial pass in backdrop-only mode so the visual tree is representative rather than
        // testing an empty window. Only the recurring work is disabled.
        RequestRefresh();
        DwmDiagnostics.Mark("window-config",
            $"backdrop={WindowBackdropType}; animations=false; " +
            $"backgroundWork={!DwmDiagnosticOptions.DisableBackgroundWork}; desktopUia={!ExperimentOptions.DisableDesktopUia}");
    }

    int _scanCount;
    long _scanTotalMs;
    long _scanMaxMs;
    long _scanWindowStart = Environment.TickCount64;

    /// <summary>One line a minute on what the refresh loop costs, so a watcher that starts firing
    /// too often shows up in the log instead of only in Task Manager.</summary>
    void NoteScan(long ms)
    {
        int n = System.Threading.Interlocked.Increment(ref _scanCount);
        System.Threading.Interlocked.Add(ref _scanTotalMs, ms);
        long max;
        do { max = System.Threading.Interlocked.Read(ref _scanMaxMs); }
        while (ms > max && System.Threading.Interlocked.CompareExchange(ref _scanMaxMs, ms, max) != max);
        long now = Environment.TickCount64;
        if (now - _scanWindowStart < 60_000) return;
        long total = System.Threading.Interlocked.Exchange(ref _scanTotalMs, 0);
        long peak = System.Threading.Interlocked.Exchange(ref _scanMaxMs, 0);
        System.Threading.Interlocked.Exchange(ref _scanCount, 0);
        _scanWindowStart = now;
        PerformanceLog.Write($"scan-rate {n}/min avg={(n > 0 ? total / n : 0)}ms max={peak}ms");
    }

    FileSystemWatcher? _registryWatcher;
    FileSystemWatcher? _hostsWatcher;
    DispatcherTimer? _registryDebounce;
    DateTime _lastWatchedRefresh;

    /// <summary>
    /// Claude rewrites <c>~/.claude/sessions/&lt;pid&gt;.json</c> the moment its status changes, so a
    /// watcher on that folder is the fastest zero-config signal there is. Events are debounced and
    /// rate-limited: a busy session heartbeats that file continuously, and the list is a handful of
    /// hosted rows, so two refreshes a second is plenty and cannot pile up (the refresh is
    /// single-flight).
    /// </summary>
    void StartRegistryWatcher()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
        if (!Directory.Exists(dir)) return;
        Directory.CreateDirectory(HostManager.HostsDir);
        _registryDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _registryDebounce.Tick += (_, _) =>
        {
            _registryDebounce.Stop();
            if (DateTime.UtcNow - _lastWatchedRefresh < TimeSpan.FromMilliseconds(500))
            {
                _registryDebounce.Start();
                return;
            }
            _lastWatchedRefresh = DateTime.UtcNow;
            RequestRefresh();
        };
        try
        {
            _registryWatcher = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler kick = (_, _) => Dispatcher.BeginInvoke(() => { _registryDebounce.Stop(); _registryDebounce.Start(); });
            _registryWatcher.Changed += kick;
            _registryWatcher.Created += kick;
            _registryWatcher.Deleted += kick;
            _registryWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _registryDebounce.Stop(); _registryDebounce.Start(); });
            _registryWatcher.EnableRaisingEvents = true;
            _hostsWatcher = new FileSystemWatcher(HostManager.HostsDir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _hostsWatcher.Changed += kick;
            _hostsWatcher.Created += kick;
            _hostsWatcher.Deleted += kick;
            _hostsWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _registryDebounce.Stop(); _registryDebounce.Start(); });
            _hostsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { App.LogError(ex); }
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
                    var stats = CodexScanner.Probe();
                    timer.Stop();
                    IsolationTelemetry.CodexProbe(timer.ElapsedMilliseconds, stats);
                    string detail =
                        $"elapsedMs={timer.ElapsedMilliseconds};files={stats.Files};" +
                        $"checks={stats.CandidateChecks + stats.VerificationChecks};" +
                        $"owned={stats.Owned};unowned={stats.Unowned};indeterminate={stats.Indeterminate};" +
                        $"forgotten={stats.Forgotten};known={stats.KnownBefore}->{stats.KnownAfter};" +
                        $"errors={stats.ErrorSummary}";
                    if (DwmDiagnosticOptions.Enabled || timer.ElapsedMilliseconds >= 2000 ||
                        stats.Indeterminate > 0 || stats.Forgotten > 0)
                        PerformanceLog.Write(
                            $"codex-probe {timer.ElapsedMilliseconds}ms files={stats.Files} " +
                            $"checks={stats.CandidateChecks + stats.VerificationChecks} " +
                            $"owned={stats.Owned} unowned={stats.Unowned} indeterminate={stats.Indeterminate} " +
                            $"forgotten={stats.Forgotten} known={stats.KnownBefore}->{stats.KnownAfter} " +
                            $"errors={stats.ErrorSummary}");
                    DwmDiagnostics.Mark("codex-probe", detail);
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
                        IsolationTelemetry.UiaSweep(timer.ElapsedMilliseconds, snap.Length, map.Count);
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
        if (ExperimentOptions.FreezeGrid) return;
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
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            SetZoom(1.0);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Tab)
        {
            Deck.CycleActive((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (ctrl && (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && e.Key == Key.W)
        {
            Deck.CloseActive();
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
        view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.GroupOrder), ListSortDirection.Ascending));
        view.LiveSortingProperties.Add(nameof(SessionRow.GroupOrder));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SessionRow.GroupName)));
        view.IsLiveGrouping = true;
        view.LiveGroupingProperties.Add(nameof(SessionRow.GroupName));
        view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.SortPriority), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.LastChanged), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.Name), ListSortDirection.Ascending));
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(SessionRow.SortPriority));
        view.LiveSortingProperties.Add(nameof(SessionRow.LastChanged));
        view.LiveSortingProperties.Add(nameof(SessionRow.Name));
        if (ExperimentOptions.TelemetryActive)
            ((System.Collections.Specialized.INotifyCollectionChanged)view).CollectionChanged +=
                (_, e) => IsolationTelemetry.CollectionChanged(e.Action);
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
            new Col("End", "", ColEnd, false, true),
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
        SessionsGrid.PreviewMouseRightButtonUp += OnHeaderRightClick;
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

    void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        string sessionId = Guid.NewGuid().ToString();
        SpawnIntoDeck(sessionId, SessionProvider.Claude, HostManager.NewClaudeCommand(sessionId, null, _app.Settings.ResumeFlags), HomeDir, null);
    }

    // Codex has no --name, so there are no named counterparts to these two — see NewCodexCommand.
    void NewCodexSessionButton_Click(object sender, RoutedEventArgs e) =>
        SpawnIntoDeck("", SessionProvider.Codex, HostManager.NewCodexCommand(_app.Settings.CodexFlags), HomeDir, null);

    void NewSessionDialogButton_Click(object sender, RoutedEventArgs e) => NewSessionDialog(SessionProvider.Claude);

    void NewSessionDialog(SessionProvider initial)
    {
        var dlg = new NewSessionWindow(this, _app.Settings, initial);
        if (dlg.ShowDialog() != true || dlg.Result is not { } r) return;
        if (r.Provider == SessionProvider.Codex)
            SpawnIntoDeck("", SessionProvider.Codex, HostManager.NewCodexCommand(_app.Settings.CodexFlags, r.Model, r.Effort), r.Cwd, null, r.Prompt);
        else
        {
            string sessionId = Guid.NewGuid().ToString();
            SpawnIntoDeck(sessionId, SessionProvider.Claude,
                HostManager.NewClaudeCommand(sessionId, r.Name, _app.Settings.ResumeFlags, r.Model, r.Effort), r.Cwd, r.Name, r.Prompt);
        }
    }

    void SettingsButton_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();

    void AdoptButton_Click(object sender, RoutedEventArgs e)
    {
        PerformanceLog.Write($"adopt-button external={_externalSnapshot.Length}");
        try
        {
            var picker = new AdoptWindow(this, _externalSnapshot);
            picker.Show();
            picker.Activate();
            PerformanceLog.Write($"adopt-picker shown visible={picker.IsVisible} left={picker.Left} top={picker.Top} w={picker.ActualWidth} h={picker.ActualHeight}");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            LiveLabel.Text = "Could not open the adopt picker: " + ex.Message;
        }
    }

    /// <summary>The X at the end of a row: end the session (host and CLI). An exited host's row is
    /// just removed. Nothing here asks — the X only appears on hover, and a stopped session can be
    /// resumed from the restore picker.</summary>
    void EndSession_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: SessionRow row }) return;
        var tab = Deck.FindByHost(row.Host.Id);
        if (row.Host.HasExited)
        {
            if (tab != null) Deck.Close(tab);
            HostManager.Forget(row.Host);
            RequestRefresh();
            return;
        }
        HostManager.Kill(row.Host);
        if (tab != null) Deck.Close(tab);
    }

    // ---------------- deck: sessions hosted inside this window ----------------

    /// <summary>Resume a saved session inside a deck tab rather than an external terminal.</summary>
    public void ResumeInDeck(SavedSession s)
    {
        string cwd = string.IsNullOrWhiteSpace(s.Cwd) ? HomeDir : s.Cwd;
        string cmd = s.Provider == SessionProvider.Codex
            ? HostManager.ResumeCodexCommand(s.Id, _app.Settings.CodexFlags)
            : SessionScanner.HasTranscript(s.Id, cwd)
                ? HostManager.ResumeClaudeCommand(s.Id, _app.Settings.ResumeFlags)
                : HostManager.NewClaudeCommand(s.Id, s.Name, _app.Settings.ResumeFlags);
        SpawnIntoDeck(s.Id, s.Provider, cmd, cwd, s.Name);
    }

    void SpawnShellIntoDeck()
    {
        try { Deck.Open(HostManager.SpawnShell(HomeDir)); }
        catch (Exception ex)
        {
            App.LogError(ex);
            LiveLabel.Text = "Could not start the terminal host: " + ex.Message;
        }
    }

    void SpawnIntoDeck(string sessionId, SessionProvider provider, string cmd, string cwd, string? title, string initialPrompt = "")
    {
        try
        {
            var host = HostManager.Spawn(sessionId, provider, cmd, cwd, title, initialPrompt);
            Deck.Open(host);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            LiveLabel.Text = "Could not start the session host: " + ex.Message;
        }
    }

    /// <summary>Hosts still running from a previous run of this app: reopen their tabs.</summary>
    void ReattachHosts()
    {
        var hosts = HostManager.Discover().OrderBy(h => h.StartedAt).ToList();
        PerformanceLog.Write($"reattach hosts={hosts.Count} ids={string.Join(",", hosts.Select(h => h.Id))} columns={_app.Settings.Deck.Columns.Count}");
        Deck.Restore(_app.Settings.Deck, hosts);
    }

    static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Reveal the title-bar update button once a new version is staged.</summary>
    public void ShowUpdateReady(string tag)
    {
        UpdateButton.ToolTip = $"Update to {tag} ready — click to restart";
        UpdateButton.Visibility = Visibility.Visible;
    }

    void UpdateButton_Click(object sender, RoutedEventArgs e) => _app.ApplyUpdate();

    /// <summary>Called after a theme swap so the Context% color converter re-runs against the new palette.</summary>
    public void RefreshAfterThemeChange()
    {
        SessionsGrid.Items.Refresh();
        Deck.ApplyTheme(!string.Equals(_app.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase));
    }

    public void RefreshTerminalLook() => Deck.ApplyLook();


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
            var scan = await System.Threading.Tasks.Task.Run(() =>
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
                var hosts = HostManager.Discover();
                long registryMs = phase.ElapsedMilliseconds;
                total.Stop();
                System.Threading.Interlocked.Exchange(ref _lastScanMs, total.ElapsedMilliseconds);
                NoteScan(total.ElapsedMilliseconds);
                if (DwmDiagnosticOptions.Enabled || total.ElapsedMilliseconds >= 1000)
                    PerformanceLog.Write($"session-scan {total.ElapsedMilliseconds}ms claude={claudeMs} codex={codexMs} " +
                        $"processes={processesMs} attribution={attributionMs} hosts={registryMs} sessions={found.Count}");
                DwmDiagnostics.Mark("session-scan", $"elapsedMs={total.ElapsedMilliseconds}; claudeMs={claudeMs}; codexMs={codexMs}; processesMs={processesMs}; attributionMs={attributionMs}; registryMs={registryMs}; sessions={found.Count}");
                return (
                    Sessions: found,
                    Hosts: hosts,
                    Parents: parents,
                    ClaudeDiscovered: claude.Count,
                    CodexDiscovered: codex.Count,
                    ClaudeRows: found.Count(s => s.Provider == SessionProvider.Claude),
                    CodexRows: found.Count(s => s.Provider == SessionProvider.Codex));
            });
            var apply = Stopwatch.StartNew();
            var changes = ApplyRefresh(scan.Sessions, scan.Hosts, scan.Parents);
            apply.Stop();
            RestartUpdatedIdleSessions();
            ApplyGroups();
            if (ExperimentOptions.TelemetryActive)
            {
                double applyMs = apply.Elapsed.TotalMilliseconds;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                    () => IsolationTelemetry.FlushRefresh(
                        scan.Sessions.Count, scan.ClaudeDiscovered, scan.CodexDiscovered,
                        scan.ClaudeRows, scan.CodexRows, applyMs));
            }
            if (DwmDiagnosticOptions.Enabled || apply.ElapsedMilliseconds >= 250)
                PerformanceLog.Write($"ui-apply {apply.ElapsedMilliseconds}ms sessions={scan.Sessions.Count}");
            DwmDiagnostics.Mark("ui-apply",
                $"elapsedMs={apply.ElapsedMilliseconds}; sessions={scan.Sessions.Count}; " +
                $"added={changes.Added}; removed={changes.Removed}; propertyChanges={changes.PropertyChanges}");
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

    /// <summary>
    /// The list is the deck's hosts, one row each, whether or not a tab is open. The machine-wide
    /// scan only enriches them: a scanned session belongs to a host when its id matches (Claude,
    /// whose id the deck chose) or when its process descends from the host's child (Codex, which
    /// names its own thread). Everything the scan found that belongs to no host is remembered for
    /// the Adopt picker and never becomes a row.
    /// </summary>
    (int Added, int Removed, int PropertyChanges) ApplyRefresh(List<SessionInfo> live, List<Host.HostRecord> hosts, Dictionary<int, int> parents)
    {
        var claimed = new HashSet<SessionInfo>();
        var byHost = new List<(Host.HostRecord Host, SessionInfo Info)>();
        foreach (var h in hosts)
        {
            SessionInfo? match = null;
            if (h.SessionId.Length > 0)
                match = live.FirstOrDefault(s => string.Equals(s.SessionId, h.SessionId, StringComparison.OrdinalIgnoreCase));
            match ??= live.FirstOrDefault(s => !claimed.Contains(s) && s.Pid > 0 && DescendsFrom(s.Pid, h.ChildPid, parents));
            if (match != null) claimed.Add(match);
            var info = match ?? SessionRow.Placeholder(h);
            ApplyHookState(h, info);
            byHost.Add((h, info));
        }

        var seen = new HashSet<string>();
        int added = 0;
        int removed = 0;
        int propertyChanges = 0;
        bool mutateGrid = !ExperimentOptions.FreezeGrid || Rows.Count == 0;
        if (mutateGrid)
        {
            foreach (var (h, s) in byHost)
            {
                seen.Add(h.Id);
                if (_rowsById.TryGetValue(h.Id, out var row)) propertyChanges += row.Update(s);
                else { row = new SessionRow(h, s); _rowsById[h.Id] = row; Rows.Add(row); added++; }
            }
            for (int i = Rows.Count - 1; i >= 0; i--)
                if (!seen.Contains(Rows[i].SessionId))
                {
                    _rowsById.Remove(Rows[i].SessionId);
                    Rows.RemoveAt(i);
                    removed++;
                }
        }

        _sessionsSnapshot = byHost.Select(x => x.Info).ToArray();
        _externalSnapshot = live.Where(s => !claimed.Contains(s)).ToArray();
        // A CLI that has exited leaves its host alive (the shell wrapper stays open), so hook status is
        // what says the session is really over; otherwise it would be offered for restore next launch.
        SessionRegistry.Snapshot(byHost.Where(x => !x.Host.HasExited && x.Host.AgentStatus != "ended" && x.Info.SessionId.Length > 0 && IsRestorable(x.Info)).Select(x => x.Info).ToList());

        int hostsLive = hosts.Count(h => !h.HasExited);
        int external = _externalSnapshot.Count(s => s.Kind != "companion");
        LiveLabel.Text = $"{hostsLive} in deck" + (external > 0 ? $" · {external} elsewhere" : "");
        LiveLabel.ToolTip = external > 0 ? "Sessions in other terminals can be brought in with Adopt" : null;

        RefreshAccount();
        return (added, removed, propertyChanges);
    }

    /// <summary>
    /// What the session's own hooks reported beats what the scanner inferred, whenever it is newer.
    /// Claude's registry is authoritative for busy/idle and lands within a second, so hooks mostly add
    /// the tool name there; for Codex the hook is the only live status there is.
    /// </summary>
    static void ApplyHookState(Host.HostRecord h, SessionInfo info)
    {
        if (h.StatusAt is not DateTime at) return;
        if (h.AgentStatus == "ended" || h.HasExited) return;
        bool newer = at > info.StatusUpdatedAt.ToUniversalTime();
        // Hosts from before protocol 2 called Claude's idle notification "waiting"; read it as idle.
        string agentStatus = h.Protocol < 2 && h.AgentStatus == "waiting" && h.LastEvent == "Notification" ? "idle" : h.AgentStatus;
        // The CLI's own registry says "shell" while background shells it started are still running;
        // the hooks only see the agent's turn end. Background work in flight outranks an idle hook.
        bool backgroundHolds = info.Status == "shell" && agentStatus is "idle" or "scheduled";
        if (agentStatus.Length > 0 && (newer || info.Status.Length == 0) && !backgroundHolds)
        {
            info.Status = agentStatus;
            info.StatusUpdatedAt = at.ToLocalTime();
        }
        if (h.LastTool.Length > 0 && (newer || info.LastTool.Length == 0)) info.LastTool = h.LastTool;
        if (info.SessionId.Length == 0 && h.SessionId.Length > 0) info.SessionId = h.SessionId;
        if (info.TranscriptPath.Length == 0 && h.TranscriptPath.Length > 0) info.TranscriptPath = h.TranscriptPath;
    }

    static bool DescendsFrom(int pid, int ancestor, Dictionary<int, int> parents)
    {
        for (int hops = 0; hops < 12 && pid > 0; hops++)
        {
            if (pid == ancestor) return true;
            if (!parents.TryGetValue(pid, out pid)) return false;
        }
        return false;
    }

    /// <summary>Is this a session a human is sitting in front of, and could therefore want back?</summary>
    static bool IsRestorable(SessionInfo s) => s.Provider == SessionProvider.Codex
        ? s.Kind == "tui"
        : s.Kind == "interactive";

    /// <summary>Poll the selected usage source now, then at that source's own cadence.</summary>
    void StartUsagePolling()
    {
        _ = _usagePoll.RunAsync();
        var interval = CpaUsageApi.IsConfigured ? CpaUsageApi.PollInterval : UsageApi.PollInterval;
        var timer = new DispatcherTimer { Interval = interval };
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
        // CPA owns account selection. Its sanitized dashboard is polled every 20s, so watching the
        // unrelated direct-login credentials would only trigger redundant work and could briefly
        // pair one identity with another account's limits.
        if (CpaUsageApi.IsConfigured) return;

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
        if (CpaUsageApi.IsConfigured)
        {
            // A failed read deliberately preserves the last good snapshot. RefreshAccount ages it
            // on every ordinary 5s session refresh instead of replacing it with a guessed identity.
            var cpa = await CpaUsageApi.FetchAsync();
            if (cpa != null) _cpaSnapshot = cpa;
            RefreshAccount();
            return;
        }

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
        if (CpaUsageApi.IsConfigured)
        {
            RefreshCpaAccount();
            return;
        }

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

        var age = DateTime.Now - _liveAt;
        bool stale = live && age > DirectStaleAfter;

        var text = acct.Email.Length > 0 ? acct.Email : "Not signed in";
        var state = stale ? $"· {AccountInfo.AgeText(age)} old" : !live ? "· cached" : "";
        var tip = acct.Email.Length > 0 ? acct.Email : "Not signed in";
        if (acct.Organization.Length > 0) tip += $"\n{acct.Organization}";
        tip += live
            ? $"\nUsage updated {_liveAt:h:mm tt}"
            : acct.FetchedAt == DateTime.MinValue
                ? "\nNo usage data available"
                : $"\nFrom Claude Code's cache, written {acct.FetchedAt:h:mm tt}";

        ApplyUsageBar(chosen, text, state, tip, stale);
    }

    /// <summary>Present the one Claude account CPA is currently routing, never a row merely present
    /// in its account pool. The snapshot comes from the sanitized loopback dashboard — no auth file
    /// or bearer token is opened by this app.</summary>
    void RefreshCpaAccount()
    {
        var cpa = _cpaSnapshot;
        if (cpa == null)
        {
            _accountUuid = null;
            ApplyUsageBar(Array.Empty<UsageMeter>(), "CPA account unavailable", "· connecting",
                "Waiting for the local CPA usage dashboard at 127.0.0.1:8318.", subdued: true);
            return;
        }

        _accountUuid = cpa.AccountKey;
        var age = cpa.CacheAge;
        bool stale = cpa.HasData && age > CpaUsageApi.StaleAfter;
        bool subdued = stale || !cpa.ProxyUp || !cpa.HasData;

        string state = !cpa.ProxyUp ? "· CPA offline" :
            !cpa.HasData ? "· no usage" :
            stale ? $"· CPA · {AccountInfo.AgeText(age)} old" : "· CPA";

        string text = cpa.Email.Length > 0 ? cpa.Email : "CPA account unavailable";
        string tip = text + "\nSelected by CPA proxy";
        if (cpa.BindingAt != DateTime.MinValue)
            tip += $"\nCurrent binding recorded {cpa.BindingAt:g}";
        tip += cpa.HasData
            ? cpa.UsageFetchedAt == DateTime.MinValue
                ? $"\nUsage cache is {AccountInfo.AgeText(age)} old"
                : $"\nUsage updated {cpa.UsageFetchedAt:g} ({AccountInfo.AgeText(age)} ago)"
            : "\nNo usage data is available for this account";
        if (!cpa.ProxyUp) tip += "\nCPA proxy is not responding";

        ApplyUsageBar(cpa.Meters, text, state, tip, subdued);
    }

    /// <summary>Mirror Claude plus Codex meters and the footer labels without rebuilding stable WPF
    /// bindings. Keeping this shared prevents CPA mode and direct-login mode from drifting.</summary>
    void ApplyUsageBar(IEnumerable<UsageMeter> claudeMeters, string text, string state, string tip,
        bool subdued)
    {
        // Codex's limits ride along in its rollout logs, so they cost nothing to add and are always
        // first-hand. They come last so the Claude meters keep their established positions.
        var combined = new List<UsageMeter>(claudeMeters);
        combined.AddRange(CodexScanner.PlanMeters);

        // Compared by value, not by reference: Codex meters are rebuilt on every read. Include every
        // tooltip-bearing value too, so a reset/source change is not hidden behind an identical %.
        string sig = string.Join("|", combined.ConvertAll(m =>
            $"{m.Label}:{m.Percent}:{m.Severity}:{m.ResetsAt.Ticks}:{m.Note}:{m.ReadAt.Ticks}"));
        if (!string.Equals(sig, _appliedMeters as string, StringComparison.Ordinal))
        {
            _appliedMeters = sig;
            Meters.Clear();
            foreach (var m in combined) Meters.Add(m);
        }

        if (text != _accountText) { _accountText = text; AccountLabel.Text = text; }
        if (state != _usageState) { _usageState = state; UsageStateLabel.Text = state; }
        if (tip != _accountTip) { _accountTip = tip; AccountGroup.ToolTip = tip; }
        UsageBar.Opacity = subdued ? 0.5 : 1.0;
    }

    // ---------------- session groups ----------------

    List<SessionGroup> GroupsSetting => _app.Settings.SessionGroups;

    /// <summary>Stamp every row with its group so the view groups and orders it; ungrouped rows come first.</summary>
    void ApplyGroups()
    {
        var groups = GroupsSetting;
        foreach (var row in Rows)
        {
            int i = groups.FindIndex(g => g.Members.Contains(row.GroupKey));
            row.SetGroup(i >= 0 ? groups[i].Name : "", i + 1);
        }
    }

    void GroupsChanged()
    {
        _app.Settings.Save();
        ApplyGroups();
        CollectionViewSource.GetDefaultView(Rows).Refresh();
    }

    void MoveToGroup(SessionRow row, string? groupName)
    {
        foreach (var g in GroupsSetting) g.Members.Remove(row.GroupKey);
        if (!string.IsNullOrEmpty(groupName))
        {
            var g = GroupsSetting.FirstOrDefault(x => x.Name == groupName);
            if (g == null) { g = new SessionGroup { Name = groupName }; GroupsSetting.Add(g); }
            g.Members.Add(row.GroupKey);
        }
        GroupsChanged();
    }

    string? PromptGroupName(string title, string initial = "")
    {
        var dlg = new TextPromptWindow(this, title, "Group name", initial);
        if (dlg.ShowDialog() != true) return null;
        return GroupsSetting.Any(g => g.Name == dlg.Text && g.Name != initial) ? null : dlg.Text;
    }

    MenuItem GroupSubmenu(SessionRow row)
    {
        var sub = new MenuItem { Header = "Group" };
        foreach (var g in GroupsSetting)
        {
            string name = g.Name;
            var item = Item(name, () => MoveToGroup(row, name));
            item.IsCheckable = true;
            item.IsChecked = row.GroupName == name;
            sub.Items.Add(item);
        }
        if (GroupsSetting.Count > 0) sub.Items.Add(new Separator());
        sub.Items.Add(Item("New group…", () => { var name = PromptGroupName("New group"); if (name != null) MoveToGroup(row, name); }));
        if (row.GroupName.Length > 0) sub.Items.Add(Item("Remove from group", () => MoveToGroup(row, null)));
        return sub;
    }

    static string? GroupNameOf(object sender) => sender is FrameworkElement { Tag: string name } && name.Length > 0 ? name : null;

    void GroupHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (GroupNameOf(sender) is not { } name) return;
        var g = GroupsSetting.FirstOrDefault(x => x.Name == name);
        if (g == null) return;
        g.Collapsed = !g.Collapsed;
        GroupsChanged();
        e.Handled = true;
    }

    readonly ContextMenu _groupMenu = new();

    void GroupHeader_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (GroupNameOf(sender) is not { } name) return;
        e.Handled = true;
        _groupMenu.Items.Clear();
        _groupMenu.Items.Add(Item("Rename…", () =>
        {
            var g = GroupsSetting.FirstOrDefault(x => x.Name == name);
            var renamed = g == null ? null : PromptGroupName("Rename group", g.Name);
            if (g != null && renamed != null) { g.Name = renamed; GroupsChanged(); }
        }));
        _groupMenu.Items.Add(Item("Ungroup (keep the sessions)", () => { GroupsSetting.RemoveAll(x => x.Name == name); GroupsChanged(); }));
        _groupMenu.PlacementTarget = SessionsGrid;
        _groupMenu.Placement = PlacementMode.MousePoint;
        Dispatcher.BeginInvoke(() => _groupMenu.IsOpen = true);
    }

    Point _rowDragStart;
    SessionRow? _rowDragCandidate;

    void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rowDragCandidate = RowAt(e.OriginalSource);
        _rowDragStart = e.GetPosition(this);
    }

    void Grid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_rowDragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _rowDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _rowDragCandidate;
        _rowDragCandidate = null;
        DragDrop.DoDragDrop(SessionsGrid, new DataObject(typeof(SessionRow), row), DragDropEffects.Move);
    }

    void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SessionRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Drop on a row: join that row's group (or leave one). Drop on a group header: join that group.</summary>
    void Grid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SessionRow)) is not SessionRow dragged) return;
        e.Handled = true;
        string? target = null;
        if (RowAt(e.OriginalSource) is { } onRow) target = onRow.GroupName.Length > 0 ? onRow.GroupName : null;
        else
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is Border { Tag: string } )) dep = VisualTreeHelper.GetParent(dep);
            if (dep is Border { Tag: string name } && name.Length > 0) target = name;
            else if (dep == null) return;
        }
        if ((target ?? "") == dragged.GroupName) return;
        MoveToGroup(dragged, target);
    }

    static SessionRow? RowAt(object? originalSource)
    {
        var dep = originalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow) dep = VisualTreeHelper.GetParent(dep);
        return (dep as DataGridRow)?.Item as SessionRow;
    }

    /// <summary>A single click on a hosted row shows its tab; external sessions still need a double-click.</summary>
    void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is not { } row) return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(d) != null) return;
        Deck.Open(row.Host);
    }

    static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not DataGridRow)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    readonly ContextMenu _rowMenu = new();

    void Grid_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is not { } row || row.IsBackgroundAgent) return;
        e.Handled = true;
        _rowMenu.Items.Clear();
        var tab = Deck.FindByHost(row.Host.Id);
        if (row.Host.HasExited)
        {
            _rowMenu.Items.Add(Item("Remove", () => { if (tab != null) Deck.Close(tab); HostManager.Forget(row.Host); RequestRefresh(); }));
            var again = Item("Restart session", () => _ = RestartSession(row));
            again.IsEnabled = row.CanRestart && !_restarting.Contains(row.Host.Id);
            _rowMenu.Items.Add(again);
        }
        else
        {
            _rowMenu.Items.Add(Item(tab != null ? "Show tab" : "Open tab", () => Deck.Open(row.Host)));
            if (tab != null) _rowMenu.Items.Add(Item("Close tab (keep running)", () => Deck.Close(tab)));
            _rowMenu.Items.Add(Item("End session", () => { HostManager.Kill(row.Host); if (tab != null) Deck.Close(tab); }));
            var restart = Item(row.UpdatePending
                    ? $"Restart to update (v{row.Version} → v{InstalledVersions.For(row.Provider)})"
                    : "Restart session (resume on the installed CLI)",
                () => _ = RestartSession(row));
            restart.IsEnabled = row.CanRestart && !_restarting.Contains(row.Host.Id);
            if (!row.CanRestart) restart.ToolTip = "The session has not reported an id to resume yet";
            _rowMenu.Items.Add(restart);
        }
        _rowMenu.Items.Add(new Separator());
        _rowMenu.Items.Add(GroupSubmenu(row));
        _rowMenu.Items.Add(Item("Copy session id", () => TrySetClipboard(row.Host.SessionId)));
        _rowMenu.Items.Add(Item("Copy folder", () => TrySetClipboard(row.Cwd)));
        _rowMenu.PlacementTarget = SessionsGrid;
        _rowMenu.Placement = PlacementMode.MousePoint;
        Dispatcher.BeginInvoke(() => _rowMenu.IsOpen = true, DispatcherPriority.Input);
    }

    readonly HashSet<string> _restarting = new();
    static readonly TimeSpan IdleBeforeRestart = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A session whose CLI has updated underneath it is restarted once it has sat idle for a while and
    /// is not the tab in front of a focused window, so no turn is cut off and no half-typed prompt is
    /// lost. The session itself survives: the restart resumes it from its transcript.
    /// </summary>
    void RestartUpdatedIdleSessions()
    {
        if (!_app.Settings.AutoRestartOnUpdate) return;
        foreach (var row in Rows.ToList())
        {
            if (!row.UpdatePending || !row.CanRestart || _restarting.Contains(row.Host.Id)) continue;
            // Only a session whose live conversation is provably resumable is touched on its own;
            // anything else waits for a restart you ask for.
            if (!SessionScanner.HasConversation(row.TranscriptPath)) continue;
            // Idle means the row shows Completed (hook or registry, whichever is live) with nothing of
            // its own running, and has for a while; the host's hook status alone can be stale when
            // the CLI in the pane was started by hand.
            if (row.State != SessionState.Completed || row.HasActiveSubagents) continue;
            if (DateTime.Now - row.LastChanged < IdleBeforeRestart) continue;
            var tab = Deck.FindByHost(row.Host.Id);
            if (tab != null && tab == Deck.Active && IsActive) continue;
            PerformanceLog.Write($"auto-restart host={row.Host.Id} provider={row.Provider} from=v{row.Version} to=v{InstalledVersions.For(row.Provider)}");
            _ = RestartSession(row);
        }
    }

    /// <summary>
    /// End the host's child and start the same session again in a new host, in the same tab slot.
    /// Claude only writes a transcript once something has been said, and <c>--resume</c> refuses a
    /// session without one, so an unused session is started fresh under its own id instead.
    /// </summary>
    async Task RestartSession(SessionRow row)
    {
        var host = row.Host;
        if (!row.CanRestart || !_restarting.Add(host.Id)) return;
        try
        {
            var tab = Deck.FindByHost(host.Id);
            int slot = tab != null ? Deck.Tabs.IndexOf(tab) : -1;
            bool wasActive = tab != null && Deck.Active == tab;
            var provider = row.Provider;
            string sessionId = row.LiveSessionId;
            bool hasTranscript = SessionScanner.HasConversation(row.TranscriptPath);
            string cmd = provider == SessionProvider.Codex
                ? HostManager.ResumeCodexCommand(sessionId, _app.Settings.CodexFlags)
                : hasTranscript
                    ? HostManager.ResumeClaudeCommand(sessionId, _app.Settings.ResumeFlags)
                    : HostManager.NewClaudeCommand(sessionId, null, _app.Settings.ResumeFlags);

            if (!host.HasExited)
            {
                HostManager.Kill(host);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250);
                    var fresh = HostManager.Reload(host);
                    if (fresh == null || fresh.HasExited || !HostManager.IsAlive(fresh)) break;
                }
            }

            var next = HostManager.Spawn(sessionId, provider, cmd, host.Cwd, host.Title);
            HostManager.Forget(host);
            if (tab != null) Deck.Close(tab);
            var opened = Deck.Open(next, activate: wasActive);
            if (slot >= 0) Deck.Move(opened, slot);
            RequestRefresh();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            LiveLabel.Text = "Restart failed: " + ex.Message;
        }
        finally { _restarting.Remove(host.Id); }
    }

    MenuItem Item(string header, Action run)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) =>
        {
            PerformanceLog.Write($"row-menu click: {header}");
            Dispatcher.BeginInvoke(run, DispatcherPriority.Background);
        };
        return mi;
    }

    static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    /// <summary>
    /// Bring an external session into the deck: end its process tree, then resume the same
    /// conversation in a hosted tab. The kill is the destructive half, so it is confirmed first.
    /// </summary>
    public void AdoptIntoDeck(SessionInfo s, bool confirm = true)
    {
        string what = s.Provider == SessionProvider.Codex ? "Codex thread" : "Claude session";
        if (s.Provider == SessionProvider.Claude && (s.TranscriptPath.Length == 0 || !File.Exists(s.TranscriptPath)))
        {
            System.Windows.MessageBox.Show(this,
                $"\"{s.DisplayName}\" has no conversation on disk yet, so there is nothing to resume.\n\n" +
                "Send it one message first, then adopt it.",
                "Adopt into deck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (confirm)
        {
            var answer = System.Windows.MessageBox.Show(this,
                $"Stop the external {what} \"{s.DisplayName}\" (PID {s.Pid}) and resume it in a deck tab?\n\n" +
                "Anything it is doing right now is interrupted; the conversation itself is kept.",
                "Adopt into deck", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }
        try
        {
            using var p = Process.GetProcessById(s.Pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            LiveLabel.Text = "Could not stop the external session: " + ex.Message;
            return;
        }
        ResumeInDeck(new SavedSession { Id = s.SessionId, Cwd = s.Cwd, Name = s.Name, Provider = s.Provider });
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
        Deck.Open(row.Host);
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
