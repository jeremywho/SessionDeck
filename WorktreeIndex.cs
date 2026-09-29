using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SessionDeck;

internal sealed record Worktree(string Repo, string Branch, string Path, string CanonicalPath)
{
    public bool Matches(PullRequest pr) => string.Equals(Repo, pr.Repo, StringComparison.OrdinalIgnoreCase) && Branch == pr.HeadRef;
}

internal sealed record LocalState(bool Uncommitted, int? Unpushed);

internal static class WorktreeIndex
{
    static readonly Regex GitHubRemote = new(@"github\.com[:/]+([^/\s]+)/([^/\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? RepoFromUrl(string url)
    {
        var m = GitHubRemote.Match(url.Trim());
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
    }

    public static string? OriginUrl(string gitConfig)
    {
        bool inOrigin = false;
        foreach (var raw in gitConfig.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = line.Replace(" ", "").Equals("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inOrigin || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase)) continue;
            int eq = line.IndexOf('=');
            if (eq > 0) return line[(eq + 1)..].Trim();
        }
        return null;
    }

    public static List<(string Path, string? Branch)> ParsePorcelain(string text)
    {
        var list = new List<(string, string?)>();
        string? path = null, branch = null;
        bool skip = false;
        foreach (var raw in (text + "\n\n").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (path != null && !skip) list.Add((path, branch));
                path = null; branch = null; skip = false;
                continue;
            }
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..];
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal)) branch = line["branch refs/heads/".Length..];
            else if (line == "bare" || line.StartsWith("prunable", StringComparison.Ordinal)) skip = true;
        }
        return list;
    }

    public static List<(string Clone, string Repo)> FindClones(IEnumerable<string> roots)
    {
        var found = new List<(string, string)>();
        foreach (var root in roots)
        {
            List<string> dirs;
            try { dirs = Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToList() : new List<string>(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var dir in dirs)
            {
                string config = System.IO.Path.Combine(dir, ".git", "config");
                try
                {
                    if (File.Exists(config) && OriginUrl(File.ReadAllText(config)) is { } url && RepoFromUrl(url) is { } repo) found.Add((dir, repo));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return found;
    }

    public static List<Worktree> Build(IEnumerable<(string Clone, string Repo)> clones, IReadOnlySet<string> repos,
                                       Func<string, string?> listPorcelain, Func<string, string> canonical)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Worktree>();
        foreach (var (clone, repo) in clones)
        {
            if (!repos.Contains(repo) || listPorcelain(clone) is not { } text) continue;
            foreach (var (path, branch) in ParsePorcelain(text))
            {
                if (branch == null) continue;
                string canon = canonical(path);
                if (seen.Add(canon)) result.Add(new Worktree(repo, branch, path.Replace('/', '\\'), canon));
            }
        }
        return result;
    }

    public static string Canonical(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path.Replace('/', '\\')); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        return (Native.FinalPath(full) ?? full).TrimEnd('\\');
    }

    public static LocalState ReadLocalState(string worktree, string headOid, Func<string, IReadOnlyList<string>, string?> git)
    {
        bool dirty = git(worktree, new[] { "status", "--porcelain" }) is { } status && status.Trim().Length > 0;
        int? unpushed = null;
        if (headOid.Length > 0 && git(worktree, new[] { "cat-file", "-e", headOid + "^{commit}" }) != null
            && int.TryParse(git(worktree, new[] { "rev-list", "--count", headOid + "..HEAD" })?.Trim(), out int n))
            unpushed = n;
        return new LocalState(dirty, unpushed);
    }

    /// <summary>Worktrees of the repositories these PRs live in, and the local state of each PR's own worktree, keyed by canonical path.</summary>
    public static (List<Worktree> Worktrees, Dictionary<string, LocalState> Local) Scan(IReadOnlyList<PullRequest> prs, IEnumerable<string> roots)
    {
        var repos = new HashSet<string>(prs.Select(p => p.Repo), StringComparer.OrdinalIgnoreCase);
        var worktrees = Build(FindClones(roots), repos, clone => GitCli.Run(clone, new[] { "worktree", "list", "--porcelain" }), Canonical);
        var local = new Dictionary<string, LocalState>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in prs)
            if (worktrees.FirstOrDefault(w => w.Matches(pr)) is { } w && !local.ContainsKey(w.CanonicalPath))
                local[w.CanonicalPath] = ReadLocalState(w.CanonicalPath, pr.HeadOid, GitCli.Run);
        return (worktrees, local);
    }
}

internal static class GitCli
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static string? Run(string workdir, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        // A status refresh must never take index.lock from under an agent working in the same worktree.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(workdir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            Task.WaitAll(stdout, stderr);
            return p.ExitCode == 0 ? stdout.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
