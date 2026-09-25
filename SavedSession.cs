using System.Text.Json.Serialization;

namespace SessionDeck;

/// <summary>A session recorded for possible restore after a crash/reboot.</summary>
internal sealed class SavedSession
{
    public string Id { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public long LastSeen { get; set; }   // unix ms

    /// <summary>Unix ms of the session's last status change while settled; orders it in its group after a resume. 0 = unknown.</summary>
    public long LastChanged { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    /// <summary>
    /// Which CLI to resume with. Written as a name rather than the enum's number — this file is
    /// indented for humans and "Provider": 1 would mean nothing. Absent in files written before Codex
    /// support, which deserializes to Claude: the only thing those entries could have been.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SessionProvider Provider { get; set; } = SessionProvider.Claude;
}
