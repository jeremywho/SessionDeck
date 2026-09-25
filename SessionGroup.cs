using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>
    /// Stamp every row with its group. A row whose key changed since the last pass (a /resume or /clear
    /// inside the session, a fresh session learning its id, a restart that came back under another id)
    /// takes its membership to the new key. Returns whether any membership was rewritten.
    /// </summary>
    internal static bool Apply(List<SessionGroup> groups, IEnumerable<SessionRow> rows)
    {
        bool moved = false;
        foreach (var row in rows)
        {
            string key = row.GroupKey;
            if (row.GroupedAs.Length > 0 && row.GroupedAs != key)
                foreach (var g in groups)
                {
                    int at = g.Members.IndexOf(row.GroupedAs);
                    if (at < 0) continue;
                    if (g.Members.Contains(key)) g.Members.RemoveAt(at);
                    else g.Members[at] = key;
                    moved = true;
                }
            row.GroupedAs = key;
            int i = groups.FindIndex(g => g.Members.Contains(key));
            row.SetGroup(i >= 0 ? groups[i].Name : "", i + 1);
        }
        return moved;
    }
}

/// <summary>The deck's columns as last laid out: which hosts each held, which was showing, its share of the width.</summary>
internal sealed class DeckLayout
{
    public List<DeckColumn> Columns { get; set; } = new();
    public int Focused { get; set; }

    /// <summary>Hosts still running whose tab was closed, and their sessions (same index).</summary>
    public List<string> ClosedHosts { get; set; } = new();
    public List<string> ClosedSessions { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class DeckColumn
{
    public List<string> Hosts { get; set; } = new();

    /// <summary>The session each host in <see cref="Hosts"/> runs (same index), so a slot outlives its host id.</summary>
    public List<string> Sessions { get; set; } = new();
    public string Active { get; set; } = "";
    public double Fraction { get; set; } = 1;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
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
