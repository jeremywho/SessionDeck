using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SessionDeck;

/// <summary>
/// A named, collapsible set of sessions in the list. Members are session ids (the CLI's own id, which
/// survives a restart), or a host id for a session that has not reported one yet. Ungrouped sessions
/// sort above every group.
/// </summary>
internal sealed class SessionGroup
{
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
    public List<string> Members { get; set; } = new();
}

/// <summary>Group name → whether its rows are hidden, read from the live settings.</summary>
internal sealed class GroupCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string name = value as string ?? "";
        if (name.Length == 0 || Application.Current is not App app) return false;
        return app.Settings.SessionGroups.FirstOrDefault(g => g.Name == name)?.Collapsed ?? false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
