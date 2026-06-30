namespace ClaudeSessionMonitor;

/// <summary>A session recorded for possible restore after a crash/reboot.</summary>
internal sealed class SavedSession
{
    public string Id { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public long LastSeen { get; set; }   // unix ms
}
