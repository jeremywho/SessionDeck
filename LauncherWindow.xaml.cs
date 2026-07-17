using System.Windows;
using System.Windows.Input;

namespace ClaudeSessionMonitor;

/// <summary>
/// Small always-on-top pill with the new-session buttons, floating at the lower right of the
/// screen (independent of the main window, so it works while the app lives in the tray).
/// Drag it anywhere; the position persists. ShowInTaskbar=false also keeps it out of Alt-Tab
/// (WPF parents it to a hidden owner window).
/// </summary>
internal partial class LauncherWindow : Window
{
    readonly App _app;

    public LauncherWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Loaded += (_, _) => Position();
    }

    void Position()
    {
        // accept a saved position anywhere on the virtual desktop (multi-monitor)
        if (_app.Settings.LauncherLeft is double l && _app.Settings.LauncherTop is double t &&
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
            var wa = SystemParameters.WorkArea;   // primary monitor, in DIPs
            Left = wa.Right - ActualWidth - 12;
            Top = wa.Bottom - ActualHeight - 12;
        }
    }

    void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();   // returns when the drag ends
        _app.Settings.LauncherLeft = Left;
        _app.Settings.LauncherTop = Top;
        _app.Settings.Save();
    }

    void NewSessionButton_Click(object sender, RoutedEventArgs e) =>
        SessionLauncher.LaunchNew(null, _app.Settings.ResumeFlags);

    void NewNamedSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new NamePromptWindow(this);
        if (prompt.ShowDialog() == true)
            SessionLauncher.LaunchNew(prompt.SessionName, _app.Settings.ResumeFlags);
    }
}
