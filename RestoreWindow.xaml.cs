using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace ClaudeSessionMonitor;

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
            SessionLauncher.Resume(it.Session, _app.Settings.ResumeFlags);
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class RestoreItem
{
    public SavedSession Session { get; set; } = new();
    public string Name { get; set; } = "";
    public string Cwd { get; set; } = "";
    public bool IsChecked { get; set; }
}
