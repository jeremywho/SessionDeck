using Xunit;

namespace SessionDeck.Tests;

public class PrBoardTests
{
    static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    static PullRequest Pr(int n, string repo = "acme/web", string? title = null, bool draft = false, string? head = null, string @base = "main",
        string mergeable = "MERGEABLE", string mergeState = "CLEAN", string review = "APPROVED", int threads = 0, CiState ci = CiState.Success,
        CheckInfo[]? checks = null) => new()
    {
        Id = "id" + n, Repo = repo, Number = n, Title = title ?? "PR " + n, Url = $"https://github.com/{repo}/pull/{n}",
        IsDraft = draft, CreatedAt = Now.AddDays(-3), LastCommitAt = Now.AddDays(-1),
        BaseRef = @base, HeadRef = head ?? "b" + n, HeadOid = "oid" + n,
        Mergeable = mergeable, MergeState = mergeState, ReviewDecision = review, OpenThreads = threads, Ci = ci,
        Checks = checks ?? Array.Empty<CheckInfo>(),
    };

    static Board Build(IEnumerable<PullRequest> prs, IReadOnlyList<Worktree>? trees = null, Dictionary<string, LocalState>? local = null,
        IReadOnlyList<AgentLink>? links = null, Func<string, AgentView?>? describe = null, HashSet<string>? expanded = null) =>
        PrBoardBuilder.Build(new PrSnapshot(PrSourceStatus.Ok, prs.ToList(), Array.Empty<string>(), "", Now), "",
            trees ?? Array.Empty<Worktree>(), local ?? new Dictionary<string, LocalState>(), links ?? Array.Empty<AgentLink>(),
            describe ?? (_ => null), expanded ?? new HashSet<string>(), Now);

    static IReadOnlyList<BoardRow> Rows(Board b, string section) => b.Sections.Single(s => s.Name == section).Rows;
    static BoardRow Only(params PullRequest[] prs) => Build(prs).Sections.SelectMany(s => s.Rows).Single();
    static CheckInfo Failing(string name) => new(name, "failure", null, "");

    [Fact]
    public void Conflicts_come_first_and_the_tooltip_lists_every_reason()
    {
        var row = Only(Pr(1, mergeable: "CONFLICTING", mergeState: "DIRTY", ci: CiState.Failure, checks: new[] { Failing("build") }));
        Assert.Equal(("Conflicts", 1, "Conflicts\nCI failing: build"), (row.Action, row.Band, row.ActionTip));
    }

    [Theory]
    [InlineData(1, "1 open thread")]
    [InlineData(3, "3 open threads")]
    public void Open_threads_are_counted(int threads, string label) => Assert.Equal(label, Only(Pr(1, threads: threads)).Action);

    [Fact]
    public void A_failing_check_is_named() =>
        Assert.Equal("CI failing: lint", Only(Pr(1, ci: CiState.Failure, checks: new[] { new CheckInfo("build", "success", null, ""), Failing("lint") })).Action);

    [Theory]
    [InlineData("REVIEW_REQUIRED", "CLEAN", "Needs review", 3)]
    [InlineData("", "BLOCKED", "Needs review", 3)]
    [InlineData("APPROVED", "CLEAN", "Ready to merge", 4)]
    [InlineData("CHANGES_REQUESTED", "BLOCKED", "Changes requested", 1)]
    [InlineData("APPROVED", "BEHIND", "Behind base", 1)]
    public void A_live_pr_waits_on_review_or_is_ready(string review, string state, string action, int band)
    {
        var row = Only(Pr(1, review: review, mergeState: state));
        Assert.Equal((action, band), (row.Action, row.Band));
    }

    [Theory]
    [InlineData("Success", "Draft, all green")]
    [InlineData("None", "Draft, no checks")]
    [InlineData("Pending", "CI running")]
    public void A_draft_says_whether_it_is_green(string ci, string action) =>
        Assert.Equal(action, Only(Pr(1, draft: true, ci: Enum.Parse<CiState>(ci))).Action);

    [Fact]
    public void Mergeability_still_unknown_after_the_retry_is_said_plainly()
    {
        var row = Only(Pr(1, mergeable: "UNKNOWN", mergeState: "UNKNOWN"));
        Assert.Equal(("Merge state unknown", 3), (row.Action, row.Band));
    }

    [Fact]
    public void A_stack_nests_under_its_root_and_marks_a_child_whose_draft_state_differs()
    {
        var board = Build(new[] { Pr(3, head: "s3", @base: "s2"), Pr(1, head: "s1"), Pr(2, head: "s2", @base: "s1", draft: true) });
        var rows = Rows(board, "Live");
        Assert.Equal(new[] { (1, 0, ""), (2, 1, "draft"), (3, 2, "") }, rows.Select(r => (r.Number, r.Depth, r.Marker)));
        Assert.Equal(new[] { "Ready to merge", "Waiting on #1", "Waiting on #2" }, rows.Select(r => r.Action));
        Assert.Equal(3, board.Sections.Single().Count);
    }

    [Fact]
    public void A_stack_sorts_by_its_most_urgent_member() =>
        Assert.Equal(new[] { 1, 2, 5 }, Rows(Build(new[] { Pr(5), Pr(1, head: "s1"), Pr(2, head: "s2", @base: "s1", ci: CiState.Failure) }), "Live").Select(r => r.Number));

    [Fact]
    public void A_base_branch_that_is_no_open_pr_makes_a_root()
    {
        var row = Only(Pr(7, @base: "merged-parent"));
        Assert.Equal((0, "Ready to merge"), (row.Depth, row.Action));
    }

    [Fact]
    public void The_same_branch_name_in_another_repository_is_not_a_parent() =>
        Assert.All(Build(new[] { Pr(1, repo: "acme/x", head: "feat"), Pr(2, repo: "acme/y", @base: "feat") }).Sections.Single().Rows, r => Assert.Equal(0, r.Depth));

    [Fact]
    public void Identical_titles_collapse_into_one_series_row()
    {
        var prs = new[] { Pr(10, title: "Sweep"), Pr(11, title: "Sweep", ci: CiState.Failure, checks: new[] { Failing("build") }), Pr(12, title: "Other") };
        var rows = Rows(Build(prs), "Live");
        var series = rows[0];
        Assert.Equal(("series:acme/web|Sweep", 2, "failure", "CI failing: build", false), (series.Key, series.SeriesCount, series.Ci, series.Action, series.Expanded));
        Assert.Equal(new[] { 10, 11 }, series.Members.Select(m => m.Number));
        Assert.Equal(12, rows[1].Number);
        Assert.True(Rows(Build(prs, expanded: new HashSet<string> { "acme/web|Sweep" }), "Live")[0].Expanded);
        Assert.Equal(3, Build(prs).Sections.Single().Count);
    }

    [Fact]
    public void Stacked_prs_never_form_a_series() =>
        Assert.Equal(new[] { (1, 0, 0), (2, 1, 0) },
            Rows(Build(new[] { Pr(1, title: "Same", head: "s1"), Pr(2, title: "Same", head: "s2", @base: "s1") }), "Live").Select(r => (r.Number, r.Depth, r.SeriesCount)));

    [Fact]
    public void Order_inside_a_band_is_repository_then_number() =>
        Assert.Equal(new[] { "a/z#2", "b/z#1", "b/z#3" }, Rows(Build(new[] { Pr(3, repo: "b/z"), Pr(2, repo: "a/z"), Pr(1, repo: "b/z") }), "Live").Select(r => r.Key));

    [Fact]
    public void Local_work_is_shown_and_a_pr_without_a_worktree_shows_nothing()
    {
        var tree = new Worktree("acme/web", "b1", @"C:\Repos\.worktrees\web\b1", @"D:\Repos\.worktrees\web\b1");
        var rows = Rows(Build(new[] { Pr(1), Pr(2) }, new[] { tree }, new Dictionary<string, LocalState> { [tree.CanonicalPath] = new LocalState(true, 2) }), "Live");
        Assert.Equal(("uncommitted changes, 2 unpushed commits", @"C:\Repos\.worktrees\web\b1"), (rows[0].Local, rows[0].LocalTip));
        Assert.Equal(("", ""), (rows[1].Local, rows[1].LocalTip));
    }

    [Fact]
    public void Agents_are_newest_first_and_sessions_no_longer_live_are_dropped()
    {
        var key = PrKey.Of("acme/web", 1);
        var links = new[]
        {
            new AgentLink(key, "a", 3, Now.AddMinutes(-10), false, "3 tool calls"),
            new AgentLink(key, "b", 5, Now.AddMinutes(-1), true, "5 tool calls"),
            new AgentLink(key, "gone", 9, Now, false, "9 tool calls"),
        };
        AgentView? Describe(string id) => id == "gone" ? null : new AgentView("name-" + id, "Claude", "working", Clickable: id != "b");
        var agents = Rows(Build(new[] { Pr(1) }, links: links, describe: Describe), "Live")[0].Agents;
        Assert.Equal(new[] { ("b", true, false), ("a", false, true) }, agents.Select(a => (a.SessionId, a.ViaCodex, a.Clickable)));
    }

    [Fact]
    public void Ages_say_when_it_opened_and_when_it_last_changed() => Assert.Equal("opened 3d ago, last commit 24h ago", Only(Pr(1)).Age);

    [Fact]
    public void No_snapshot_yet_is_loading_and_a_failure_keeps_its_status()
    {
        var none = new Dictionary<string, LocalState>();
        Assert.Equal("loading", PrBoardBuilder.Build(null, "", Array.Empty<Worktree>(), none, Array.Empty<AgentLink>(), _ => null, new HashSet<string>(), Now).Status);
        var failed = new PrSnapshot(PrSourceStatus.NotAuthenticated, Array.Empty<PullRequest>(), Array.Empty<string>(), "run gh auth login", Now);
        var b = PrBoardBuilder.Build(failed, "", Array.Empty<Worktree>(), none, Array.Empty<AgentLink>(), _ => null, new HashSet<string>(), Now);
        Assert.Equal(("gh-auth", "run gh auth login", 0), (b.Status, b.Error, b.Total));
    }

    [Fact]
    public void Series_keys_name_only_current_series() =>
        Assert.Equal(new[] { "acme/web|Sweep" }, PrBoardBuilder.SeriesKeys(new[] { Pr(10, title: "Sweep"), Pr(11, title: "Sweep"), Pr(12, title: "Other") }).Order());
}
