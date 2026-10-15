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
            match ??= live.FirstOrDefault(s => !claimed.Contains(s) && s.Pid > 0 && descends(s, h));
            if (match != null) claimed.Add(match);
            pairs.Add((h, match ?? SessionRow.Placeholder(h)));
        }
        return pairs;
    }
}
