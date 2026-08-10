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
    public static List<SessionInfo> Fold(IReadOnlyList<SessionInfo> claude, IReadOnlyList<SessionInfo> codex)
    {
        var rows = new List<SessionInfo>(claude);

        foreach (var c in codex)
        {
            if (!IsHeadless(c)) { rows.Add(c); continue; }

            var owner = OwnerOf(c, claude);
            if (owner != null) owner.BackgroundTasks++;   // no owner -> not shown at all
        }
        return rows;
    }

    /// <summary>
    /// Codex work with no terminal behind it: companion threads driven through the app server, and
    /// headless `codex exec` runs. Interactive TUI sessions are left alone.
    /// </summary>
    public static bool IsHeadless(SessionInfo s) =>
        s.Provider == SessionProvider.Codex && (s.Kind == "companion" || s.Kind == "exec");

    /// <summary>
    /// Which Claude session started this thread.
    ///
    /// Two signals, strongest first. A thread launched from a session's scratchpad carries that
    /// session's id inside its path, which is exact. Otherwise all we have is the working directory,
    /// which is usually decisive — and when several sessions share one, the one that's <c>busy</c> is
    /// the one blocked waiting on a second opinion, so it wins the tie.
    /// </summary>
    public static SessionInfo? OwnerOf(SessionInfo headless, IReadOnlyList<SessionInfo> claude)
    {
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
