using System.Globalization;
using System.Text.Json;

namespace SessionDeck;

internal enum CiState { None, Pending, Success, Failure }

internal enum PrSourceStatus { Ok, GhMissing, NotAuthenticated, Failed }

internal readonly record struct PrKey(string Repo, int Number)
{
    public static PrKey Of(string repo, int number) => new(repo.ToLowerInvariant(), number);
}

internal sealed record CheckInfo(string Name, string State, DateTime? StartedAt, string Url);

internal sealed record PullRequest
{
    public string Id { get; init; } = "";
    public string Repo { get; init; } = "";
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public bool IsDraft { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastCommitAt { get; init; }
    public string BaseRef { get; init; } = "";
    public string HeadRef { get; init; } = "";
    public string HeadOid { get; init; } = "";
    public bool IsCrossRepository { get; init; }
    public string HeadRepo { get; init; } = "";
    public string Mergeable { get; init; } = "";
    public string MergeState { get; init; } = "";
    public string ReviewDecision { get; init; } = "";
    public int OpenThreads { get; init; }
    public CiState Ci { get; init; }
    public IReadOnlyList<CheckInfo> Checks { get; init; } = Array.Empty<CheckInfo>();
    public PrKey Key => PrKey.Of(Repo, Number);
}

internal sealed record PrSnapshot(PrSourceStatus Status, IReadOnlyList<PullRequest> Prs, IReadOnlyList<string> Warnings, string Error, DateTime FetchedAt);

internal static class PrSource
{
    internal const string SearchQuery = "is:pr is:open author:@me archived:false";
    const int MaxPages = 10;

    internal const string Query = """
        query($q: String!, $cursor: String) {
          search(query: $q, type: ISSUE, first: 100, after: $cursor) {
            pageInfo { hasNextPage endCursor }
            nodes {
              ... on PullRequest {
                id number title url isDraft createdAt
                repository { nameWithOwner }
                baseRefName headRefName headRefOid
                isCrossRepository headRepository { nameWithOwner }
                mergeable mergeStateStatus reviewDecision
                reviewThreads(first: 100) { nodes { isResolved } }
                commits(last: 1) { nodes { commit { committedDate statusCheckRollup { state contexts(first: 100) { nodes {
                  __typename
                  ... on CheckRun { name status conclusion startedAt detailsUrl }
                  ... on StatusContext { context state createdAt targetUrl }
                } } } } } }
              }
            }
          }
        }
        """;

    static readonly HashSet<string> FailingConclusions = new(StringComparer.Ordinal) { "FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE" };

    internal sealed record SearchPage(List<PullRequest> Prs, List<string> Warnings, bool HasData, bool HasNextPage, string? EndCursor);

    public static async Task<PrSnapshot> FetchAsync(Func<IReadOnlyList<string>, Task<GhResult>> gh, DateTime nowUtc, TimeSpan retryDelay)
    {
        var prs = new List<PullRequest>();
        var warnings = new List<string>();
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            var args = new List<string> { "api", "graphql", "-f", "query=" + Query, "-f", "q=" + SearchQuery };
            if (cursor != null) { args.Add("-f"); args.Add("cursor=" + cursor); }
            var result = await gh(args);
            var parsed = ParseSearch(result.Stdout);
            if (!parsed.HasData)
            {
                if (prs.Count == 0) return Failure(result, nowUtc);
                warnings.Add($"Only the first {prs.Count} pull requests loaded");
                break;
            }
            prs.AddRange(parsed.Prs);
            foreach (var w in parsed.Warnings) if (!warnings.Contains(w)) warnings.Add(w);
            if (!parsed.HasNextPage || parsed.EndCursor == null) break;
            cursor = parsed.EndCursor;
        }

        var unknown = prs.Where(p => p.Mergeable == "UNKNOWN").ToList();
        if (unknown.Count > 0)
        {
            if (retryDelay > TimeSpan.Zero) await Task.Delay(retryDelay);
            var retry = await gh(new[] { "api", "graphql", "-f", "query=" + MergeabilityQuery(unknown.Select(p => p.Id)) });
            var states = ParseMergeability(retry.Stdout);
            for (int i = 0; i < prs.Count; i++)
                if (states.TryGetValue(prs[i].Id, out var s)) prs[i] = prs[i] with { Mergeable = s.Mergeable, MergeState = s.MergeState };
        }
        return new PrSnapshot(PrSourceStatus.Ok, prs, warnings, "", nowUtc);
    }

    internal static PrSnapshot Failure(GhResult r, DateTime nowUtc)
    {
        var none = Array.Empty<PullRequest>();
        var noWarnings = Array.Empty<string>();
        if (r.NotFound) return new PrSnapshot(PrSourceStatus.GhMissing, none, noWarnings, "gh was not found", nowUtc);
        string err = r.Stderr.Trim();
        if (err.Contains("gh auth login", StringComparison.OrdinalIgnoreCase) || err.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
            return new PrSnapshot(PrSourceStatus.NotAuthenticated, none, noWarnings, FirstLine(err), nowUtc);
        string message = err.Length > 0 ? FirstLine(err) : r.TimedOut ? "gh timed out" : $"gh exited with code {r.ExitCode}";
        return new PrSnapshot(PrSourceStatus.Failed, none, noWarnings, message, nowUtc);
    }

    static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }

    internal static SearchPage ParseSearch(string json)
    {
        var empty = new SearchPage(new(), new(), false, false, null);
        if (string.IsNullOrWhiteSpace(json)) return empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var warnings = new List<string>();
            if (Arr(root, "errors", out var errors))
                foreach (var e in errors.EnumerateArray())
                    if (Str(e, "message") is { Length: > 0 } m && !warnings.Contains(m)) warnings.Add(m);
            if (!Obj(root, "data", out var data) || !Obj(data, "search", out var search)) return empty with { Warnings = warnings };
            var prs = new List<PullRequest>();
            if (Arr(search, "nodes", out var nodes))
                foreach (var n in nodes.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.Object && n.TryGetProperty("number", out var num) && num.TryGetInt32(out _)) prs.Add(Parse(n));
            bool next = false;
            string? cursor = null;
            if (Obj(search, "pageInfo", out var info))
            {
                next = info.TryGetProperty("hasNextPage", out var h) && h.ValueKind == JsonValueKind.True;
                cursor = Str(info, "endCursor") is { Length: > 0 } c ? c : null;
            }
            return new SearchPage(prs, warnings, true, next, cursor);
        }
        catch (JsonException) { return empty; }
    }

    static PullRequest Parse(JsonElement n)
    {
        JsonElement commit = default, rollup = default;
        if (Obj(n, "commits", out var commits) && Arr(commits, "nodes", out var cn) && cn.GetArrayLength() > 0 && Obj(cn[0], "commit", out var c)) commit = c;
        if (Obj(commit, "statusCheckRollup", out var r)) rollup = r;
        var checks = new List<CheckInfo>();
        if (Obj(rollup, "contexts", out var ctx) && Arr(ctx, "nodes", out var cnodes))
            foreach (var node in cnodes.EnumerateArray())
                if (Check(node) is { } check) checks.Add(check);
        int open = Obj(n, "reviewThreads", out var rt) && Arr(rt, "nodes", out var tn)
            ? tn.EnumerateArray().Count(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("isResolved", out var v) && v.ValueKind == JsonValueKind.False)
            : 0;
        return new PullRequest
        {
            Id = Str(n, "id"),
            Repo = Obj(n, "repository", out var repo) ? Str(repo, "nameWithOwner") : "",
            Number = n.GetProperty("number").GetInt32(),
            Title = Str(n, "title"),
            Url = Str(n, "url"),
            IsDraft = n.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True,
            CreatedAt = Time(n, "createdAt") ?? DateTime.MinValue,
            LastCommitAt = Time(commit, "committedDate"),
            BaseRef = Str(n, "baseRefName"),
            HeadRef = Str(n, "headRefName"),
            HeadOid = Str(n, "headRefOid"),
            IsCrossRepository = n.TryGetProperty("isCrossRepository", out var x) && x.ValueKind == JsonValueKind.True,
            HeadRepo = Obj(n, "headRepository", out var head) ? Str(head, "nameWithOwner") : "",
            Mergeable = Str(n, "mergeable"),
            MergeState = Str(n, "mergeStateStatus"),
            ReviewDecision = Str(n, "reviewDecision"),
            OpenThreads = open,
            Ci = rollup.ValueKind != JsonValueKind.Object ? CiState.None : Str(rollup, "state") switch
            {
                "SUCCESS" => CiState.Success,
                "FAILURE" or "ERROR" => CiState.Failure,
                "PENDING" or "EXPECTED" => CiState.Pending,
                _ => CiState.None,
            },
            Checks = checks,
        };
    }

    static CheckInfo? Check(JsonElement n)
    {
        switch (Str(n, "__typename"))
        {
            case "CheckRun":
                string state = Str(n, "status") != "COMPLETED" ? "pending" : FailingConclusions.Contains(Str(n, "conclusion")) ? "failure" : "success";
                return new CheckInfo(Str(n, "name"), state, Time(n, "startedAt"), Str(n, "detailsUrl"));
            case "StatusContext":
                string s = Str(n, "state");
                return new CheckInfo(Str(n, "context"), s is "FAILURE" or "ERROR" ? "failure" : s is "PENDING" or "EXPECTED" ? "pending" : "success",
                    Time(n, "createdAt"), Str(n, "targetUrl"));
            default:
                return null;
        }
    }

    internal static string MergeabilityQuery(IEnumerable<string> ids) =>
        "query { " + string.Join(" ", ids.Select((id, i) => $"n{i}: node(id: {JsonSerializer.Serialize(id)}) {{ ... on PullRequest {{ id mergeable mergeStateStatus }} }}")) + " }";

    internal static Dictionary<string, (string Mergeable, string MergeState)> ParseMergeability(string json)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (Obj(doc.RootElement, "data", out var data))
                foreach (var p in data.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && Str(p.Value, "id") is { Length: > 0 } id)
                        map[id] = (Str(p.Value, "mergeable"), Str(p.Value, "mergeStateStatus"));
        }
        catch (JsonException) { }
        return map;
    }

    static bool Obj(JsonElement e, string name, out JsonElement v)
    {
        v = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Object;
    }

    static bool Arr(JsonElement e, string name, out JsonElement v)
    {
        v = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Array;
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static DateTime? Time(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
