using System.Linq;
using System.Windows;
using System.Windows.Forms;          // tray only: NotifyIcon / ContextMenuStrip
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace ClaudeSessionMonitor;

/// <summary>WPF application shell. Owns the tray icon, the theme palette, and the sessions window.</summary>
internal sealed class App : Application
{
    NotifyIcon _tray = null!;
    WpfContextMenu _trayMenu = null!;
    SessionsWindow? _window;

    public Settings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A tray app has no console; log unhandled exceptions so failures aren't silent.
        DispatcherUnhandledException += (_, ev) => { LogError(ev.Exception); ev.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LogError(ev.ExceptionObject as Exception);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Settings = Settings.Load();
        ApplyRunOnLogin();   // also refreshes the registered path if the install dir ever moves

        var theme = ThemeFrom(Settings.Theme);
        Resources.MergedDictionaries.Add(new ControlsDictionary());
        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = theme });
        Resources.MergedDictionaries.Add(PaletteDict(theme));   // our custom design tokens
        ApplicationThemeManager.Apply(theme);

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Claude Sessions",
        };
        _trayMenu = BuildTrayMenu();
        _tray.MouseUp += OnTrayMouseUp;   // WPF menu (per-monitor DPI safe), not the WinForms one
        _tray.DoubleClick += (_, _) => ShowWindow();

        // Orphaned = saved in the registry but not currently live. Capture BEFORE the window's scan
        // loop starts overwriting the registry, then offer to restore them.
        var orphaned = ComputeOrphaned();

        ShowWindow();

        if (orphaned.Count > 0) ShowRestore(orphaned);

        StartUpdater();
    }

    /// <summary>
    /// OS shutdown / restart / logoff (WM_QUERYENDSESSION). Windows kills the claude processes
    /// while this app keeps pumping — the tray window cancels its close — so the next scan would
    /// see an emptying live set and overwrite active-sessions.json with it, seconds before the
    /// app itself dies. Freeze the registry so the file keeps the last real set for restore.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        SessionRegistry.Frozen = true;
        base.OnSessionEnding(e);
    }

    // ---------------- auto-update ----------------

    void StartUpdater()
    {
        // A freshly-updated build: once it's been up a few seconds, clear the rollback marker + drop .old.
        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        settle.Tick += (_, _) => { settle.Stop(); Updater.ConfirmStartupOk(); };
        settle.Start();

        // Test hook: force the "update ready" button without cutting a real release.
        var fake = Environment.GetEnvironmentVariable("CSM_FAKE_UPDATE");
        if (!string.IsNullOrEmpty(fake)) { _window?.ShowUpdateReady(fake); return; }

        Updater.UpdateStaged += () => Dispatcher.InvokeAsync(() => _window?.ShowUpdateReady(Updater.StagedTag ?? ""));

        _ = Updater.CheckAsync();   // check now,
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(4) };  // then periodically
        timer.Tick += (_, _) => { _ = Updater.CheckAsync(); };
        timer.Start();
    }

    /// <summary>Save state, swap the staged exe into place, and relaunch it.</summary>
    public void ApplyUpdate()
    {
        Settings.Save();
        if (Updater.Apply())
        {
            // Shutdown() trips a WPF telemetry crash (System.Diagnostics.Tracing not resolvable) in
            // single-file self-contained builds, which can leave the process half-alive still holding
            // the single-instance mutex and block the relaunch. Dispose the tray and hard-exit instead.
            _tray?.Dispose();
            Environment.Exit(0);
        }
    }

    static ApplicationTheme ThemeFrom(string s) =>
        string.Equals(s, "Light", StringComparison.OrdinalIgnoreCase) ? ApplicationTheme.Light : ApplicationTheme.Dark;

    static ResourceDictionary PaletteDict(ApplicationTheme t) => new ResourceDictionary
    {
        Source = new Uri($"pack://application:,,,/Themes/{(t == ApplicationTheme.Light ? "Light" : "Dark")}.xaml", UriKind.Absolute)
    };

    /// <summary>Swap the custom design-token dictionary (DynamicResource consumers re-theme live).</summary>
    public void ApplyPalette(ApplicationTheme t)
    {
        var dicts = Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Contains("SurfaceBrush"));
        if (old != null) dicts.Remove(old);
        dicts.Add(PaletteDict(t));
    }

    public void ToggleTheme()
    {
        Settings.Theme = string.Equals(Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        var t = ThemeFrom(Settings.Theme);
        ApplicationThemeManager.Apply(t);   // WPF-UI controls
        ApplyPalette(t);                    // our custom tokens
        _window?.RefreshAfterThemeChange(); // re-run the Context% color converter against the new palette
        Settings.Save();
    }

    void ShowWindow()
    {
        _window ??= new SessionsWindow(this);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    SettingsWindow? _settingsWindow;
    public void ShowSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded) _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Sync the HKCU Run registration. Installed instance only — dev builds leave the key alone.</summary>
    public void ApplyRunOnLogin()
    {
        if (Installer.IsInstalledInstance()) Installer.SyncRunAtLogin(Settings.RunOnLogin);
    }

    /// <summary>Apply the "show in taskbar" preference to the live window.</summary>
    public void ApplyShowInTaskbar()
    {
        if (_window != null) _window.ShowInTaskbar = Settings.ShowInTaskbar;
    }

    void RestoreFromTray()
    {
        var orphaned = ComputeOrphaned();
        if (orphaned.Count == 0)
        {
            _tray.ShowBalloonTip(2500, "Claude Sessions", "No previous sessions to restore.", ToolTipIcon.Info);
            return;
        }
        ShowRestore(orphaned);
    }

    void ShowRestore(List<SavedSession> orphaned)
    {
        if (orphaned.Count > 0) new RestoreWindow(this, orphaned).Show();
    }

    /// <summary>Sessions saved last run that aren't live now (and seen within the last week).</summary>
    static List<SavedSession> ComputeOrphaned()
    {
        var saved = SessionRegistry.Load();
        if (saved.Count == 0) return new();
        var liveIds = new HashSet<string>(SessionScanner.Scan().Select(s => s.SessionId));
        // A week, not a day: a machine can sit wedged/powered-off well past 24h, and the restore
        // offer is an opt-in checklist — a stale entry costs one unticked row.
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 7L * 24 * 60 * 60 * 1000;
        return saved.Where(s => !liveIds.Contains(s.Id) && s.LastSeen >= cutoff).ToList();
    }

    void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        _trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;   // at the cursor, DPI-correct
        _trayMenu.IsOpen = true;
        // bring the menu's popup to the foreground so submenus are reachable and outside-clicks dismiss it
        if (System.Windows.PresentationSource.FromVisual(_trayMenu) is System.Windows.Interop.HwndSource src)
            Native.SetForegroundWindow(src.Handle);
    }

    WpfContextMenu BuildTrayMenu()
    {
        var menu = new WpfContextMenu();
        menu.Items.Add(Item("Open", ShowWindow));
        menu.Items.Add(Item("Settings…", ShowSettings));
        menu.Items.Add(Item("Restore sessions…", RestoreFromTray));
        menu.Items.Add(Item("Toggle theme", ToggleTheme));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new WpfMenuItem { Header = "Dock to", IsEnabled = false });
        menu.Items.Add(Item("Left edge", () => _window?.SnapTo("LeftEdge")));
        menu.Items.Add(Item("Right edge", () => _window?.SnapTo("RightEdge")));
        menu.Items.Add(Item("Top-left", () => _window?.SnapTo("TopLeft")));
        menu.Items.Add(Item("Top-right", () => _window?.SnapTo("TopRight")));
        menu.Items.Add(Item("Bottom-left", () => _window?.SnapTo("BottomLeft")));
        menu.Items.Add(Item("Bottom-right", () => _window?.SnapTo("BottomRight")));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Item("Exit", ExitApp));
        return menu;
    }

    static WpfMenuItem Item(string header, Action onClick)
    {
        var mi = new WpfMenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    void ExitApp()
    {
        // Hard-exit rather than Shutdown() — the latter trips a WPF telemetry crash in single-file builds.
        _tray?.Dispose();
        Environment.Exit(0);
    }

    static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (info != null) { using var s = info.Stream; return new System.Drawing.Icon(s); }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    internal static void LogError(Exception? ex)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude-session-monitor-error.log"),
                $"[{DateTime.Now:o}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
