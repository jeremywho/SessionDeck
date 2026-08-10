using System.IO;
using System.Linq;

namespace ClaudeSessionMonitor;

/// <summary>
/// Folds headless Codex work back onto the Claude session that started it.
///
/// The Claude Code codex plugin runs its second opinions as real Codex threads through a shared
/// app-server daemon. They're genuinely live, but they have no terminal and nothing to click into —
/// so listing them as sessions of their own gives you rows you can't act on, and buries the session
/// that's actually waiting on them. Rolled up, they show as a count on their parent instead.
///
/// Attribution has no explicit link to follow: the daemon is shared between every Claude session and
/// is orphaned from the process tree (its parent, the plugin's broker script, has already exited), so
/// walking parents finds nothing. What the rollout does carry is the working directory it was started
/// in, and that's enough — see <see cref="OwnerOf"/>.
/// </summary>
internal static class CodexAttribution
{
    /// <summary>
    /// The rows to display: Claude sessions (carrying a count of the Codex work they started) and
    /// interactive Codex sessions.
    /// <para>A headless thread never earns a row. Attributed, it becomes a badge on its parent;
    /// unattributable, it is dropped entirely. That's deliberate: the list is for sessions you can act
    /// on, and a row you can't click into is worse than absent — it takes up space, pushes down the
    /// session that's actually waiting, and offers nothing to do about it.</para>
    /// </summary>
    public static List<SessionInfo> Fold(IReadOnlyList<SessionInfo> claude, IReadOnlyList<SessionInfo> codex,
                                         IReadOnlyDictionary<int, int>? parents = null)
    {
        // Only sessions you could actually sit in are candidate parents — a headless session can't
        // own another, and folding onto one would just hide work behind something else hidden.
        var owners = claude.Where(c => !IsHeadless(c)).ToList();
        var rows = new List<SessionInfo>(owners);

        foreach (var s in claude.Where(IsHeadless).Concat(codex))
        {
            if (!IsHeadless(s)) { rows.Add(s); continue; }

            var owner = OwnerOf(s, owners, parents);
            if (owner != null) owner.BackgroundTasks++;   // no owner -> not shown at all
        }
        return rows;
    }

    /// <summary>
    /// Work with no terminal behind it, from either CLI: Codex companion threads driven through the
    /// app server and headless `codex exec` runs, plus Claude's own <c>bg</c> sessions (what
    /// <c>/tr:pr</c> and friends spawn — they resolve to no window, so double-click can only fail).
    /// <para>Claude's <c>bg</c> is matched by name rather than "anything not interactive": an
    /// unrecognised kind is far more likely to be a real terminal than a headless job, and hiding a
    /// session you could have clicked into is the expensive mistake.</para>
    /// </summary>
    public static bool IsHeadless(SessionInfo s) => s.Provider == SessionProvider.Codex
        ? s.Kind == "companion" || s.Kind == "exec"
        : s.Kind == "bg";

    /// <summary>
    /// Which Claude session started this thread.
    ///
    /// Two signals, strongest first. A thread launched from a session's scratchpad carries that
    /// session's id inside its path, which is exact. Otherwise all we have is the working directory,
    /// which is usually decisive — and when several sessions share one, the one that's <c>busy</c> is
    /// the one blocked waiting on a second opinion, so it wins the tie.
    /// </summary>
    public static SessionInfo? OwnerOf(SessionInfo headless, IReadOnlyList<SessionInfo> claude,
                                       IReadOnlyDictionary<int, int>? parents = null)
    {
        // Strongest of all when it's available: a Claude bg session is a child PROCESS of the session
        // that started it, so walking up the tree names the parent outright. (Codex threads can't use
        // this — they're served by a shared daemon that isn't anyone's child.)
        var byTree = OwnerByProcessTree(headless, claude, parents);
        if (byTree != null) return byTree;

        string? id = ScratchpadSessionId(headless.Cwd);
        if (id != null)
        {
            var exact = claude.FirstOrDefault(c =>
                string.Equals(c.SessionId, id, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        var sharing = claude.Where(c => SamePath(c.Cwd, headless.Cwd)).ToList();
        if (sharing.Count == 0) return null;
        if (sharing.Count == 1) return sharing[0];

        return sharing
            .OrderByDescending(c => c.Status == "busy")
            .ThenByDescending(c => c.StatusUpdatedAt)
            .First();
    }

    /// <summary>
    /// The nearest ancestor process that is itself a listed session, or null. Bounded rather than
    /// unbounded: a deep walk on a machine with many nested shells is how you end up attributing a job
    /// to something several layers removed that merely happens to be an ancestor.
    /// </summary>
    public static SessionInfo? OwnerByProcessTree(SessionInfo headless, IReadOnlyList<SessionInfo> claude,
                                                  IReadOnlyDictionary<int, int>? parents)
    {
        if (parents == null || headless.Pid == 0) return null;

        int pid = headless.Pid;
        for (int hop = 0; hop < 12; hop++)
        {
            if (!parents.TryGetValue(pid, out int parent) || parent == 0 || parent == pid) return null;
            var owner = claude.FirstOrDefault(c => c.Pid == parent);
            if (owner != null) return owner;
            pid = parent;
        }
        return null;
    }

    /// <summary>
    /// The Claude session id embedded in a scratchpad path, or null.
    /// Scratchpads live at <c>…\Temp\claude\&lt;cwd-slug&gt;\&lt;sessionId&gt;\scratchpad</c>, so a
    /// thread started in one names its parent outright.
    /// </summary>
    public static string? ScratchpadSessionId(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            // Anchored on the "claude" segment so an unrelated GUID elsewhere in a path can't be
            // mistaken for a session id.
            if (!parts[i].Equals("claude", StringComparison.OrdinalIgnoreCase)) continue;
            for (int j = i + 1; j < parts.Length && j <= i + 3; j++)
                if (Guid.TryParse(parts[j], out _)) return parts[j];
        }
        return null;
    }

    static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        return string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);
        static string Trim(string p) => p.TrimEnd('\\', '/');
    }
}
