using System.Windows;

namespace ClaudeSessionMonitor;

internal partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly App _app;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        ResumeFlagsBox.Text = _app.Settings.ResumeFlags;
        ContextWindowBox.Text = _app.Settings.ContextWindowTokens.ToString();
        InstantUpdatesCheck.IsChecked = _app.Settings.InstantUpdates;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.ResumeFlags = ResumeFlagsBox.Text?.Trim() ?? "";
        if (long.TryParse(ContextWindowBox.Text?.Trim(), out var ctx) && ctx > 0)
        {
            _app.Settings.ContextWindowTokens = ctx;
            SessionRow.ContextWindow = ctx;   // takes effect on the next refresh
        }

        bool wantInstant = InstantUpdatesCheck.IsChecked == true;
        bool instantChanged = wantInstant != _app.Settings.InstantUpdates;
        _app.Settings.InstantUpdates = wantInstant;

        _app.Settings.Save();
        if (instantChanged) _app.ApplyInstantUpdates();
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
