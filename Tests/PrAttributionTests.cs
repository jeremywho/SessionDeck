using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace SessionDeck.Tests;

public class PrAttributionTests : IDisposable
{
    static readonly JsonSerializerOptions Raw = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    const string InTree = @"git -C C:\Repos\.worktrees\web\feat-x status";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-attr-" + Guid.NewGuid().ToString("N"));

    public PrAttributionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static PullRequest Pr(string repo, int n, string head) => new() { Repo = repo, Number = n, HeadRef = head, Title = "t" + n, Url = $"https://github.com/{repo}/pull/{n}" };

    static readonly PullRequest[] Open =
    {
        Pr("acme/web", 101, "feat-x"),
        Pr("acme/web", 102, "sub-branch"),
        Pr("acme/web", 103, "parent-branch"),
        Pr("acme/web", 7, "seven"),
        Pr("acme/api", 7, "api-seven"),
        Pr("acme/api", 8, "api-eight"),
    };

    static readonly Worktree[] Trees =
    {
        new("acme/web", "feat-x", @"C:\Repos\.worktrees\web\feat-x", @"D:\Repos\.worktrees\web\feat-x"),
        new("acme/web", "parent-branch", @"C:\Repos\.worktrees\web\feat", @"D:\Repos\.worktrees\web\feat"),
        new("acme/web", "sub-branch", @"C:\Repos\.worktrees\web\feat\sub", @"D:\Repos\.worktrees\web\feat\sub"),
    };

    static PrTargets Targets() => PrTargets.From(Open, Trees);

    static IEnumerable<PrKey> Hits(string raw) => PrAttribution.HitsIn(PrAttribution.Normalize(raw), Targets()).Select(h => h.Pr);

    static string Claude(string command, string at = "2026-09-29T10:00:00Z") =>
        JsonSerializer.Serialize(new { type = "assistant", timestamp = at, message = new { content = new object[] { new { type = "tool_use", name = "Bash", input = new { command } } } } }, Raw);

    static string CodexCall(string input) =>
        JsonSerializer.Serialize(new { timestamp = "2026-09-29T10:00:00Z", type = "response_item", payload = new { type = "custom_tool_call", name = "exec", input } }, Raw);

    string Transcript(string relative, IEnumerable<string> lines)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    SessionSource Session(string id, IEnumerable<string> lines, params FoldedSource[] folded) =>
        new(id, SessionProvider.Claude, "interactive", @"C:\Users\x", Now, Transcript(Path.Combine("proj", id + ".jsonl"), lines), folded);

    static List<AgentLink> Attribute(params SessionSource[] sessions) => PrAttribution.Attribute(sessions, Targets(), new ToolCallCache(), Now);

    [Theory]
    [InlineData(@"git -C C:\Repos\.worktrees\web\feat-x status")]
    [InlineData(@"cd C:/Repos/.worktrees/web/feat-x && dotnet build")]
    [InlineData(@"cd /c/Repos/.worktrees/web/feat-x")]
    [InlineData(@"type D:\\Repos\\.worktrees\\WEB\\feat-x\\src\\a.cs")]
    [InlineData(@"C://Repos//.worktrees//web//feat-x")]
    public void Every_spelling_of_a_worktree_path_is_a_hit(string raw) => Assert.Equal(new[] { PrKey.Of("acme/web", 101) }, Hits(raw));

    [Theory]
    [InlineData(@"cd C:\Repos\.worktrees\web\feat-x-old")]
    [InlineData(@"cd C:\Data\Repos\.worktrees\web\feat-x")]
    [InlineData(@"cd Repos\.worktrees\web\feat-x")]
    public void A_path_that_only_resembles_a_worktree_is_not_a_hit(string raw) => Assert.Empty(Hits(raw));

    [Fact]
    public void The_longer_of_two_nested_worktree_paths_wins() =>
        Assert.Equal(new[] { PrKey.Of("acme/web", 102) }, Hits(@"code C:\Repos\.worktrees\web\feat\sub\file.cs"));

    [Fact]
    public void The_outer_worktree_still_matches_on_its_own() =>
        Assert.Equal(new[] { PrKey.Of("acme/web", 103) }, Hits(@"code C:\Repos\.worktrees\web\feat\other.cs"));

    [Theory]
    [InlineData("see https://github.com/acme/web/pull/101/files", "acme/web", 101)]
    [InlineData("gh api repos/acme/web/pulls/101/comments", "acme/web", 101)]
    [InlineData("gh api repos/acme/web/issues/101/comments", "acme/web", 101)]
    [InlineData("gh pr view 101 --repo acme/web", "acme/web", 101)]
    [InlineData("gh pr checks 7 -R acme/api", "acme/api", 7)]
    [InlineData("gh pr view 8", "acme/api", 8)]
    public void Links_and_gh_commands_naming_an_open_pr_are_hits(string raw, string repo, int number) =>
        Assert.Equal(new[] { PrKey.Of(repo, number) }, Hits(raw));

    [Theory]
    [InlineData("gh pr view 7")]
    [InlineData("https://github.com/acme/web/pull/999")]
    [InlineData("gh pr view 101 --repo other/web")]
    public void Ambiguous_or_closed_prs_are_not_hits(string raw) => Assert.Empty(Hits(raw));

    [Fact]
    public void Two_hits_do_not_attribute_a_session_and_three_do()
    {
        var link = Assert.Single(Attribute(Session("s2", Enumerable.Repeat(Claude(InTree), 2)), Session("s3", Enumerable.Repeat(Claude(InTree), 3))));
        Assert.Equal(("s3", PrKey.Of("acme/web", 101), 3, false), (link.SessionId, link.Pr, link.Count, link.ViaCodex));
        Assert.Contains("feat-x", link.Evidence);
    }

    [Fact]
    public void A_session_touching_many_prs_once_or_twice_is_attributed_to_none()
    {
        var lines = new[] { "gh pr view 101 --repo acme/web", "gh pr view 8", "https://github.com/acme/web/pull/103", "gh pr view 101 --repo acme/web", "gh pr diff 8" }.Select(c => Claude(c));
        Assert.Empty(Attribute(Session("review", lines)));
    }

    [Fact]
    public void Several_mentions_in_one_tool_call_count_once() =>
        Assert.Empty(Attribute(Session("s", new[] { Claude(InTree + " && git -C C:/Repos/.worktrees/web/feat-x log && ls /c/Repos/.worktrees/web/feat-x") })));

    [Fact]
    public void Only_the_last_hundred_tool_calls_count() =>
        Assert.Empty(Attribute(Session("s", Enumerable.Repeat(Claude(InTree), 50).Concat(Enumerable.Repeat(Claude("ls"), 100)))));

    [Fact]
    public void Recent_subagent_work_counts_for_the_parent_and_old_subagent_work_does_not()
    {
        var parent = Session("p", new[] { Claude(InTree) });
        string fresh = Transcript(Path.Combine("proj", "p", "subagents", "agent-new.jsonl"), Enumerable.Repeat(Claude(InTree), 2));
        string old = Transcript(Path.Combine("proj", "p", "subagents", "agent-old.jsonl"), Enumerable.Repeat(Claude(@"cd C:\Repos\.worktrees\web\feat\sub"), 5));
        File.SetLastWriteTimeUtc(fresh, Now.AddMinutes(-5));
        File.SetLastWriteTimeUtc(old, Now.AddHours(-3));
        var link = Assert.Single(Attribute(parent));
        Assert.Equal((PrKey.Of("acme/web", 101), 3, false), (link.Pr, link.Count, link.ViaCodex));
    }

    [Fact]
    public void Folded_codex_threads_attribute_their_owner_marked_as_codex()
    {
        string rollout = Transcript("rollout-1.jsonl", Enumerable.Repeat(CodexCall("tools.exec_command({cmd:\"git status\",\"workdir\":\"C:\\\\Repos\\\\.worktrees\\\\web\\\\feat-x\"})"), 3));
        var link = Assert.Single(Attribute(Session("o", new[] { Claude("ls") }, new FoldedSource(rollout, SessionProvider.Codex, "companion", @"C:\Users\x", Now))));
        Assert.Equal((PrKey.Of("acme/web", 101), true), (link.Pr, link.ViaCodex));
    }

    [Fact]
    public void A_codex_exec_thread_started_in_a_worktree_attributes_its_owner_on_its_own()
    {
        var link = Assert.Single(Attribute(Session("o", new[] { Claude("ls") }, new FoldedSource("", SessionProvider.Codex, "exec", @"D:\Repos\.worktrees\web\feat-x", Now.AddMinutes(-1)))));
        Assert.Equal((PrKey.Of("acme/web", 101), true), (link.Pr, link.ViaCodex));
    }

    [Fact]
    public void An_unowned_codex_exec_thread_in_a_worktree_is_an_agent_itself()
    {
        var exec = new SessionSource("x1", SessionProvider.Codex, "exec", @"D:\Repos\.worktrees\web\feat-x", Now, "", Array.Empty<FoldedSource>());
        var link = Assert.Single(Attribute(exec));
        Assert.Equal(("x1", PrKey.Of("acme/web", 101), false), (link.SessionId, link.Pr, link.ViaCodex));
    }

    [Fact]
    public void A_folded_claude_background_session_counts_as_the_owners_own_work()
    {
        string bg = Transcript(Path.Combine("proj", "bg.jsonl"), Enumerable.Repeat(Claude(InTree), 3));
        Assert.False(Assert.Single(Attribute(Session("o", new[] { Claude("ls") }, new FoldedSource(bg, SessionProvider.Claude, "bg", "", Now)))).ViaCodex);
    }

    [Fact]
    public void Tool_results_and_codex_outputs_are_not_tool_calls()
    {
        var lines = new[]
        {
            JsonSerializer.Serialize(new { type = "user", message = new { content = new object[] { new { type = "tool_result", content = InTree } } } }, Raw),
            JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "custom_tool_call_output", output = InTree } }, Raw),
        };
        Assert.Empty(PrAttribution.ToolCalls(lines));
    }

    [Fact]
    public void A_tail_that_starts_mid_line_and_a_missing_file_are_both_harmless()
    {
        string path = Transcript("t.jsonl", Enumerable.Repeat(Claude(InTree), 5));
        Assert.InRange(ToolCallCache.Read(path, start: 700, max: 700).Count, 1, 4);
        Assert.Empty(new ToolCallCache().LastCalls(Path.Combine(_dir, "gone.jsonl")));
    }

    [Fact]
    public void The_tail_grows_until_it_holds_a_hundred_tool_calls()
    {
        string path = Transcript("big.jsonl", Enumerable.Range(0, 150).Select(i => Claude($"echo n{i}x")));
        var calls = ToolCallCache.Read(path, start: 1024, max: 4 * 1024 * 1024);
        Assert.Equal(100, calls.Count);
        Assert.Contains("echo n149x", calls[^1].Text);
        Assert.Contains("echo n50x", calls[0].Text);
    }

    [Fact]
    public void A_subagent_folder_that_never_existed_is_harmless() =>
        Assert.Empty(Attribute(Session("nofolder", new[] { Claude(InTree) })));
}
