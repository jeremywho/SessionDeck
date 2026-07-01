using System.Windows;

namespace ClaudeSessionMonitor;

internal partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly App _app;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        ThemeToggle.IsChecked = !string.Equals(_app.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        TaskbarToggle.IsChecked = _app.Settings.ShowInTaskbar;
        ResumeFlagsBox.Text = _app.Settings.ResumeFlags;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        // theme: flip only if the choice changed (ToggleTheme applies the palette + persists live)
        bool wantDark = ThemeToggle.IsChecked == true;
        bool isDark = !string.Equals(_app.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        if (wantDark != isDark) _app.ToggleTheme();

        _app.Settings.ShowInTaskbar = TaskbarToggle.IsChecked == true;
        _app.ApplyShowInTaskbar();

        _app.Settings.ResumeFlags = ResumeFlagsBox.Text?.Trim() ?? "";

        _app.Settings.Save();
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
