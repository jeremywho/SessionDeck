using System.IO;
using System.Linq;
using System.Text.Json;

namespace SessionDeck;

/// <summary>
/// Persists the set of currently-live interactive sessions to active-sessions.json so they can be
/// offered for restore after a crash/reboot. Snapshot-on-change: the file always mirrors the last
/// live set this app saw — so a crash (app dies too) leaves exactly what was running, while a
/// graceful close is removed on the next scan. Writes stop once <see cref="Frozen"/> is set at OS
/// session end — an OS shutdown otherwise looks like every session closing gracefully at once.
/// </summary>
internal static class SessionRegistry
{
    // SD_DATA_DIR: test hook — points the registry at a scratch dir so tests never touch the
    // real %APPDATA% file (the installed instance may be rewriting it at that very moment).
    static readonly string Dir = Settings.DataDir;
    static readonly string FilePath = Path.Combine(Dir, "active-sessions.json");

    static string _lastSig = "";
    static DateTime _lastWriteUtc = DateTime.MinValue;
    static Dictionary<string, long>? _settled;

    /// <summary>
    /// How often an UNCHANGED live set still gets rewritten so LastSeen keeps advancing. Without
    /// this, a set that stayed identical for longer than the 7-day restore cutoff aged itself out
    /// of the restore offer while every session in it was alive the whole time.
    /// </summary>
    public static readonly TimeSpan LastSeenCheckpoint = TimeSpan.FromHours(12);

    /// <summary>Test seam: forget the last-written signature/time so a test starts from a clean slate.</summary>
    internal static void ResetForTests() { _lastSig = ""; _lastWriteUtc = DateTime.MinValue; _settled = null; }

    /// <summary>
    /// When the session last settled (its last status change while not working), as last recorded.
    /// Remembered past the session leaving the live set, because a restart takes it out and brings
    /// it back under a new host. MinValue when unknown.
    /// </summary>
    public static DateTime SettledAt(string sessionId)
    {
        var settled = Settled();
        return sessionId.Length > 0 && settled.TryGetValue(sessionId, out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
            : DateTime.MinValue;
    }

    static Dictionary<string, long> Settled()
    {
        if (_settled != null) return _settled;
        _settled = new(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Load())
            if (s.LastChanged > 0 && s.Id.Length > 0) _settled[s.Id] = s.LastChanged;
        return _settled;
    }

    /// <summary>
    /// Set when the OS announces the end of the interactive session (shutdown/restart/logoff).
    /// The claude processes die before this app does, so later scans see an emptying live set;
    /// persisting that would wipe exactly the state the post-reboot restore offer needs.
    /// </summary>
    public static bool Frozen;

    public static List<SavedSession> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<SavedSession>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>Rewrite the file when anything about the live interactive set changes (not just the
    /// id-set — a rename or cwd change must persist too), plus a periodic checkpoint for LastSeen.</summary>
    public static void Snapshot(IReadOnlyList<SessionInfo> liveInteractive, Func<SessionInfo, DateTime?>? settledAt = null)
    {
        if (Frozen) return;

        var settled = Settled();
        foreach (var s in liveInteractive)
            if (settledAt?.Invoke(s) is DateTime at && at > DateTime.MinValue)
                settled[s.SessionId] = new DateTimeOffset(at).ToUnixTimeMilliseconds();

        var parts = new List<string>(liveInteractive.Count);
        foreach (var s in liveInteractive)
            parts.Add($"{s.SessionId}|{s.DisplayName}|{s.Cwd}|{s.Model}|{(int)s.Provider}|{settled.GetValueOrDefault(s.SessionId)}");
        parts.Sort(StringComparer.Ordinal);
        string sig = string.Join("\n", parts);

        var nowUtc = DateTime.UtcNow;
        bool checkpoint = liveInteractive.Count > 0 && nowUtc - _lastWriteUtc >= LastSeenCheckpoint;
        if (sig == _lastSig && !checkpoint) return;
        _lastSig = sig;
        _lastWriteUtc = nowUtc;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var saved = liveInteractive.Select(s => new SavedSession
        {
            Id = s.SessionId, Cwd = s.Cwd, Name = s.DisplayName, Model = s.Model, LastSeen = now,
            Provider = s.Provider, LastChanged = settled.GetValueOrDefault(s.SessionId),
        }).ToList();

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
