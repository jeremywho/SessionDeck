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
    void ShowSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded) _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Show();
        _settingsWindow.Activate();
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

    /// <summary>Sessions saved last run that aren't live now (and seen within the last day).</summary>
    static List<SavedSession> ComputeOrphaned()
    {
        var saved = SessionRegistry.Load();
        if (saved.Count == 0) return new();
        var liveIds = new HashSet<string>(SessionScanner.Scan().Select(s => s.SessionId));
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 24L * 60 * 60 * 1000;
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
        if (_window != null) _window.AllowClose = true;
        Shutdown();
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

    static void LogError(Exception? ex)
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
