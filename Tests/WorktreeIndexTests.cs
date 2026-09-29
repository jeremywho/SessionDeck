using System.Diagnostics;
using System.IO;
using Xunit;

namespace SessionDeck.Tests;

public class WorktreeIndexTests
{
    [Theory]
    [InlineData("https://github.com/acme/web.git", "acme/web")]
    [InlineData("https://github.com/acme/web", "acme/web")]
    [InlineData("git@github.com:acme/web.git", "acme/web")]
    [InlineData("ssh://git@github.com/acme/web.git", "acme/web")]
    [InlineData("https://gitlab.com/acme/web.git", null)]
    public void The_repository_is_read_from_the_origin_url(string url, string? repo) => Assert.Equal(repo, WorktreeIndex.RepoFromUrl(url));

    [Fact]
    public void The_origin_url_comes_from_the_origin_section_only()
    {
        const string config = "[core]\n\tbare = false\n[remote \"upstream\"]\n\turl = https://github.com/other/web.git\n[remote \"origin\"]\n\turl = https://github.com/acme/web.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n";
        Assert.Equal("https://github.com/acme/web.git", WorktreeIndex.OriginUrl(config));
    }

    [Fact]
    public void Porcelain_lists_branches_and_skips_bare_detached_and_prunable_entries()
    {
        const string text = "worktree C:/Repos/web\nHEAD abc\nbranch refs/heads/main\n\n" +
                            "worktree C:/Repos/.worktrees/web/feat/x\nHEAD def\nbranch refs/heads/feat/x\n\n" +
                            "worktree C:/Repos/.worktrees/web/rebasing\nHEAD 123\ndetached\n\n" +
                            "worktree C:/Repos/.worktrees/web/gone\nHEAD 456\nbranch refs/heads/gone\nprunable gitdir file points to non-existent location\n\n" +
                            "worktree C:/Repos/bare.git\nbare\n";
        Assert.Equal(new (string, string?)[] { ("C:/Repos/web", "main"), ("C:/Repos/.worktrees/web/feat/x", "feat/x"), ("C:/Repos/.worktrees/web/rebasing", null) },
            WorktreeIndex.ParsePorcelain(text).ToArray());
    }

    [Fact]
    public void A_second_clone_reporting_the_same_worktrees_adds_nothing_and_other_repos_are_not_asked()
    {
        var asked = new List<string>();
        var clones = new[] { (@"C:\Repos\web", "acme/web"), (@"C:\Repos\web-review", "acme/web"), (@"C:\Repos\other", "acme/other") };
        const string porcelain = "worktree C:/Repos/.worktrees/web/feat\nHEAD a\nbranch refs/heads/feat\n\n";
        var list = WorktreeIndex.Build(clones, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ACME/web" },
            c => { asked.Add(c); return porcelain; }, p => p.Replace('/', '\\').Replace(@"C:\Repos", @"D:\Repos"));
        var w = Assert.Single(list);
        Assert.Equal(("acme/web", "feat", @"C:\Repos\.worktrees\web\feat", @"D:\Repos\.worktrees\web\feat"), (w.Repo, w.Branch, w.Path, w.CanonicalPath));
        Assert.Equal(new[] { @"C:\Repos\web", @"C:\Repos\web-review" }, asked);
    }

    [Fact]
    public void A_junction_resolves_to_its_target()
    {
        string root = Path.Combine(Path.GetTempPath(), "sd-wt-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "real"), link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                p.WaitForExit();
            Assert.True(Directory.Exists(link));
            Assert.Equal(WorktreeIndex.Canonical(target), WorktreeIndex.Canonical(link), ignoreCase: true);
            Assert.NotEqual(link.TrimEnd('\\'), WorktreeIndex.Canonical(link), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Local_state_reports_uncommitted_and_unpushed_work()
    {
        var calls = new List<string>();
        string? Git(string dir, IReadOnlyList<string> args)
        {
            calls.Add(string.Join(' ', args));
            return args[0] switch { "status" => " M file.cs\n", "cat-file" => "", "rev-list" => "2\n", _ => null };
        }
        Assert.Equal(new LocalState(true, 2), WorktreeIndex.ReadLocalState(@"C:\wt", "abc", Git));
        Assert.Contains("rev-list --count abc..HEAD", calls);
    }

    [Fact]
    public void Unpushed_is_unknown_when_the_pr_head_is_not_in_the_local_repository() =>
        Assert.Equal(new LocalState(false, null), WorktreeIndex.ReadLocalState(@"C:\wt", "abc", (_, a) => a[0] switch { "status" => "", "cat-file" => null, _ => "5" }));

    [Fact]
    public void A_fork_pull_request_matches_the_worktree_in_the_fork_clone()
    {
        var pr = new PullRequest { Repo = "acme/web", HeadRef = "feat", IsCrossRepository = true, HeadRepo = "me/web" };
        Assert.True(new Worktree("me/web", "feat", @"C:\wt", @"D:\wt").Matches(pr));
        Assert.False(new Worktree("acme/web", "feat", @"C:\wt2", @"D:\wt2").Matches(pr));
        Assert.False(new Worktree("acme/web", "feat", @"C:\wt3", @"D:\wt3").Matches(pr with { HeadRepo = "" }));
    }

    [Fact]
    public void The_repositories_to_look_in_are_the_base_for_branch_prs_and_the_fork_for_fork_prs() =>
        Assert.Equal(new[] { "acme/api", "me/web" },
            WorktreeIndex.ReposOf(new[] { new PullRequest { Repo = "acme/api" }, new PullRequest { Repo = "acme/web", IsCrossRepository = true, HeadRepo = "me/web" } }).Order());

    [Fact]
    public void A_worktree_matches_its_pr_by_repository_and_head_branch()
    {
        var w = new Worktree("acme/web", "feat", @"C:\wt", @"D:\wt");
        Assert.True(w.Matches(new PullRequest { Repo = "ACME/web", HeadRef = "feat" }));
        Assert.False(w.Matches(new PullRequest { Repo = "acme/api", HeadRef = "feat" }));
        Assert.False(w.Matches(new PullRequest { Repo = "acme/web", HeadRef = "Feat" }));
    }
}
