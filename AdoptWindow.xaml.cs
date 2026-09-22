using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace SessionDeck;

/// <summary>Picker for sessions running outside the deck. Adopting kills the external process by pid
/// and resumes the same conversation in a hosted tab.</summary>
internal partial class AdoptWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly SessionsWindow _owner;
    public ObservableCollection<AdoptItem> Items { get; }

    public AdoptWindow(SessionsWindow owner, IEnumerable<SessionInfo> external)
    {
        _owner = owner;
        InitializeComponent();
        Owner = owner;
        Items = new ObservableCollection<AdoptItem>(external
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(s => new AdoptItem(s)));
        if (Items.Count == 0) EmptyLabel.Visibility = Visibility.Visible;
        DataContext = this;
    }

    void Adopt_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Items.Where(i => i.IsChecked && i.CanAdopt).Select(i => i.Info).ToList();
        Close();
        foreach (var s in chosen) _owner.AdoptIntoDeck(s, confirm: false);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class AdoptItem
{
    public SessionInfo Info { get; }
    public bool IsChecked { get; set; }

    public AdoptItem(SessionInfo s)
    {
        Info = s;
        CanAdopt = s.Kind != "companion"
                   && (s.Provider == SessionProvider.Codex || (s.TranscriptPath.Length > 0 && File.Exists(s.TranscriptPath)));
    }

    public bool CanAdopt { get; }
    public string Name => Info.DisplayName;
    public SessionProvider Provider => Info.Provider;
    public string ProviderGlyph => Info.Provider == SessionProvider.Codex ? "◆" : "✳";
    public string ProviderName => Info.Provider == SessionProvider.Codex ? "Codex" : "Claude Code";
    public string Note =>
        Info.Kind == "companion" ? "background agent, no terminal"
        : !CanAdopt ? "no conversation yet"
        : Info.Model.Length > 0 ? Info.Model : "";
    public string Detail => $"{Info.Cwd} · PID {Info.Pid}";
}
