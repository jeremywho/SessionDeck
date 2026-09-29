namespace SessionDeck;

internal enum NextAction { Conflicts, CiFailing, ChangesRequested, OpenThreads, BehindBase, WaitingOnParent, CiRunning, MergeUnknown, Draft, NeedsReview, ReadyToMerge }

internal sealed record AgentView(string Name, string Provider, string State, bool Clickable = true);

internal sealed record BoardAgent(string SessionId, string Name, string Provider, string State, bool ViaCodex, bool Clickable, string Tip);

internal sealed record BoardCheck(string Name, string State, DateTime? StartedAt);

internal sealed record BoardRow
{
    public string Key { get; init; } = "";
    public int Depth { get; init; }
    public string Ci { get; init; } = "none";
    public IReadOnlyList<BoardCheck> Checks { get; init; } = Array.Empty<BoardCheck>();
    public string Repo { get; init; } = "";
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Action { get; init; } = "";
    public string ActionTip { get; init; } = "";
    public int Band { get; init; }
    public IReadOnlyList<BoardAgent> Agents { get; init; } = Array.Empty<BoardAgent>();
    public DateTime CreatedAt { get; init; }
    public DateTime? LastCommitAt { get; init; }
    public string Local { get; init; } = "";
    public string LocalTip { get; init; } = "";
    public string Marker { get; init; } = "";
    public int SeriesCount { get; init; }
    public bool Expanded { get; init; }
    public IReadOnlyList<BoardRow> Members { get; init; } = Array.Empty<BoardRow>();
}

internal sealed record BoardSection(string Name, int Count, IReadOnlyList<BoardRow> Rows);

internal sealed record Board(string Status, string Error, IReadOnlyList<string> Warnings, DateTime? FetchedAt, int StaleAfterSeconds, int Total,
                             IReadOnlyList<BoardSection> Sections);

internal static class PrBoardBuilder
{
    /// <summary>Two and a half missed 60 s polls: the same rule the usage bar uses.</summary>
    public const int StaleAfterSeconds = 150;

    sealed record Item(bool Draft, int Band, string Repo, int Number, List<BoardRow> Rows);

    public static Board Build(PrSnapshot? shown, string error, IReadOnlyList<Worktree> worktrees, IReadOnlyDictionary<string, LocalState> local,
                              IReadOnlyList<AgentLink> links, Func<string, AgentView?> describe, IReadOnlySet<string> expanded)
    {
        if (shown == null) return new Board("loading", "", Array.Empty<string>(), null, StaleAfterSeconds, 0, Array.Empty<BoardSection>());
        string status = shown.Status switch
        {
            PrSourceStatus.Ok => "ok",
            PrSourceStatus.GhMissing => "gh-missing",
            PrSourceStatus.NotAuthenticated => "gh-auth",
            _ => "failed",
        };
        var prs = shown.Prs;
        var parentOf = Parents(prs);
        var children = prs.Where(parentOf.ContainsKey).GroupBy(p => parentOf[p]).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Number).ToList());
        var agentsByPr = links.GroupBy(l => l.Pr).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.LastHit).ToList());
        var roots = prs.Where(p => !parentOf.ContainsKey(p)).ToList();
        var series = SeriesGroups(roots, children);
        var inSeries = series.SelectMany(g => g).ToHashSet();
        var items = new List<Item>();

        foreach (var root in roots.Where(r => !inSeries.Contains(r)))
        {
            var rows = new List<BoardRow>();
            AddStack(root, 0, null);
            items.Add(new Item(root.IsDraft, rows.Min(r => r.Band), root.Repo.ToLowerInvariant(), root.Number, rows));

            void AddStack(PullRequest p, int depth, PullRequest? parent)
            {
                rows.Add(RowFor(p, depth, parent, root.IsDraft));
                if (children.TryGetValue(p, out var kids))
                    foreach (var kid in kids) AddStack(kid, depth + 1, p);
            }
        }
        foreach (var g in series)
        {
            var members = g.OrderBy(p => p.Number).Select(p => RowFor(p, 1, null, g.Key.IsDraft)).ToList();
            var row = SeriesRow(g.Key.Repo, g.Key.Title, members, expanded);
            items.Add(new Item(g.Key.IsDraft, row.Band, g.Key.Repo, members[0].Number, new List<BoardRow> { row }));
        }

        var sections = new List<BoardSection>();
        foreach (var (name, draft) in new[] { ("Live", false), ("Draft", true) })
        {
            var mine = items.Where(i => i.Draft == draft).OrderBy(i => i.Band).ThenBy(i => i.Repo, StringComparer.Ordinal).ThenBy(i => i.Number).ToList();
            if (mine.Count == 0) continue;
            sections.Add(new BoardSection(name, mine.Sum(i => i.Rows.Sum(r => Math.Max(1, r.SeriesCount))), mine.SelectMany(i => i.Rows).ToList()));
        }
        return new Board(status, error.Length > 0 ? error : shown.Error, shown.Warnings, shown.FetchedAt, StaleAfterSeconds, prs.Count, sections);

        BoardRow RowFor(PullRequest p, int depth, PullRequest? parent, bool rootDraft)
        {
            var actions = Actions(p, parent != null);
            var tree = worktrees.FirstOrDefault(w => w.Matches(p));
            string localText = "", localTip = "";
            if (tree != null && local.TryGetValue(tree.CanonicalPath, out var state))
            {
                var parts = new List<string>();
                if (state.Uncommitted) parts.Add("uncommitted changes");
                if (state.Unpushed is int n && n > 0) parts.Add(n == 1 ? "1 unpushed commit" : $"{n} unpushed commits");
                localText = string.Join(", ", parts);
                if (localText.Length > 0) localTip = tree.Path;
            }
            return new BoardRow
            {
                Key = $"{p.Repo.ToLowerInvariant()}#{p.Number}",
                Depth = depth,
                Ci = CiToken(p.Ci),
                Checks = p.Checks.Where(c => c.State != "success").Select(c => new BoardCheck(c.Name, c.State, c.StartedAt)).ToList(),
                Repo = p.Repo,
                Number = p.Number,
                Title = p.Title,
                Url = p.Url,
                Action = Label(actions[0], p, parent),
                ActionTip = string.Join("\n", actions.Select(a => Label(a, p, parent))),
                Band = Band(actions[0]),
                Agents = AgentsFor(p.Key),
                CreatedAt = p.CreatedAt,
                LastCommitAt = p.LastCommitAt,
                Local = localText,
                LocalTip = localTip,
                Marker = depth > 0 && p.IsDraft != rootDraft ? (p.IsDraft ? "draft" : "live") : "",
            };
        }

        IReadOnlyList<BoardAgent> AgentsFor(PrKey key) =>
            agentsByPr.TryGetValue(key, out var list)
                ? list.Select(l => describe(l.SessionId) is { } v ? new BoardAgent(l.SessionId, v.Name, v.Provider, v.State, l.ViaCodex, v.Clickable, l.Evidence) : null)
                      .OfType<BoardAgent>().ToList()
                : Array.Empty<BoardAgent>();
    }

    public static HashSet<string> SeriesKeys(IReadOnlyList<PullRequest> prs)
    {
        var parentOf = Parents(prs);
        var children = prs.Where(parentOf.ContainsKey).GroupBy(p => parentOf[p]).ToDictionary(g => g.Key, g => g.ToList());
        return SeriesGroups(prs.Where(p => !parentOf.ContainsKey(p)).ToList(), children).Select(g => SeriesKey(g.Key.Repo, g.Key.Title)).ToHashSet();
    }

    static List<IGrouping<(string Repo, string Title, bool IsDraft), PullRequest>> SeriesGroups(List<PullRequest> roots, Dictionary<PullRequest, List<PullRequest>> children) =>
        roots.Where(p => !children.ContainsKey(p))
             .GroupBy(p => (Repo: p.Repo.ToLowerInvariant(), p.Title, p.IsDraft))
             .Where(g => g.Count() >= 2)
             .ToList();

    static string SeriesKey(string repo, string title) => $"{repo}|{title}";

    static BoardRow SeriesRow(string repo, string title, List<BoardRow> members, IReadOnlySet<string> expanded)
    {
        string key = SeriesKey(repo, title);
        var worst = members.OrderByDescending(m => CiRank(m.Ci)).First();
        var urgent = members.OrderBy(m => m.Band).ThenBy(m => m.Number).First();
        var withLocal = members.Where(m => m.Local.Length > 0).ToList();
        return new BoardRow
        {
            Key = "series:" + key,
            Ci = worst.Ci,
            Repo = members[0].Repo,
            Number = members[0].Number,
            Title = title,
            Action = urgent.Action,
            ActionTip = string.Join("\n", members.Select(m => $"#{m.Number}: {m.Action}")),
            Band = urgent.Band,
            Agents = members.SelectMany(m => m.Agents).GroupBy(a => a.SessionId).Select(g => g.First()).ToList(),
            CreatedAt = members[0].CreatedAt,
            LastCommitAt = members[0].LastCommitAt,
            Local = withLocal.Count == 0 ? "" : withLocal.Count == 1 ? "1 with local changes" : $"{withLocal.Count} with local changes",
            LocalTip = string.Join("\n", withLocal.Select(m => $"#{m.Number}: {m.Local}")),
            SeriesCount = members.Count,
            Expanded = expanded.Contains(key),
            Members = members,
        };
    }

    static Dictionary<PullRequest, PullRequest> Parents(IReadOnlyList<PullRequest> prs)
    {
        var byHead = new Dictionary<(string, string), PullRequest>();
        foreach (var p in prs.Where(p => !p.IsCrossRepository)) byHead.TryAdd((p.Repo.ToLowerInvariant(), p.HeadRef), p);
        var raw = new Dictionary<PullRequest, PullRequest>();
        foreach (var p in prs)
            if (byHead.TryGetValue((p.Repo.ToLowerInvariant(), p.BaseRef), out var parent) && !ReferenceEquals(parent, p)) raw[p] = parent;
        var result = new Dictionary<PullRequest, PullRequest>();
        foreach (var (child, parent) in raw)
        {
            var seen = new HashSet<PullRequest> { child };
            bool cycle = false;
            for (PullRequest? q = parent; q != null; q = raw.GetValueOrDefault(q))
                if (!seen.Add(q)) { cycle = true; break; }
            if (!cycle) result[child] = parent;
        }
        return result;
    }

    internal static List<NextAction> Actions(PullRequest pr, bool stacked)
    {
        var a = new List<NextAction>();
        if (pr.Mergeable == "CONFLICTING" || pr.MergeState == "DIRTY") a.Add(NextAction.Conflicts);
        if (pr.Ci == CiState.Failure) a.Add(NextAction.CiFailing);
        if (pr.ReviewDecision == "CHANGES_REQUESTED") a.Add(NextAction.ChangesRequested);
        if (pr.OpenThreads > 0) a.Add(NextAction.OpenThreads);
        if (pr.MergeState == "BEHIND") a.Add(NextAction.BehindBase);
        if (stacked) a.Add(NextAction.WaitingOnParent);
        if (pr.Ci == CiState.Pending) a.Add(NextAction.CiRunning);
        if (pr.Mergeable == "UNKNOWN") a.Add(NextAction.MergeUnknown);
        if (a.Count == 0)
            a.Add(pr.IsDraft ? NextAction.Draft
                : pr.ReviewDecision == "REVIEW_REQUIRED" || pr.MergeState == "BLOCKED" ? NextAction.NeedsReview
                : NextAction.ReadyToMerge);
        return a;
    }

    static int Band(NextAction a) => a switch
    {
        NextAction.Conflicts or NextAction.CiFailing or NextAction.ChangesRequested or NextAction.OpenThreads or NextAction.BehindBase => 1,
        NextAction.CiRunning => 2,
        NextAction.WaitingOnParent or NextAction.NeedsReview or NextAction.MergeUnknown => 3,
        _ => 4,
    };

    static string Label(NextAction a, PullRequest pr, PullRequest? parent) => a switch
    {
        NextAction.Conflicts => "Conflicts",
        NextAction.CiFailing => pr.Checks.FirstOrDefault(c => c.State == "failure") is { } f ? $"CI failing: {f.Name}" : "CI failing",
        NextAction.ChangesRequested => "Changes requested",
        NextAction.OpenThreads => pr.OpenThreads == 1 ? "1 open thread" : $"{pr.OpenThreads} open threads",
        NextAction.BehindBase => "Behind base",
        NextAction.WaitingOnParent => parent != null ? $"Waiting on #{parent.Number}" : "Waiting on its base pull request",
        NextAction.CiRunning => "CI running",
        NextAction.MergeUnknown => "Merge state unknown",
        NextAction.Draft => pr.Ci == CiState.Success ? "Draft, all green" : "Draft, no checks",
        NextAction.NeedsReview => "Needs review",
        _ => "Ready to merge",
    };

    static string CiToken(CiState ci) => ci switch
    {
        CiState.Success => "success",
        CiState.Pending => "pending",
        CiState.Failure => "failure",
        _ => "none",
    };

    static int CiRank(string ci) => ci switch { "failure" => 3, "pending" => 2, "none" => 1, _ => 0 };

}
