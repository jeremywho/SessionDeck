using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using SessionDeck.Gateway;

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
        foreach (System.Windows.Controls.ComboBoxItem item in RowOrderBox.Items)
            if (string.Equals(item.Tag as string, _app.Settings.RowOrder, StringComparison.OrdinalIgnoreCase)) RowOrderBox.SelectedItem = item;
        if (RowOrderBox.SelectedItem == null) RowOrderBox.SelectedIndex = 0;
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

        GatewayToggle.IsChecked = _app.Settings.Gateway.Enabled;
        GatewayPortBox.Value = _app.Settings.Gateway.Port;
        _ = RefreshDevicesAsync();

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
        _app.Settings.RowOrder = (RowOrderBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? Settings.RowOrderState;
        _app.ApplyRowOrder();

        _app.Settings.ResumeFlags = ResumeFlagsBox.Text?.Trim() ?? "";
        _app.Settings.CodexFlags = CodexFlagsBox.Text?.Trim() ?? "";
        _app.Settings.TerminalFont = string.IsNullOrWhiteSpace(FontBox.Text) ? "CaskaydiaCove NF" : FontBox.Text.Trim();
        _app.Settings.TerminalFontSize = FontSizeBox.Value is double fs && fs >= 8 ? fs : 12;
        _app.Settings.TerminalOpacity = (int)OpacitySlider.Value;
        _app.Settings.TerminalScheme = (SchemeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "Campbell";
        _app.ApplyTerminalLook();

        bool gatewayWas = _app.Settings.Gateway.Enabled;
        int portWas = _app.Settings.Gateway.Port;
        _app.Settings.Gateway.Enabled = GatewayToggle.IsChecked == true;
        _app.Settings.Gateway.Port = GatewayPortBox.Value is double gp && gp >= 1024 && gp <= 65535 ? (int)gp : 7420;
        _app.Settings.Save();
        if (_app.Settings.Gateway.Enabled != gatewayWas || _app.Settings.Gateway.Port != portWas) _app.ApplyGatewaySettings();
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    sealed record DeviceLine(string Id, string Name, string Seen);

    async Task RefreshDevicesAsync()
    {
        var client = GatewayClient.Running();
        if (client == null)
        {
            PairNote.Text = _app.Settings.Gateway.Enabled ? "The gateway is not running. It starts with the deck; save to start it now." : "Turn the gateway on and save to pair a phone.";
            DevicesList.ItemsSource = null;
            return;
        }
        var devices = await client.DevicesAsync();
        DevicesList.ItemsSource = devices.Select(d => new DeviceLine(d.Id, d.Name, "last seen " + Ago(d.LastSeenUtc))).ToList();
        if (PairText.Text.Length == 0) PairNote.Text = devices.Count == 0 ? "No phone is paired yet." : "";
    }

    static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalMinutes < 2 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalDays} d ago";
    }

    async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var client = GatewayClient.Running();
        if (client == null) { PairNote.Text = "The gateway is not running."; return; }
        var issued = await client.IssueCodeAsync();
        if (issued == null) { PairNote.Text = "The gateway did not answer."; return; }
        string host = QrPayload.LanAddress();
        string payload = QrPayload.For(host, client.Port, issued.Value.Code);
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
        var image = new BitmapImage();
        using (var ms = new System.IO.MemoryStream(png)) { image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = ms; image.EndInit(); }
        QrImage.Source = image;
        QrImage.Visibility = Visibility.Visible;
        PairText.Text = $"{host}:{client.Port}   code {issued.Value.Code}";
        PairNote.Text = "Scan with the phone app, or type the address and code. Good for five minutes, one phone.";
    }

    async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        var client = GatewayClient.Running();
        if (client != null) await client.RevokeAsync(id);
        await RefreshDevicesAsync();
    }

    /// <summary>A rule by port, private profile: it survives updates (the gateway runs from a stamped copy) and needs one elevation.</summary>
    void Firewall_Click(object sender, RoutedEventArgs e)
    {
        int port = GatewayPortBox.Value is double gp && gp >= 1024 ? (int)gp : _app.Settings.Gateway.Port;
        string script = $"netsh advfirewall firewall delete rule name=\"SessionDeck gateway\" ; netsh advfirewall firewall add rule name=\"SessionDeck gateway\" dir=in action=allow protocol=TCP localport={port} profile=private";
        // Encoded, not quoted: nested quotes inside -Command split the rule name into two arguments.
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}") { UseShellExecute = true, Verb = "runas" });
            PairNote.Text = $"Firewall rule requested for port {port} on private networks.";
        }
        catch (Exception ex) { PairNote.Text = "Firewall rule not added: " + ex.Message; }
    }
}
