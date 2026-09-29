using Xunit;

namespace SessionDeck.Tests;

public class PrSourceTests
{
    static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    const string Page = """
        {"data":{"search":{"pageInfo":{"hasNextPage":false,"endCursor":"Y3Vyc29yOjM="},"nodes":[
         {"id":"PR_a1","number":101,"title":"Add the widget","url":"https://github.com/acme/web/pull/101","isDraft":false,"createdAt":"2026-09-20T10:00:00Z",
          "repository":{"nameWithOwner":"acme/web"},"baseRefName":"main","headRefName":"widget","headRefOid":"aaa111",
          "mergeable":"MERGEABLE","mergeStateStatus":"BLOCKED","reviewDecision":"REVIEW_REQUIRED",
          "reviewThreads":{"nodes":[{"isResolved":true},{"isResolved":false}]},
          "commits":{"nodes":[{"commit":{"committedDate":"2026-09-28T09:00:00Z","statusCheckRollup":{"state":"FAILURE","contexts":{"nodes":[
            {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"FAILURE","startedAt":"2026-09-28T09:01:00Z","detailsUrl":"https://ci.example/run/1"},
            {"__typename":"CheckRun","name":"lint","status":"IN_PROGRESS","conclusion":null,"startedAt":"2026-09-28T09:02:00Z","detailsUrl":"https://ci.example/run/2"},
            {"__typename":"StatusContext","context":"ci/external","state":"SUCCESS","createdAt":"2026-09-28T09:03:00Z","targetUrl":"https://ci.example/s/3"}]}}}}]}},
         {"id":"PR_a2","number":102,"title":"Widget follow-up","url":"https://github.com/acme/web/pull/102","isDraft":true,"createdAt":"2026-09-21T10:00:00Z",
          "repository":{"nameWithOwner":"acme/web"},"baseRefName":"widget","headRefName":"widget-2","headRefOid":"bbb222",
          "mergeable":"UNKNOWN","mergeStateStatus":"UNKNOWN","reviewDecision":null,
          "reviewThreads":{"nodes":[]},
          "commits":{"nodes":[{"commit":{"committedDate":"2026-09-22T09:00:00Z","statusCheckRollup":null}}]}},
         {}
        ]}}}
        """;

    sealed class FakeGh
    {
        readonly Queue<GhResult> _results;
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public FakeGh(params GhResult[] results) => _results = new(results);
        public Task<GhResult> Run(IReadOnlyList<string> args) { Calls.Add(args); return Task.FromResult(_results.Dequeue()); }
    }

    static GhResult Ok(string stdout) => new(0, stdout, "");

    [Fact]
    public void A_search_page_parses_every_field_the_board_uses()
    {
        var page = PrSource.ParseSearch(Page);
        Assert.True(page.HasData);
        Assert.Equal(new[] { 101, 102 }, page.Prs.Select(p => p.Number));
        var a = page.Prs[0];
        Assert.Equal(("PR_a1", "acme/web", "Add the widget", false, "main", "widget", "aaa111"), (a.Id, a.Repo, a.Title, a.IsDraft, a.BaseRef, a.HeadRef, a.HeadOid));
        Assert.Equal(("MERGEABLE", "BLOCKED", "REVIEW_REQUIRED", 1, CiState.Failure), (a.Mergeable, a.MergeState, a.ReviewDecision, a.OpenThreads, a.Ci));
        Assert.Equal(new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc), a.CreatedAt);
        Assert.Equal<DateTime?>(new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc), a.LastCommitAt);
        Assert.Equal(new[] { ("build", "failure"), ("lint", "pending"), ("ci/external", "success") }, a.Checks.Select(c => (c.Name, c.State)));
        var b = page.Prs[1];
        Assert.Equal((true, "UNKNOWN", "", CiState.None, 0, 0), (b.IsDraft, b.Mergeable, b.ReviewDecision, b.Ci, b.Checks.Count, b.OpenThreads));
        Assert.Equal(PrKey.Of("ACME/web", 101), a.Key);
    }

    [Fact]
    public async Task Partial_results_with_an_error_show_what_came_back_and_warn()
    {
        const string partial = """
            {"data":{"search":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[
             {"id":"PR_x","number":5,"title":"x","url":"u","isDraft":false,"createdAt":"2026-09-20T10:00:00Z","repository":{"nameWithOwner":"acme/api"},
              "baseRefName":"main","headRefName":"x","headRefOid":"o","mergeable":"MERGEABLE","mergeStateStatus":"CLEAN","reviewDecision":null,
              "reviewThreads":{"nodes":[]},"commits":{"nodes":[]}}]}},
             "errors":[{"message":"Resource protected by organization SAML enforcement."}]}
            """;
        var gh = new FakeGh(new GhResult(1, partial, "gh: Resource protected by organization SAML enforcement.\n"));
        var snap = await PrSource.FetchAsync(gh.Run, Now, TimeSpan.Zero);
        Assert.Equal((PrSourceStatus.Ok, 1), (snap.Status, snap.Prs.Count));
        Assert.Contains(snap.Warnings, w => w.Contains("SAML"));
    }

    [Fact]
    public async Task Gh_missing_is_reported_as_such()
    {
        var gh = new FakeGh(new GhResult(-1, "", "", NotFound: true));
        Assert.Equal(PrSourceStatus.GhMissing, (await PrSource.FetchAsync(gh.Run, Now, TimeSpan.Zero)).Status);
    }

    [Fact]
    public async Task Signed_out_gh_is_reported_as_such()
    {
        var gh = new FakeGh(new GhResult(4, "", "To get started with GitHub CLI, please run:  gh auth login\n"));
        Assert.Equal(PrSourceStatus.NotAuthenticated, (await PrSource.FetchAsync(gh.Run, Now, TimeSpan.Zero)).Status);
    }

    [Fact]
    public async Task Any_other_failure_carries_the_first_line_of_the_error()
    {
        var gh = new FakeGh(new GhResult(1, "", "error connecting to api.github.com\ncheck your internet connection\n"));
        var snap = await PrSource.FetchAsync(gh.Run, Now, TimeSpan.Zero);
        Assert.Equal((PrSourceStatus.Failed, "error connecting to api.github.com", Now), (snap.Status, snap.Error, snap.FetchedAt));
    }

    [Fact]
    public async Task Pages_are_followed_with_the_cursor()
    {
        string first = Page.Replace("\"hasNextPage\":false,\"endCursor\":\"Y3Vyc29yOjM=\"", "\"hasNextPage\":true,\"endCursor\":\"c1\"");
        const string second = """{"data":{"search":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}""";
        var gh = new FakeGh(Ok(first), Ok(second), Ok("""{"data":{}}"""));
        var snap = await PrSource.FetchAsync(gh.Run, Now, TimeSpan.Zero);
        Assert.Equal(2, snap.Prs.Count);
        Assert.Contains("cursor=c1", gh.Calls[1]);
        Assert.DoesNotContain(gh.Calls[0], a => a.StartsWith("cursor=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_mergeability_is_read_again_once()
    {
        var gh = new FakeGh(Ok(Page), Ok("""{"data":{"n0":{"id":"PR_a2","mergeable":"CONFLICTING","mergeStateStatus":"DIRTY"}}}"""));
        var snap = await PrSource.FetchAsync(gh.Run, Now, TimeSpan.Zero);
        Assert.Equal(("CONFLICTING", "DIRTY"), (snap.Prs[1].Mergeable, snap.Prs[1].MergeState));
        Assert.Equal(2, gh.Calls.Count);
        Assert.Contains("n0: node(id: \"PR_a2\")", string.Join(" ", gh.Calls[1]));
    }
}
