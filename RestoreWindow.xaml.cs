using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace SessionDeck;

internal partial class RestoreWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly App _app;
    public ObservableCollection<RestoreItem> Items { get; }

    public RestoreWindow(App app, IEnumerable<SavedSession> sessions)
    {
        _app = app;
        InitializeComponent();
        Items = new ObservableCollection<RestoreItem>(
            sessions.Select(s => new RestoreItem
            {
                Session = s,
                Name = string.IsNullOrWhiteSpace(s.Name) ? s.Id[..Math.Min(8, s.Id.Length)] : s.Name,
                Cwd = s.Cwd,
                IsChecked = true,
            }));
        DataContext = this;
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in Items.Where(i => i.IsChecked))
            _app.ResumeInDeck(it.Session);
        Close();
    }

    /// <summary>Each CLI gets its own flags. They share no spelling, so handing Claude's
    /// <c>--dangerously-skip-permissions</c> to codex would just make it exit on an unknown argument.</summary>
    string FlagsFor(SessionProvider p) =>
        p == SessionProvider.Codex ? _app.Settings.CodexFlags : _app.Settings.ResumeFlags;

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class RestoreItem
{
    public SavedSession Session { get; set; } = new();
    public string Name { get; set; } = "";
    public string Cwd { get; set; } = "";
    public bool IsChecked { get; set; }

    // The list can mix the two CLIs, and which one a row reopens with is the thing you can't infer
    // from a name and a folder — so it's marked the same way the session list marks it.
    public SessionProvider Provider => Session.Provider;
    public string ProviderGlyph => Session.Provider == SessionProvider.Codex ? "◆" : "✳";
    public string ProviderName => Session.Provider == SessionProvider.Codex ? "Codex" : "Claude Code";
}
