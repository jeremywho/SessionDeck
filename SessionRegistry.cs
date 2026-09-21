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
    static readonly string Dir =
        Environment.GetEnvironmentVariable("SD_DATA_DIR") is { Length: > 0 } dir ? dir :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionDeck");
    static readonly string FilePath = Path.Combine(Dir, "active-sessions.json");

    static string _lastSig = "";
    static DateTime _lastWriteUtc = DateTime.MinValue;

    /// <summary>
    /// How often an UNCHANGED live set still gets rewritten so LastSeen keeps advancing. Without
    /// this, a set that stayed identical for longer than the 7-day restore cutoff aged itself out
    /// of the restore offer while every session in it was alive the whole time.
    /// </summary>
    public static readonly TimeSpan LastSeenCheckpoint = TimeSpan.FromHours(12);

    /// <summary>Test seam: forget the last-written signature/time so a test starts from a clean slate.</summary>
    internal static void ResetForTests() { _lastSig = ""; _lastWriteUtc = DateTime.MinValue; }

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
    public static void Snapshot(IReadOnlyList<SessionInfo> liveInteractive)
    {
        if (Frozen) return;

        var parts = new List<string>(liveInteractive.Count);
        foreach (var s in liveInteractive)
            parts.Add($"{s.SessionId}|{s.DisplayName}|{s.Cwd}|{s.Model}|{(int)s.Provider}");
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
            Provider = s.Provider,
        }).ToList();

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
