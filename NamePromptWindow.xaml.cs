using System.Windows;

namespace ClaudeSessionMonitor;

/// <summary>Modal prompt for a new session's name. ShowDialog() == true when a name was entered.</summary>
internal partial class NamePromptWindow : Wpf.Ui.Controls.FluentWindow
{
    public string SessionName => NameBox.Text.Trim();

    public NamePromptWindow(Window owner)
    {
        Owner = owner;
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (SessionName.Length == 0) { NameBox.Focus(); return; }
        DialogResult = true;
    }
}
