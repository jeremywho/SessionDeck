using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeSessionMonitor;

/// <summary>
/// Persists the set of currently-live interactive sessions to active-sessions.json so they can be
/// offered for restore after a crash/reboot. Snapshot-on-change: the file always mirrors the last
/// live set this app saw — so a crash (app dies too) leaves exactly what was running, while a
/// graceful close is removed on the next scan. Writes stop once <see cref="Frozen"/> is set at OS
/// session end — an OS shutdown otherwise looks like every session closing gracefully at once.
/// </summary>
internal static class SessionRegistry
{
    // CSM_DATA_DIR: test hook — points the registry at a scratch dir so tests never touch the
    // real %APPDATA% file (the installed instance may be rewriting it at that very moment).
    static readonly string Dir =
        Environment.GetEnvironmentVariable("CSM_DATA_DIR") is { Length: > 0 } dir ? dir :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSessionMonitor");
    static readonly string FilePath = Path.Combine(Dir, "active-sessions.json");

    static HashSet<string> _lastIds = new();

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

    /// <summary>Rewrite the file only when the live interactive id-set changes.</summary>
    public static void Snapshot(IReadOnlyList<SessionInfo> liveInteractive)
    {
        if (Frozen) return;

        var ids = new HashSet<string>(liveInteractive.Select(s => s.SessionId));
        if (ids.SetEquals(_lastIds)) return;
        _lastIds = ids;

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
