using System.Windows;

namespace SessionDeck;

/// <summary>One line of text from the user. <c>ShowDialog() == true</c> when <see cref="Text"/> is set.</summary>
internal partial class TextPromptWindow : Wpf.Ui.Controls.FluentWindow
{
    public string Text { get; private set; } = "";

    public TextPromptWindow(Window owner, string title, string label, string initial = "")
    {
        Owner = owner;
        InitializeComponent();
        Title = title;
        Bar.Title = title;
        Label.Text = label;
        Box.Text = initial;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Text = (Box.Text ?? "").Trim();
        if (Text.Length == 0) { Box.Focus(); return; }
        DialogResult = true;
    }
}
