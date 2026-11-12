using System.Text.Json;

namespace SessionDeck;

/// <summary>One session as a device sees it: no port, no token, nothing a phone could use to reach a host directly.</summary>
internal sealed record CatalogEntry(
    string HostId,
    string SessionId,
    string Provider,
    string Name,
    string ModelChip,
    string Effort,
    string State,
    long StateSinceMs,
    int Subagents,
    string Group,
    int GroupOrder,
    int SortPriority,
    string Cwd,
    bool HostExited,
    bool MobileReady,
    long ReadAtMs = 0,
    string Kind = "",
    bool ChatReady = false);

/// <summary>
/// The session list in the order the deck shows it, built from the pipeline's pairs. Rows are kept
/// across builds and updated in place: <see cref="SessionRow.StateSince"/> is the time a state began,
/// and a fresh row would restart it on every build, turning Claude's idle notification a minute after
/// a turn into a new "unread".
/// </summary>
internal sealed class SessionCatalog
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly Dictionary<string, SessionRow> _rows = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="phoneRead">When a phone last looked at each session; the deck's own ledger comes from <paramref name="settings"/>.</param>
    public List<CatalogEntry> Build(List<(Host.HostRecord Host, SessionInfo Info)> pairs, Settings settings, DateTime nowUtc, IReadOnlyDictionary<string, long>? phoneRead = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<SessionRow>(pairs.Count);
        foreach (var (host, info) in pairs)
        {
            seen.Add(host.Id);
            if (_rows.TryGetValue(host.Id, out var row)) row.Update(info, host);
            else _rows[host.Id] = row = new SessionRow(host, info);
            rows.Add(row);
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList()) _rows.Remove(gone);

        // The deck owns its groups file; grouping here works on a copy so a stale member id is never rewritten from this side.
        var groups = settings.SessionGroups.Select(g => new SessionGroup { Name = g.Name, Collapsed = g.Collapsed, Members = new List<string>(g.Members) }).ToList();
        SessionGroup.Apply(groups, rows);

        bool byName = settings.RowOrder == Settings.RowOrderName;
        var ordered = byName
            ? rows.OrderBy(r => r.GroupOrder).ThenBy(r => r.HostExited).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            : rows.OrderBy(r => r.GroupOrder).ThenBy(r => r.SortPriority).ThenBy(r => r.Name, StringComparer.CurrentCulture);
        return ordered.Select(r => Entry(r, ReadAtMs(r.LiveSessionId, settings, phoneRead))).ToList();
    }

    /// <summary>The latest moment anyone looked at the session, on the desktop or on a phone; 0 when nobody has.</summary>
    internal static long ReadAtMs(string sessionId, Settings settings, IReadOnlyDictionary<string, long>? phoneRead)
    {
        if (sessionId.Length == 0) return 0;
        long deck = settings.ReadAt != null && settings.ReadAt.TryGetValue(sessionId, out var d) ? d : 0;
        long phone = phoneRead != null && phoneRead.TryGetValue(sessionId, out var p) ? p : 0;
        return Math.Max(deck, phone);
    }

    static CatalogEntry Entry(SessionRow r, long readAtMs) => new(
        r.Host.Id,
        r.LiveSessionId,
        string.Equals(r.Host.Provider, "Shell", StringComparison.OrdinalIgnoreCase) ? "Shell" : r.Provider.ToString(),
        r.Name,
        r.ModelChip,
        r.Effort,
        r.State.ToString(),
        r.StateSince == default ? 0 : new DateTimeOffset(r.StateSince.ToUniversalTime()).ToUnixTimeMilliseconds(),
        r.SubagentsActive,
        r.GroupName,
        r.GroupOrder,
        r.SortPriority,
        r.Cwd,
        r.HostExited,
        r.Host.Protocol >= 4 && !r.Host.HasExited && !r.Host.IsChat,
        readAtMs,
        r.Host.Kind,
        r.Host.IsChat && !r.Host.HasExited);

    public static string ToJson(IReadOnlyList<CatalogEntry> entries) => JsonSerializer.Serialize(entries, Json);
}
