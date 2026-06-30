namespace ClaudeSessionMonitor;

/// <summary>One live Claude Code (or compatible) session.</summary>
internal sealed class SessionInfo
{
    public int Pid;
    public string SessionId = "";
    public string Cwd = "";
    public string Name = "";       // user/AI session name from sessions/<pid>.json
    public string Status = "";     // "busy" / "idle" / ... (treat as open set)
    public string Version = "";
    public string Kind = "";
    public string Model = "";      // from latest assistant turn
    public string LastTool = "";   // last tool_use block seen
    public string Title = "";      // Claude-set terminal title (ai-title / custom-title) -> used for tab matching
    public long ContextTokens;     // input + cache_read + cache_creation of latest assistant turn
    public long OutputTokens;      // output tokens of latest assistant turn
    public bool ApiError;          // most recent assistant message is a synthetic API-error message
    public string ErrorText = "";  // the error text (e.g. "API Error: … Rate limited")
    public DateTime StartedAt;
    public DateTime UpdatedAt;
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
