using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeSessionMonitor;

/// <summary>
/// Persists the set of currently-live interactive sessions to active-sessions.json so they can be
/// offered for restore after a crash/reboot. Snapshot-on-change: the file always mirrors the last
/// live set this app saw — so a crash (app dies too) leaves exactly what was running, while a
/// graceful close is removed on the next scan.
/// </summary>
internal static class SessionRegistry
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSessionMonitor");
    static readonly string FilePath = Path.Combine(Dir, "active-sessions.json");

    static HashSet<string> _lastIds = new();

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
        var ids = new HashSet<string>(liveInteractive.Select(s => s.SessionId));
        if (ids.SetEquals(_lastIds)) return;
        _lastIds = ids;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var saved = liveInteractive.Select(s => new SavedSession
        {
            Id = s.SessionId, Cwd = s.Cwd, Name = s.DisplayName, Model = s.Model, LastSeen = now,
        }).ToList();

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
