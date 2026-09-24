namespace SessionDeck;

/// <summary>The display states the UI color-codes and sorts by (attention-first). Scheduled: the turn
/// ended but something it armed (a wake-up, a background command, a monitor) will bring it back.</summary>
internal enum SessionState { Completed, Awaiting, Idle, Working, Error, Scheduled }

internal static class SessionStateMap
{
    /// <summary>
    /// Maps Claude Code's raw session status to a display state.
    /// busy → Working · waiting → Awaiting · idle → Completed · shell → Working (background shell/lane
    /// work in flight — the agent often "holds" idle while background bash shells keep running).
    /// </summary>
    public static SessionState FromStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "busy" => SessionState.Working,
        "waiting" => SessionState.Awaiting,
        "scheduled" => SessionState.Scheduled,
        "idle" => SessionState.Completed,
        "shell" => SessionState.Working,
        _ => SessionState.Idle,
    };
}
