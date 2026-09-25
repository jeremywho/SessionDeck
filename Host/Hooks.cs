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
            sb.Append($" -c 'hooks.{ev}=[{{hooks=[{{type=\"command\",command=\"{hookCommand}\",timeout={(ev is "Interrupt" or "SessionEnd" ? 3 : 10)}}}]}}]'");
        return sb.ToString();
    }

    /// <summary>Map a hook event to the status the deck shows.</summary>
    /// <summary>
    /// The status an event implies. <paramref name="pending"/> says the turn armed something that will
    /// wake the session by itself (see <see cref="Defers"/>): its end is then "scheduled", not idle.
    /// A "waiting" status means the session needs a person: a permission prompt, an elicitation, or
    /// a question put to the user. Claude's idle notification, sent a minute after any turn ends,
    /// carries no such meaning and changes nothing.
    /// </summary>
    public static string? StatusFor(string eventName, JsonElement root, bool pending) => eventName switch
    {
        "SessionStart" => StartSource(root) == "compact" ? null : "idle",
        "UserPromptSubmit" => "busy",
        "PreToolUse" => AsksUser(ToolName(root)) ? "waiting" : "busy",
        "PostToolUse" => "busy",
        "PermissionRequest" => "waiting",
        "Notification" => NotificationStatus(root),
        "Stop" => pending ? "scheduled" : "idle",
        "Interrupt" => "idle",
        "SessionEnd" => "ended",
        _ => null,
    };

    /// <summary>Why a SessionStart fired: startup, resume, clear, or compact. A compaction happens mid-turn and changes nothing.</summary>
    public static string StartSource(JsonElement root) =>
        root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";

    public static string ToolName(JsonElement root) =>
        root.TryGetProperty("tool_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";

    /// <summary>A tool that puts a question to the person; the session is waiting the moment it runs.</summary>
    public static bool AsksUser(string tool) => tool == "AskUserQuestion";

    /// <summary>
    /// A tool whose effect outlives the turn and is certain to wake the session later: a scheduled
    /// wake-up, a cron entry, a monitor. A shell command run in the background is deliberately not
    /// here: the hooks cannot see it finish, so it would pin the session as scheduled until the next
    /// prompt; the app watches such commands' output files instead.
    /// </summary>
    public static bool Defers(JsonElement root) => ToolName(root) is "ScheduleWakeup" or "CronCreate" or "Monitor";

    static string? NotificationStatus(JsonElement root)
    {
        if (root.TryGetProperty("notification_type", out var t) && t.ValueKind == JsonValueKind.String)
        {
            string s = t.GetString() ?? "";
            if (s.Contains("permission", StringComparison.OrdinalIgnoreCase) || s.Contains("elicit", StringComparison.OrdinalIgnoreCase)) return "waiting";
            return null;
        }
        if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
        {
            string s = m.GetString() ?? "";
            if (s.Contains("permission", StringComparison.OrdinalIgnoreCase)) return "waiting";
        }
        return null;
    }
}
