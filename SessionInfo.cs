namespace ClaudeSessionMonitor;

/// <summary>Which CLI a session belongs to. Drives the provider badge and the launch/restore paths —
/// the two scanners normalise very different on-disk shapes into one <see cref="SessionInfo"/>.</summary>
internal enum SessionProvider { Claude, Codex }

/// <summary>One live Claude Code (or compatible) session.</summary>
internal sealed class SessionInfo
{
    public SessionProvider Provider = SessionProvider.Claude;
    public int Pid;
    public string SessionId = "";
    public string Cwd = "";
    public string Name = "";       // user/AI session name from sessions/<pid>.json (Codex: session_index.jsonl)
    public string Status = "";     // "busy" / "idle" / ... (treat as open set)
    public string Version = "";
    public string Kind = "";
    public string Model = "";      // from latest assistant turn (Codex: latest turn_context)
    public string Effort = "";     // reasoning effort of the latest turn ("high", "max", "xhigh", "ultra", …)
    public string LastTool = "";   // last tool_use block seen
    public string Title = "";      // Claude-set terminal title (ai-title / custom-title) -> used for tab matching
    public long ContextTokens;     // input + cache_read + cache_creation of latest assistant turn
    public long ContextWindow;     // this model's context size; 0 = use the app-wide default
    public long OutputTokens;      // output tokens of latest assistant turn
    public bool ApiError;          // most recent assistant message is a synthetic API-error message
    public string ErrorText = "";  // the error text (e.g. "API Error: … Rate limited")
    public int SubagentsActive;    // subagent transcript files touched in the last ~30s (working now)
    public int SubagentsTotal;     // subagent files this session has spawned (cumulative)

    /// <summary>Headless Codex threads this Claude session started (companion second opinions, exec
    /// runs) — rolled onto the parent row by <see cref="CodexAttribution"/> instead of listed on their
    /// own, since there's no terminal to click into.</summary>
    public int BackgroundTasks;
    public DateTime StartedAt;
    public DateTime UpdatedAt;
    public DateTime StatusUpdatedAt;   // when the status field last changed (sessions/<pid>.json)
    public string TranscriptPath = "";

    public string ShortId => SessionId.Length >= 8 ? SessionId.Substring(0, 8) : SessionId;

    public string DisplayName =>
        !string.IsNullOrEmpty(Name) ? Name :
        Title.Length > 0 ? Title : ShortId;

    public int IdleSeconds =>
        UpdatedAt == DateTime.MinValue ? -1 : Math.Max(0, (int)(DateTime.Now - UpdatedAt).TotalSeconds);

    public string ContextDisplay =>
        ContextTokens >= 1000 ? $"{ContextTokens / 1000.0:0.0}k" : ContextTokens.ToString();
}
