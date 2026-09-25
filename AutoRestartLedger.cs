using System.IO;
using System.Text.Json;

namespace SessionDeck;

/// <summary>
/// Which installed CLI version each session was last auto-restarted for, kept on disk so a CLI that
/// still reports its old version after the restart is restarted once per version, not once per
/// launch of the app. Only the current version's entries are kept.
/// </summary>
internal sealed class AutoRestartLedger
{
    readonly string _path;
    readonly Dictionary<string, string> _restartedFor;

    public AutoRestartLedger(string path)
    {
        _path = path;
        var saved = AtomicFile.Read(path, text => JsonSerializer.Deserialize<Dictionary<string, string>>(text));
        _restartedFor = new Dictionary<string, string>(saved ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    public static string DefaultPath => Path.Combine(Settings.DataDir, "auto-restarts.json");

    public bool Done(string sessionId, string version) =>
        _restartedFor.TryGetValue(sessionId, out var done) && done == version;

    public void Record(string sessionId, string version)
    {
        foreach (var stale in _restartedFor.Where(kv => kv.Value != version).Select(kv => kv.Key).ToList())
            _restartedFor.Remove(stale);
        _restartedFor[sessionId] = version;
        try { AtomicFile.Write(_path, JsonSerializer.Serialize(_restartedFor)); }
        catch (Exception ex) { App.LogError(ex); }
    }
}
