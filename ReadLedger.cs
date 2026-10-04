namespace SessionDeck;

/// <summary>
/// When each session was last in view, by session id. A session that finished a turn after that
/// is unread: it has results waiting that have not been looked at. Lives in settings so the
/// badges mean the same thing after a relaunch as before it.
/// </summary>
internal sealed class ReadLedger
{
    readonly Dictionary<string, long> _readAt;

    public ReadLedger(Dictionary<string, long> readAt) { _readAt = readAt; }

    public static long Ms(DateTime at) => new DateTimeOffset(at.ToUniversalTime()).ToUnixTimeMilliseconds();

    /// <summary>Finished (at <paramref name="lastChanged"/>) later than it was last seen, or never seen.</summary>
    public bool IsUnread(string sessionId, DateTime lastChanged) =>
        sessionId.Length > 0 && (!_readAt.TryGetValue(sessionId, out var seen) || Ms(lastChanged) > seen);

    /// <summary>The session is in view now. Returns whether anything changed.</summary>
    public bool MarkRead(string sessionId, DateTime now)
    {
        if (sessionId.Length == 0) return false;
        long ms = Ms(now);
        if (_readAt.TryGetValue(sessionId, out var seen) && seen >= ms) return false;
        _readAt[sessionId] = ms;
        return true;
    }

    /// <summary>Forget sessions gone for longer than <paramref name="keep"/>; the live ones stay whatever their age.</summary>
    public int Prune(IEnumerable<string> liveSessionIds, DateTime now, TimeSpan keep)
    {
        var live = new HashSet<string>(liveSessionIds, StringComparer.OrdinalIgnoreCase);
        long cutoff = Ms(now - keep);
        var stale = _readAt.Where(kv => !live.Contains(kv.Key) && kv.Value < cutoff).Select(kv => kv.Key).ToList();
        foreach (var k in stale) _readAt.Remove(k);
        return stale.Count;
    }
}
