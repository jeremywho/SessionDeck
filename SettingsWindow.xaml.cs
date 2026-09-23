using System.Windows;

namespace SessionDeck;

internal partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly App _app;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        ThemeToggle.IsChecked = !string.Equals(_app.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        TaskbarToggle.IsChecked = _app.Settings.ShowInTaskbar;
        RunOnLoginToggle.IsChecked = _app.Settings.RunOnLogin;
        AutoRestartToggle.IsChecked = _app.Settings.AutoRestartOnUpdate;
        ResumeFlagsBox.Text = _app.Settings.ResumeFlags;
        CodexFlagsBox.Text = _app.Settings.CodexFlags;
        FontBox.Text = _app.Settings.TerminalFont;
        FontSizeBox.Value = _app.Settings.TerminalFontSize;
        OpacitySlider.Value = Math.Clamp(_app.Settings.TerminalOpacity, 30, 100);
        OpacityLabel.Text = $"{(int)OpacitySlider.Value}%";
        OpacitySlider.ValueChanged += (_, e) => OpacityLabel.Text = $"{(int)e.NewValue}%";
        foreach (System.Windows.Controls.ComboBoxItem item in SchemeBox.Items)
            if (string.Equals(item.Content as string, _app.Settings.TerminalScheme, StringComparison.OrdinalIgnoreCase)) SchemeBox.SelectedItem = item;
        if (SchemeBox.SelectedItem == null) SchemeBox.SelectedIndex = 0;

        // Assembly version == the release tag (release.yml stamps -p:Version); dev builds show the csproj default.
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v == null ? "" : $"v{v.ToString(3)}";
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        // theme: flip only if the choice changed (ToggleTheme applies the palette + persists live)
        bool wantDark = ThemeToggle.IsChecked == true;
        bool isDark = !string.Equals(_app.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        if (wantDark != isDark) _app.ToggleTheme();

        _app.Settings.ShowInTaskbar = TaskbarToggle.IsChecked == true;
        _app.ApplyShowInTaskbar();

        _app.Settings.RunOnLogin = RunOnLoginToggle.IsChecked == true;
        _app.Settings.AutoRestartOnUpdate = AutoRestartToggle.IsChecked == true;
        _app.ApplyRunOnLogin();

        _app.Settings.ResumeFlags = ResumeFlagsBox.Text?.Trim() ?? "";
        _app.Settings.CodexFlags = CodexFlagsBox.Text?.Trim() ?? "";
        _app.Settings.TerminalFont = string.IsNullOrWhiteSpace(FontBox.Text) ? "CaskaydiaCove NF" : FontBox.Text.Trim();
        _app.Settings.TerminalFontSize = FontSizeBox.Value is double fs && fs >= 8 ? fs : 12;
        _app.Settings.TerminalOpacity = (int)OpacitySlider.Value;
        _app.Settings.TerminalScheme = (SchemeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "Campbell";
        _app.ApplyTerminalLook();

        _app.Settings.Save();
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
