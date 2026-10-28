using SessionDeck.Host;

namespace SessionDeck;

/// <summary>
/// Which scanned session each deck host is running. A session belongs to one host: by id for
/// Claude, whose id the deck chose, else by process descent for Codex, which names its own thread.
/// Hosts still running claim before hosts whose CLI has exited, so an old record left behind by a
/// restart or a reboot cannot take the session away from the host that actually runs it now.
/// </summary>
internal static class HostPairing
{
    public static List<(HostRecord Host, SessionInfo Info)> Pair(
        IEnumerable<HostRecord> hosts, IReadOnlyList<SessionInfo> live, Func<SessionInfo, HostRecord, bool> descends, HashSet<SessionInfo> claimed)
    {
        var pairs = new List<(HostRecord, SessionInfo)>();
        foreach (var h in hosts.OrderBy(h => h.HasExited).ThenByDescending(h => h.StartedAt))
        {
            SessionInfo? match = null;
            if (h.SessionId.Length > 0)
                match = live.FirstOrDefault(s => !claimed.Contains(s) && string.Equals(s.SessionId, h.SessionId, StringComparison.OrdinalIgnoreCase));
            match ??= live.FirstOrDefault(s => !claimed.Contains(s) && s.Pid > 0 && CanDescend(s, h) && descends(s, h));
            if (match != null) claimed.Add(match);
            pairs.Add((h, match ?? SessionRow.Placeholder(h)));
        }
        return pairs;
    }

    /// <summary>
    /// Descent says "runs inside this pane", which is only the pane's own session for a CLI the
    /// scanners see. An agy pane is never scanned, so anything under it is a tool agy ran; and a
    /// headless Codex thread (an exec, a companion task) is a tool of whatever ran it, never the
    /// session a person sits in front of. Seen live: an agy pane's codex exec took over its row.
    /// </summary>
    public static bool CanDescend(SessionInfo s, HostRecord h) =>
        !string.Equals(h.Provider, "Agy", StringComparison.OrdinalIgnoreCase)
        && !(s.Provider == SessionProvider.Codex && s.Kind != "tui");
}
