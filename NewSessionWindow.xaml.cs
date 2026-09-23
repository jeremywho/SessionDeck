using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SessionDeck;

/// <summary>What the New-session dialog decided. Empty strings mean "the CLI's own default".</summary>
internal sealed record NewSessionRequest(SessionProvider Provider, string Cwd, string Name, string Model, string Effort, string Prompt);

/// <summary>Modal launcher for a deck session. <c>ShowDialog() == true</c> when <see cref="Result"/> is set.</summary>
internal partial class NewSessionWindow : Wpf.Ui.Controls.FluentWindow
{
    static readonly string[] ClaudeModels = { "", "claude-fable-5-1[1m]", "claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5-20251001" };
    static readonly string[] CodexModels = { "", "gpt-6-astra", "gpt-5.5", "gpt-5.5-mini" };
    static readonly string[] ClaudeEfforts = { "", "low", "medium", "high", "xhigh", "max" };
    static readonly string[] CodexEfforts = { "", "low", "medium", "high", "xhigh" };

    readonly Settings _settings;
    public NewSessionRequest? Result { get; private set; }

    public NewSessionWindow(Window owner, Settings settings, SessionProvider initial)
    {
        Owner = owner;
        _settings = settings;
        InitializeComponent();
        if (initial == SessionProvider.Codex) CodexRadio.IsChecked = true; else ClaudeRadio.IsChecked = true;
        foreach (var f in settings.RecentFolders) FolderBox.Items.Add(f);
        FolderBox.Text = settings.RecentFolders.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Provider_Changed(this, new RoutedEventArgs());
        Loaded += (_, _) => PromptBox.Focus();
    }

    SessionProvider Provider => CodexRadio.IsChecked == true ? SessionProvider.Codex : SessionProvider.Claude;

    void Provider_Changed(object sender, RoutedEventArgs e)
    {
        if (ModelBox == null || EffortBox == null) return;
        bool codex = Provider == SessionProvider.Codex;
        Fill(ModelBox, codex ? CodexModels : ClaudeModels, codex ? _settings.LastCodexModel : _settings.LastClaudeModel);
        Fill(EffortBox, codex ? CodexEfforts : ClaudeEfforts, codex ? _settings.LastCodexEffort : _settings.LastClaudeEffort);
        NameBox.IsEnabled = !codex;
        NameBox.ToolTip = codex ? "Codex names a thread from inside the TUI" : null;
        string flags = codex ? _settings.CodexFlags : _settings.ResumeFlags;
        FlagsNote.Text = flags.Length > 0 ? "Also: " + flags : "";
        FlagsNote.ToolTip = flags.Length > 0 ? "Flags from Settings, appended to every launch" : null;
    }

    static void Fill(ComboBox box, string[] items, string current)
    {
        box.Items.Clear();
        foreach (var it in items) box.Items.Add(new ComboBoxItem { Content = it.Length == 0 ? "(default)" : it, Tag = it });
        int idx = Array.IndexOf(items, current);
        box.SelectedIndex = idx >= 0 ? idx : 0;
        if (idx < 0 && current.Length > 0 && box.IsEditable) box.Text = current;
    }

    static string Chosen(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem it) return it.Tag as string ?? "";
        string t = box.Text?.Trim() ?? "";
        return t == "(default)" ? "" : t;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Session folder", InitialDirectory = SafeDir(FolderBox.Text) };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    static string SafeDir(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Directory.Exists(s) ? s : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    void Launch_Click(object sender, RoutedEventArgs e)
    {
        string cwd = (FolderBox.Text ?? "").Trim().Trim('"');
        if (cwd.Length == 0) cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(cwd))
        {
            System.Windows.MessageBox.Show(this, $"Folder not found:\n{cwd}", "New session", MessageBoxButton.OK, MessageBoxImage.Warning);
            FolderBox.Focus();
            return;
        }
        cwd = Path.GetFullPath(cwd);
        bool codex = Provider == SessionProvider.Codex;
        string model = Chosen(ModelBox), effort = Chosen(EffortBox);
        if (codex) { _settings.LastCodexModel = model; _settings.LastCodexEffort = effort; }
        else { _settings.LastClaudeModel = model; _settings.LastClaudeEffort = effort; }
        _settings.RememberFolder(cwd);
        _settings.Save();
        Result = new NewSessionRequest(Provider, cwd, codex ? "" : NameBox.Text.Trim(), model, effort, PromptBox.Text.Trim());
        DialogResult = true;
    }
}
