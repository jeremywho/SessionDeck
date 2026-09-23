using System.ComponentModel;
using System.Text.RegularExpressions;

namespace SessionDeck;

/// <summary>
/// Observable view-model wrapping a <see cref="SessionInfo"/> so the grid updates and re-sorts in
/// place as the 1s scan refreshes data.
/// </summary>
internal sealed class SessionRow : INotifyPropertyChanged
{
    /// <summary>Context-window size used as the Context % divisor — fixed at the 1M max.</summary>
    public static long ContextWindow = 1_000_000;

    public string SessionId { get; }
    SessionInfo _s;

    /// <summary>The deck host this row stands for. Rows exist only for hosts; the scanner's
    /// <see cref="SessionInfo"/> is what it knows about the CLI running inside the host, and it can be
    /// a placeholder before the CLI has registered itself.</summary>
    public Host.HostRecord Host { get; }

    public SessionRow(Host.HostRecord host, SessionInfo s)
    {
        Host = host;
        SessionId = host.Id;
        _s = s;
        _hosted = true;
    }

    /// <summary>A row with no real host behind it, for tests of the view-model's own derivations.</summary>
    public SessionRow(SessionInfo s) : this(new Host.HostRecord { Id = s.SessionId, SessionId = s.SessionId, Provider = s.Provider.ToString(), Cwd = s.Cwd, ChildPid = s.Pid }, s) { }

    /// <summary>What the scanner has not told us yet: a row for a host whose CLI has not written its
    /// registry entry (a Codex TUI before its first turn, a Claude that is still starting).</summary>
    public static SessionInfo Placeholder(Host.HostRecord h) => new()
    {
        Provider = string.Equals(h.Provider, "Codex", StringComparison.OrdinalIgnoreCase) ? SessionProvider.Codex : SessionProvider.Claude,
        Pid = h.ChildPid,
        SessionId = h.SessionId,
        Cwd = h.Cwd,
        Status = h.HasExited ? "exited" : "",
        Kind = "interactive",
        StartedAt = h.StartedAt,
        UpdatedAt = h.StartedAt,
    };

    public int Update(SessionInfo s)
    {
        var h = PropertyChanged;
        if (h == null) { _s = s; return 0; }

        // Raise only what actually changed. Every raise re-runs bindings and converters, and three
        // of these are live-sort keys the collection view re-evaluates per raise — so a blanket
        // raise on the 2s tick had the whole grid churning while nothing was visibly different.
        var before = new object?[Tracked.Length];
        for (int i = 0; i < Tracked.Length; i++) before[i] = Tracked[i].Get(this);
        _s = s;
        int changed = 0;
        for (int i = 0; i < Tracked.Length; i++)
            if (!Equals(before[i], Tracked[i].Get(this)))
            {
                IsolationTelemetry.PropertyChanged(Tracked[i].Name);
                h(this, new PropertyChangedEventArgs(Tracked[i].Name));
                changed++;
            }
        return changed;
    }

    static readonly (string Name, Func<SessionRow, object?> Get)[] Tracked =
    {
        (nameof(Pid), r => r.Pid),
        (nameof(Name), r => r.Name),
        (nameof(Status), r => r.Status),
        (nameof(State), r => r.State),
        (nameof(SortPriority), r => r.SortPriority),
        (nameof(ShortId), r => r.ShortId),
        (nameof(Model), r => r.Model),
        (nameof(ModelChip), r => r.ModelChip),
        (nameof(Effort), r => r.Effort),
        (nameof(HasEffort), r => r.HasEffort),
        (nameof(HasModel), r => r.HasModel),
        (nameof(ModelTooltip), r => r.ModelTooltip),
        (nameof(Provider), r => r.Provider),
        (nameof(ProviderGlyph), r => r.ProviderGlyph),
        (nameof(IsCodex), r => r.IsCodex),
        (nameof(ContextPct), r => r.ContextPct),
        (nameof(ContextDisplay), r => r.ContextDisplay),
        (nameof(ContextTokensDisplay), r => r.ContextTokensDisplay),
        (nameof(IdleDisplay), r => r.IdleDisplay),
        (nameof(LastTool), r => r.LastTool),
        (nameof(Cwd), r => r.Cwd),
        (nameof(Version), r => r.Version),
        (nameof(ApiError), r => r.ApiError),
        (nameof(RowTooltip), r => r.RowTooltip),
        (nameof(LastChanged), r => r.LastChanged),
        (nameof(SubagentsActive), r => r.SubagentsActive),
        (nameof(HasActiveSubagents), r => r.HasActiveSubagents),
        (nameof(SubagentTooltip), r => r.SubagentTooltip),
        (nameof(IsBackgroundAgent), r => r.IsBackgroundAgent),
    };

    public SessionInfo Info => _s;

    public int Pid => _s.Pid;
    public string Name =>
        IsBackgroundAgent ? CompanionName(_s.DisplayName)
        : _s.Name.Length > 0 ? _s.Name
        : _s.Title.Length > 0 && !LooksLikeId(_s.Title) ? DeckPane.DeckTab.StripMark(_s.Title)
        : Host.Title.Length > 0 && !LooksLikeId(Host.Title) ? DeckPane.DeckTab.StripMark(Host.Title)
        : _s.Provider == SessionProvider.Codex ? "Codex"
        : _s.SessionId.Length > 0 ? _s.ShortId
        : Host.Provider;

    /// <summary>A bare uuid or hex handle is not a name worth showing; the provider's word is.</summary>
    static bool LooksLikeId(string s) => Regex.IsMatch(s.Trim(), "^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$|^[0-9a-fA-F]{12,}$");

    // --- background agents (companion threads driven by another app, not a terminal) ---

    /// <summary>
    /// A thread another app is driving through the codex app server (the Claude Code codex plugin's
    /// "Codex Companion Task" second opinions, IDE extensions). Live and worth listing, but it has no
    /// terminal window — so double-click must not hunt for one, and the row wears an agent badge
    /// instead of pretending to be a session you could sit in.
    /// </summary>
    public bool IsBackgroundAgent => _s.Kind == "companion";

    /// <summary>
    /// The plugin names its threads "Codex Companion Task: &lt;task&gt;…"; the badge already says
    /// "agent", so the row shows just the task text. The raw name stays in the tooltip.
    /// </summary>
    internal static string CompanionName(string name)
    {
        const string prefix = "Codex Companion Task";
        string n = name.Trim();
        if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            n = n.Substring(prefix.Length).TrimStart(':', ' ');
            if (n.StartsWith("<task>", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6);
            n = n.Trim();
        }
        return n.Length > 0 ? n : "Codex agent";
    }
    public string Status => _s.Status;                            // raw status (optional column)
    public bool HostExited => Host.HasExited;
    public SessionState State => Host.HasExited ? SessionState.Idle : _s.ApiError ? SessionState.Error : SessionStateMap.FromStatus(_s.Status);
    public bool ApiError => _s.ApiError;

    /// <summary>When the status last changed — secondary sort key (most-recent-first within each group).</summary>
    public DateTime LastChanged => _s.StatusUpdatedAt > DateTime.MinValue ? _s.StatusUpdatedAt : _s.UpdatedAt;

    readonly bool _hosted;

    // --- virtual desktop (set by the throttled resolver, independent of the status scan) ---
    int _desktopIndex = -1;       // 0 = Desktop 1, 1 = Desktop 2, …; -1 = unknown
    bool _onCurrentDesktop = true;
    public int DesktopIndex => _desktopIndex;
    public bool OnOtherDesktop => _desktopIndex >= 0 && !_onCurrentDesktop;
    public string DesktopLabel => _desktopIndex >= 0 ? $"Desktop {_desktopIndex + 1}" : "";

    public void SetDesktop(int index, bool onCurrent)
    {
        if (_desktopIndex == index && _onCurrentDesktop == onCurrent) return;
        _desktopIndex = index; _onCurrentDesktop = onCurrent;
        var h = PropertyChanged;
        if (h == null) return;
        h(this, new PropertyChangedEventArgs(nameof(DesktopIndex)));
        h(this, new PropertyChangedEventArgs(nameof(OnOtherDesktop)));
        h(this, new PropertyChangedEventArgs(nameof(DesktopLabel)));
        h(this, new PropertyChangedEventArgs(nameof(RowTooltip)));
    }

    // --- background work this session has in flight ---

    /// <summary>
    /// One badge for everything this session has running elsewhere: its own Task subagents, plus any
    /// headless Codex threads it started (which used to appear as rows of their own that you couldn't
    /// click into — see <see cref="CodexAttribution"/>). They're counted together because the question
    /// the badge answers is "is this session waiting on something?", and the tooltip splits them.
    /// </summary>
    public int SubagentsActive => _s.SubagentsActive + _s.BackgroundTasks;
    public bool HasActiveSubagents => SubagentsActive > 0;

    public string SubagentTooltip
    {
        get
        {
            var parts = new List<string>();
            if (_s.SubagentsTotal > 0)
                parts.Add($"{_s.SubagentsActive} subagent{(_s.SubagentsActive == 1 ? "" : "s")} working · {_s.SubagentsTotal} this session");
            if (_s.BackgroundTasks > 0)
                parts.Add($"{_s.BackgroundTasks} Codex task{(_s.BackgroundTasks == 1 ? "" : "s")} running (no terminal)");
            return string.Join("\n", parts);
        }
    }

    /// <summary>Multi-line hover summary so you can eyeball anything odd (model / context / cwd / ids).</summary>
    public string RowTooltip
    {
        get
        {
            var lines = new List<string>
            {
                _s.DisplayName,
                $"Model: {(_s.Model.Length > 0 ? _s.Model : "—")}{(HasEffort ? $" · {_s.Effort} effort" : "")}",
            };
            if (IsBackgroundAgent)
                lines.Insert(1, "Background agent — driven by another app; no terminal window");
            if (Host.HasExited)
                lines.Insert(1, "Session ended — click the X to remove");
            lines.AddRange(new[]
            {
                $"Context: {ContextPct}% ({ContextTokensDisplay} of {Window / 1000}k)",
                $"Status: {_s.Status}{(_s.ApiError ? " · API ERROR" : "")}{(LastTool.Length > 0 ? $" · {LastTool}" : "")}",
                $"Folder: {_s.Cwd}",
                $"{ProviderName} · PID {_s.Pid} · {ShortId} · v{_s.Version}",
            });
            if (OnOtherDesktop) lines.Add($"On {DesktopLabel}");
            if (_s.SubagentsActive > 0) lines.Add($"Subagents: {_s.SubagentsActive} working / {_s.SubagentsTotal} this session");
            if (_s.ApiError && _s.ErrorText.Length > 0) lines.Add(_s.ErrorText);
            return string.Join("\n", lines);
        }
    }
    public int SortPriority => Host.HasExited ? 8 : State switch
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
    public bool HasModel => _s.Model.Length > 0;

    /// <summary>Reasoning effort of the latest turn. Both CLIs report one; empty means none was seen,
    /// which keeps the trailing text collapsed rather than rendering a stray separator.</summary>
    public string Effort => _s.Effort;
    public bool HasEffort => _s.Effort.Length > 0;
    public string ModelTooltip => HasModel
        ? $"{ProviderName} · {_s.Model}{(HasEffort ? $" · {_s.Effort} effort" : "")}"
        : "";

    // --- provider ---
    public SessionProvider Provider => _s.Provider;
    public bool IsCodex => _s.Provider == SessionProvider.Codex;
    public string ProviderName => _s.Provider == SessionProvider.Codex ? "Codex" : "Claude Code";
    /// <summary>Marker inside the model pill. Two shapes rather than two letters — both CLIs start
    /// with a C, so a glyph tells them apart faster than initials do.</summary>
    public string ProviderGlyph => _s.Provider == SessionProvider.Codex ? "◆" : "✳";

    /// <summary>This session's context size — the model's own when we know it (Codex reports it per
    /// turn), otherwise the app-wide default.</summary>
    long Window => _s.ContextWindow > 0 ? _s.ContextWindow : ContextWindow;

    public int ContextPct => Window > 0
        ? (int)Math.Min(100.0, Math.Round(_s.ContextTokens * 100.0 / Window, MidpointRounding.AwayFromZero))  // round (match the status line), not truncate
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

    /// <summary>
    /// Model id -> short pill text. The minor version is optional on purpose: ids run both
    /// <c>claude-opus-4-8</c> (-> "Opus 4.8") and <c>claude-opus-5</c> / <c>claude-fable-5</c>
    /// (-> "Opus 5" / "Fable 5"). Requiring the second number is what made the newer ids fall
    /// through and render raw. A trailing date (<c>claude-haiku-4-5-20251001</c>) and a bracketed
    /// variant (<c>claude-opus-5[1m]</c>) are both ignored — the tooltip carries the full id.
    /// </summary>
    static string FriendlyModel(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";

        string raw = id;
        int bracket = raw.IndexOf('[');
        if (bracket > 0) raw = raw.Substring(0, bracket);

        var m = Regex.Match(raw, @"\b(opus|sonnet|haiku|fable)-(\d+)(?:-(\d+))?", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            string fam = Title(m.Groups[1].Value);
            string ver = m.Groups[3].Success ? $"{m.Groups[2].Value}.{m.Groups[3].Value}" : m.Groups[2].Value;
            return $"{fam} {ver}";
        }

        // Codex: gpt-5.6-sol -> "Sol", gpt-5.6-codex -> "Codex", plain gpt-5.6 -> "GPT-5.6".
        // The named variant is the part that distinguishes them in practice, so it wins the pill.
        m = Regex.Match(raw, @"^gpt-([\d.]+)(?:-([a-z0-9]+))?$", RegexOptions.IgnoreCase);
        if (m.Success)
            return m.Groups[2].Success && m.Groups[2].Value.Length > 0
                ? Title(m.Groups[2].Value)
                : $"GPT-{m.Groups[1].Value}";

        return raw;
    }

    static string Title(string s) =>
        char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;
}
