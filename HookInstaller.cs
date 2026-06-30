using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSessionMonitor;

/// <summary>
/// Installs/removes our own hook entries in ~/.claude/settings.json so Claude Code pings the
/// HookServer on key events. Merges alongside any existing hooks; our entries are tagged with a
/// marker URL path so removal touches only ours. Backs the file up once before the first edit.
/// </summary>
internal static class HookInstaller
{
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    const string Marker = "/csm/";   // our hook commands contain this path segment
    static readonly string[] Events = { "UserPromptSubmit", "PostToolUse", "Notification", "Stop", "SessionStart", "SessionEnd" };

    static string Command(string ev) => $"curl -s -m 1 -X POST http://127.0.0.1:{HookServer.Port}{Marker}{ev}";

    public static void Install()
    {
        var root = Load();
        if (root == null) return;
        Backup();

        if (root["hooks"] is not JsonObject hooks) { hooks = new JsonObject(); root["hooks"] = hooks; }

        foreach (var ev in Events)
        {
            if (hooks[ev] is not JsonArray arr) { arr = new JsonArray(); hooks[ev] = arr; }
            RemoveOurs(arr);

            var inner = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = Command(ev) } };
            var group = new JsonObject();
            if (ev == "PostToolUse") group["matcher"] = "*";
            group["hooks"] = inner;
            arr.Add(group);
        }
        Save(root);
    }

    public static void Uninstall()
    {
        var root = Load();
        if (root == null || root["hooks"] is not JsonObject hooks) return;
        foreach (var ev in Events)
            if (hooks[ev] is JsonArray arr) RemoveOurs(arr);
        Save(root);
    }

    static void RemoveOurs(JsonArray arr)
    {
        for (int i = arr.Count - 1; i >= 0; i--)
            if (arr[i]?["hooks"] is JsonArray inner && inner.Any(IsOurs))
                arr.RemoveAt(i);
    }

    static bool IsOurs(JsonNode? h)
    {
        try { return h?["command"]?.GetValue<string>()?.Contains(Marker) == true; }
        catch { return false; }
    }

    static JsonObject? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new JsonObject();
            var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            return JsonNode.Parse(File.ReadAllText(SettingsPath), null, opts) as JsonObject;
        }
        catch { return null; }   // unparseable -> abort, change nothing
    }

    static void Save(JsonObject root)
    {
        try { File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    static void Backup()
    {
        try
        {
            var bak = SettingsPath + ".csm-backup";
            if (File.Exists(SettingsPath) && !File.Exists(bak)) File.Copy(SettingsPath, bak);
        }
        catch { }
    }
}
