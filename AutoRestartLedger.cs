using System.IO;
using System.Text.Json;

namespace SessionDeck;

/// <summary>
/// What each session was last auto-restarted for, kept on disk so a CLI that still reports its old
/// version after the restart is restarted once per version, not once per launch of the app. Two
/// kinds of target are tracked apart, a CLI version and a host protocol, so one kind's restart never
/// counts as the other's; within a kind only the current target's entries are kept.
/// </summary>
internal sealed class AutoRestartLedger
{
    public const string Cli = "cli";
    public const string HostProtocol = "host";

    readonly string _path;
    readonly Dictionary<string, string> _restartedFor;

    public AutoRestartLedger(string path)
    {
        _path = path;
        var saved = AtomicFile.Read(path, text => JsonSerializer.Deserialize<Dictionary<string, string>>(text)) ?? new();
        _restartedFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in saved) _restartedFor[key.Contains('|') ? key : Key(key, Cli)] = value;
    }

    public static string DefaultPath => Path.Combine(Settings.DataDir, "auto-restarts.json");

    static string Key(string sessionId, string kind) => sessionId + "|" + kind;

    public bool Done(string sessionId, string target, string kind = Cli) =>
        _restartedFor.TryGetValue(Key(sessionId, kind), out var done) && done == target;

    public void Record(string sessionId, string target, string kind = Cli)
    {
        foreach (var stale in _restartedFor.Where(kv => kv.Key.EndsWith("|" + kind, StringComparison.Ordinal) && kv.Value != target).Select(kv => kv.Key).ToList())
            _restartedFor.Remove(stale);
        _restartedFor[Key(sessionId, kind)] = target;
        try { AtomicFile.Write(_path, JsonSerializer.Serialize(_restartedFor)); }
        catch (Exception ex) { App.LogError(ex); }
    }
}
