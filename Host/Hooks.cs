using System.IO;
using System.Text;
using System.Text.Json;

namespace SessionDeck.Host;

/// <summary>
/// Per-launch hook wiring. Neither CLI's user config is touched: Claude takes a settings file on
/// its command line, Codex takes inline <c>-c</c> overrides. Both point every event at
/// <c>SessionDeck.exe --hook &lt;port&gt; &lt;token&gt;</c>, which forwards the event to this host.
/// </summary>
internal static class Hooks
{
    public const string CommandPlaceholder = "{SD_HOOK_CMD}";
    public const string ClaudeSettingsPlaceholder = "{SD_CLAUDE_SETTINGS}";

    static readonly string[] ClaudeEvents = { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd" };
    /// <summary>No tool events for Codex: its hooks run synchronously (an <c>async</c> handler never
    /// fires, measured on 0.155.1), so each one costs a forwarder launch, and the rollout scanner
    /// already reports Codex's last tool.</summary>
    static readonly string[] CodexEvents = { "SessionStart", "UserPromptSubmit", "PermissionRequest", "Stop", "Interrupt", "SessionEnd" };

    /// <summary>The hook command as a shell-neutral string: forward slashes, no spaces needing quotes
    /// inside the exe path are assumed safe because both CLIs run it through a shell that honours the
    /// surrounding quotes we add per format.</summary>
    public static string HookCommand(string exe, int port, string token) =>
        $"{exe.Replace('\\', '/')} --hook {port} {token}";

    /// <summary>Claude: a settings JSON that registers every event as an async hook.</summary>
    public static string ClaudeSettingsJson(string hookCommand)
    {
        string cmd = "'" + hookCommand.Split(' ', 2)[0] + "' " + hookCommand.Split(' ', 2)[1];
        var events = new Dictionary<string, object>();
        foreach (var ev in ClaudeEvents)
            events[ev] = new object[] { new { hooks = new object[] { new { type = "command", command = cmd, async = true, timeout = 10 } } } };
        return JsonSerializer.Serialize(new { hooks = events }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Codex: one <c>-c</c> override per event, each a TOML array literal. Single-quoted at
    /// the PowerShell level so the double quotes inside reach codex untouched.</summary>
    public static string CodexArgs(string hookCommand)
    {
        var sb = new StringBuilder("--dangerously-bypass-hook-trust");
        foreach (var ev in CodexEvents)
            sb.Append($" -c 'hooks.{ev}=[{{hooks=[{{type=\"command\",command=\"{hookCommand}\",timeout=10}}]}}]'");
        return sb.ToString();
    }

    /// <summary>Map a hook event to the status the deck shows.</summary>
    public static string? StatusFor(string eventName, JsonElement root) => eventName switch
    {
        "SessionStart" => "idle",
        "UserPromptSubmit" => "busy",
        "PreToolUse" => "busy",
        "PostToolUse" => "busy",
        "PermissionRequest" => "waiting",
        "Notification" => NotificationIsPrompt(root) ? "waiting" : null,
        "Stop" => "idle",
        "Interrupt" => "idle",
        "SessionEnd" => "ended",
        _ => null,
    };

    static bool NotificationIsPrompt(JsonElement root)
    {
        if (root.TryGetProperty("notification_type", out var t) && t.ValueKind == JsonValueKind.String)
        {
            string s = t.GetString() ?? "";
            return s.Contains("permission", StringComparison.OrdinalIgnoreCase) || s.Contains("idle", StringComparison.OrdinalIgnoreCase) || s.Contains("elicit", StringComparison.OrdinalIgnoreCase);
        }
        if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
        {
            string s = m.GetString() ?? "";
            return s.Contains("permission", StringComparison.OrdinalIgnoreCase) || s.Contains("waiting", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }
}
