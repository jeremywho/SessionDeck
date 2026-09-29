using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

namespace SessionDeck;

/// <summary>
/// Keeps the pull requests column current: GitHub every minute while the window is visible (five
/// while hidden), worktrees every two minutes, attribution every 15 s, each single-flight and off the
/// dispatcher. All state lives on the dispatcher; only the passes' work runs on the thread pool.
/// </summary>
internal sealed class PrPaneController
{
    static readonly TimeSpan VisibleInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan HiddenInterval = TimeSpan.FromMinutes(5);
    static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);
    static readonly TimeSpan AttributionInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan WorktreeInterval = TimeSpan.FromMinutes(2);
    static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(60);
    static readonly TimeSpan MergeabilityRetry = TimeSpan.FromSeconds(3);
    internal static readonly JsonSerializerOptions BoardJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly DeckPane _deck;
    readonly Settings _settings;
    readonly Func<IReadOnlyList<SessionSource>> _sessions;
    readonly Func<string, AgentView?> _describe;
    readonly Action<string> _focus;
    readonly DispatcherTimer _githubTimer = new() { Interval = VisibleInterval };
    readonly DispatcherTimer _attributionTimer = new() { Interval = AttributionInterval };
    readonly DispatcherTimer _worktreeTimer = new() { Interval = WorktreeInterval };
    readonly SingleFlight _github;
    readonly SingleFlight _worktreePass;
    readonly SingleFlight _attributionPass;
    readonly ToolCallCache _calls = new();
    DateTime _lastGithubStart = DateTime.MinValue;
    PrSnapshot? _shown;
    string _error = "";
    string _heads = "";
    IReadOnlyList<Worktree> _worktrees = Array.Empty<Worktree>();
    IReadOnlyDictionary<string, LocalState> _local = new Dictionary<string, LocalState>();
    IReadOnlyList<AgentLink> _links = Array.Empty<AgentLink>();
    string _posted = "";

    public PrPaneController(DeckPane deck, Settings settings, Func<IReadOnlyList<SessionSource>> sessions, Func<string, AgentView?> describe, Action<string> focus)
    {
        _deck = deck;
        _settings = settings;
        _sessions = sessions;
        _describe = describe;
        _focus = focus;
        _github = new SingleFlight(FetchGithubAsync);
        _worktreePass = new SingleFlight(RefreshWorktreesAsync);
        _attributionPass = new SingleFlight(RefreshAttributionAsync);
        _githubTimer.Tick += (_, _) => RequestGithub(force: false);
        _attributionTimer.Tick += (_, _) => _ = _attributionPass.RunAsync();
        _worktreeTimer.Tick += (_, _) => _ = _worktreePass.RunAsync();
        deck.PrMessage += OnPageMessage;
        deck.PageReloaded += () => { _posted = ""; Publish(); };
        deck.LayoutChanged += () => { if (_deck.HasPrPane && _shown == null) RequestGithub(force: false); };
    }

    public void Start()
    {
        _githubTimer.Start();
        _attributionTimer.Start();
        _worktreeTimer.Start();
        RequestGithub(force: false);
    }

    public void OnVisibilityChanged(bool visible)
    {
        _githubTimer.Interval = visible ? VisibleInterval : HiddenInterval;
        if (visible) RequestGithub(force: false);
    }

    internal static bool PastMinGap(DateTime lastStartUtc, DateTime nowUtc) => nowUtc - lastStartUtc >= MinGap;

    internal static string StateToken(SessionState state) => state switch
    {
        SessionState.Working => "working",
        SessionState.Awaiting => "awaiting",
        SessionState.Error => "error",
        SessionState.Scheduled => "scheduled",
        _ => "idle",
    };

    internal static List<SessionSource> SourcesFrom(IEnumerable<SessionInfo> sessions) =>
        sessions.Where(s => s.SessionId.Length > 0 && s.TranscriptPath.Length > 0)
            .GroupBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(s => new SessionSource(s.SessionId, s.Provider, s.Kind, s.Cwd, s.UpdatedAt, s.TranscriptPath,
                s.FoldedThreads.Select(f => new FoldedSource(f.TranscriptPath, f.Provider, f.Kind, f.Cwd, f.UpdatedAt)).ToList()))
            .ToList();

    internal static AgentView DescribeUnowned(SessionInfo s) =>
        new(s.Provider == SessionProvider.Codex ? $"Codex {s.Kind} {s.ShortId}" : s.DisplayName, s.Provider.ToString(), "working", Clickable: false);

    void RequestGithub(bool force)
    {
        if (!_deck.HasPrPane || (!force && !PastMinGap(_lastGithubStart, DateTime.UtcNow))) return;
        _ = _github.RunAsync();
    }

    async Task FetchGithubAsync()
    {
        _lastGithubStart = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var snap = await Task.Run(() => PrSource.FetchAsync(a => GhCli.RunAsync(a, GhTimeout), DateTime.UtcNow, MergeabilityRetry));
        if (sw.ElapsedMilliseconds >= 5000) PerformanceLog.Write($"pr-github {sw.ElapsedMilliseconds}ms prs={snap.Prs.Count} status={snap.Status}");
        bool ok = snap.Status == PrSourceStatus.Ok;
        _error = ok ? "" : snap.Error;
        var current = ok || _shown is not { Status: PrSourceStatus.Ok } ? snap : _shown;
        _shown = current;
        if (ok)
        {
            var live = PrBoardBuilder.SeriesKeys(snap.Prs);
            if (_settings.PrExpandedSeries.RemoveAll(k => !live.Contains(k)) > 0) _settings.Save();
        }
        string heads = string.Join("|", current.Prs.Select(p => $"{p.Key.Repo}#{p.Key.Number}={p.HeadOid}").Order(StringComparer.Ordinal));
        bool moved = heads != _heads;
        _heads = heads;
        Publish();
        if (moved) _ = _worktreePass.RunAsync();
    }

    async Task RefreshWorktreesAsync()
    {
        if (!_deck.HasPrPane || _shown is not { Status: PrSourceStatus.Ok } shown) return;
        var roots = _settings.PrRepoRoots.ToList();
        var sw = Stopwatch.StartNew();
        var (worktrees, local) = await Task.Run(() => WorktreeIndex.Scan(shown.Prs, roots));
        if (sw.ElapsedMilliseconds >= 2000) PerformanceLog.Write($"pr-worktrees {sw.ElapsedMilliseconds}ms worktrees={worktrees.Count} local={local.Count}");
        _worktrees = worktrees;
        _local = local;
        Publish();
        _ = _attributionPass.RunAsync();
    }

    async Task RefreshAttributionAsync()
    {
        if (!_deck.HasPrPane || _shown is not { Status: PrSourceStatus.Ok } shown) return;
        var sessions = _sessions();
        var targets = PrTargets.From(shown.Prs, _worktrees);
        var sw = Stopwatch.StartNew();
        var links = await Task.Run(() => PrAttribution.Attribute(sessions, targets, _calls, DateTime.UtcNow));
        if (sw.ElapsedMilliseconds >= 1000) PerformanceLog.Write($"pr-attribution {sw.ElapsedMilliseconds}ms sessions={sessions.Count} links={links.Count}");
        _links = links;
        Publish();
    }

    /// <summary>Post the board only when its value changed, so an unchanged refresh neither re-renders nor closes an open tooltip.</summary>
    void Publish()
    {
        var board = PrBoardBuilder.Build(_shown, _error, _worktrees, _local, _links, _describe,
            new HashSet<string>(_settings.PrExpandedSeries, StringComparer.Ordinal), DateTime.UtcNow);
        string json = JsonSerializer.Serialize(board, BoardJson);
        if (json == _posted) return;
        _posted = json;
        using var doc = JsonDocument.Parse(json);
        _deck.PostToPage(new { type = "prs", board = doc.RootElement.Clone() });
    }

    void OnPageMessage(string type, JsonElement root)
    {
        switch (type)
        {
            case "prRefresh":
                RequestGithub(force: true);
                _ = _worktreePass.RunAsync();
                break;
            case "focusSession":
                if (Str(root, "sessionId") is { Length: > 0 } id) _focus(id);
                break;
            case "prToggleSeries":
                if (Str(root, "key") is not { Length: > 0 } key) break;
                if (!_settings.PrExpandedSeries.Remove(key)) _settings.PrExpandedSeries.Add(key);
                _settings.Save();
                Publish();
                break;
        }
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
