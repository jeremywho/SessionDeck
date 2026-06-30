using System.ComponentModel;
using System.Text.RegularExpressions;

namespace ClaudeSessionMonitor;

/// <summary>
/// Observable view-model wrapping a <see cref="SessionInfo"/> so the grid updates and re-sorts in
/// place as the 1s scan refreshes data.
/// </summary>
internal sealed class SessionRow : INotifyPropertyChanged
{
    /// <summary>Context-window size used as the % divisor. Set from Settings at startup.</summary>
    public static long ContextWindow = 1_000_000;

    public string SessionId { get; }
    SessionInfo _s;

    public SessionRow(SessionInfo s) { SessionId = s.SessionId; _s = s; }

    public void Update(SessionInfo s)
    {
        _s = s;
        var h = PropertyChanged;
        if (h == null) return;
        foreach (var n in Tracked) h(this, new PropertyChangedEventArgs(n));
    }

    static readonly string[] Tracked =
    {
        nameof(Pid), nameof(Name), nameof(Status), nameof(State), nameof(SortPriority),
        nameof(ShortId), nameof(Model), nameof(ModelChip),
        nameof(ContextPct), nameof(ContextDisplay), nameof(ContextTokensDisplay),
        nameof(IdleDisplay), nameof(LastTool), nameof(Cwd), nameof(Version),
        nameof(ApiError), nameof(RowTooltip),
    };

    public SessionInfo Info => _s;

    public int Pid => _s.Pid;
    public string Name => _s.DisplayName;
    public string Status => _s.Status;                            // raw status (optional column)
    public SessionState State => _s.ApiError ? SessionState.Error : SessionStateMap.FromStatus(_s.Status);
    public bool ApiError => _s.ApiError;

    /// <summary>Multi-line hover summary so you can eyeball anything odd (model / context / cwd / ids).</summary>
    public string RowTooltip
    {
        get
        {
            var lines = new List<string>
            {
                _s.DisplayName,
                $"Model: {(ModelChip.Length > 0 ? ModelChip : "—")}",
                $"Context: {ContextPct}% ({ContextTokensDisplay})",
                $"Status: {_s.Status}{(_s.ApiError ? " · API ERROR" : "")}{(LastTool.Length > 0 ? $" · {LastTool}" : "")}",
                $"Folder: {_s.Cwd}",
                $"PID {_s.Pid} · {ShortId} · v{_s.Version}",
            };
            if (_s.ApiError && _s.ErrorText.Length > 0) lines.Add(_s.ErrorText);
            return string.Join("\n", lines);
        }
    }
    public int SortPriority => State switch
    {
        SessionState.Error => 0,      // API error / stuck — top
        SessionState.Awaiting => 1,   // needs your feedback
        SessionState.Working => 2,    // actively running
        SessionState.Completed => 3,  // done / idle — bottom
        SessionState.Idle => 4,
        _ => 9,
    };

    public string ShortId => _s.ShortId;
    public string Model => _s.Model;                              // raw model id (optional column)
    public string ModelChip => FriendlyModel(_s.Model);
    public int ContextPct => ContextWindow > 0
        ? (int)Math.Min(100.0, Math.Round(_s.ContextTokens * 100.0 / ContextWindow, MidpointRounding.AwayFromZero))  // round (match the status line), not truncate
        : 0;
    public string ContextDisplay => $"{ContextPct}%";
    public string ContextTokensDisplay => _s.ContextDisplay;      // e.g. "173.2k" (optional column)
    public string IdleDisplay => HumanizeIdle(_s.IdleSeconds);
    public string LastTool => _s.LastTool;
    public string Cwd => _s.Cwd;
    public string Version => _s.Version;

    static string HumanizeIdle(int sec)
    {
        if (sec < 0) return "";
        if (sec < 60) return $"{sec}s";
        if (sec < 3600) return $"{sec / 60}m";
        return $"{sec / 3600}h {sec % 3600 / 60}m";
    }

    static string FriendlyModel(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var m = Regex.Match(id, @"(opus|sonnet|haiku|fable)-(\d+)-(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return id;
        string fam = char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value.Substring(1).ToLowerInvariant();
        return $"{fam} {m.Groups[2].Value}.{m.Groups[3].Value}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
