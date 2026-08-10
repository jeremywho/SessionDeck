using Xunit;

namespace ClaudeSessionMonitor.Tests;

/// <summary>
/// Headless Codex threads (the codex plugin's "Codex Companion Task" second opinions, and `codex exec`
/// runs) have no terminal, so a row for one is a row you can't click into. They belong on the Claude
/// session that started them — but nothing links them explicitly: the app-server daemon is shared
/// between sessions and orphaned from the process tree, so attribution comes from the path they ran in.
/// </summary>
public class CodexAttributionTests
{
    static SessionInfo Claude(string id, string cwd, string status = "idle") => new()
    {
        Provider = SessionProvider.Claude, SessionId = id, Cwd = cwd, Kind = "interactive",
        Status = status, StatusUpdatedAt = new DateTime(2026, 8, 10, 9, 0, 0),
    };

    static SessionInfo Codex(string kind, string cwd) => new()
    {
        Provider = SessionProvider.Codex, SessionId = "cx-" + kind + cwd.GetHashCode(), Cwd = cwd, Kind = kind,
    };

    const string Sid = "efddf6a2-fd39-4dcd-ad6b-fcaf0b100e8e";

    [Fact]
    public void A_companion_thread_folds_onto_the_session_sharing_its_folder()
    {
        var owner = Claude("a", @"C:\Repos\X");
        var rows = CodexAttribution.Fold(new[] { owner }, new[] { Codex("companion", @"C:\Repos\X") });

        Assert.Equal(new[] { owner }, rows);      // no row of its own
        Assert.Equal(1, owner.BackgroundTasks);
    }

    [Fact]
    public void A_headless_exec_run_folds_the_same_way()
    {
        var owner = Claude("a", @"C:\Repos\X");
        CodexAttribution.Fold(new[] { owner }, new[] { Codex("exec", @"C:\Repos\X") });
        Assert.Equal(1, owner.BackgroundTasks);
    }

    // A real terminal session is something you CAN click into, so it keeps its row.
    [Fact]
    public void An_interactive_codex_session_still_gets_its_own_row()
    {
        var owner = Claude("a", @"C:\Repos\X");
        var tui = Codex("tui", @"C:\Repos\X");
        var rows = CodexAttribution.Fold(new[] { owner }, new[] { tui });

        Assert.Contains(tui, rows);
        Assert.Equal(0, owner.BackgroundTasks);
    }

    // The list is for sessions you can act on. A headless thread we can't place has no terminal AND no
    // parent to hang a badge on, so there is nothing the row could offer -- it is dropped, not shown.
    [Fact]
    public void An_unattributable_thread_is_hidden_rather_than_shown()
    {
        var owner = Claude("a", @"C:\Repos\X");
        var orphan = Codex("companion", @"C:\Somewhere\Else");
        var rows = CodexAttribution.Fold(new[] { owner }, new[] { orphan });

        Assert.DoesNotContain(orphan, rows);
        Assert.Equal(new[] { owner }, rows);
        Assert.Equal(0, owner.BackgroundTasks);
    }

    [Fact]
    public void No_headless_thread_ever_earns_a_row()
    {
        var owner = Claude("a", @"C:\Repos\X");
        var rows = CodexAttribution.Fold(new[] { owner }, new[]
        {
            Codex("companion", @"C:\Repos\X"),          // attributed -> badge
            Codex("exec", @"C:\Nowhere"),               // unattributable -> gone
        });

        Assert.Equal(new[] { owner }, rows);
        Assert.Equal(1, owner.BackgroundTasks);
    }

    // A thread launched from a session's scratchpad names its parent outright -- that beats the folder,
    // which would otherwise be the scratchpad path and match nobody.
    [Fact]
    public void A_scratchpad_path_attributes_to_the_session_that_owns_it()
    {
        var right = Claude(Sid, @"C:\Repos\X");
        var wrong = Claude("other", @"C:\Users\Jeremy");
        string scratch = $@"C:\Users\Jeremy\AppData\Local\Temp\claude\C--Users-Jeremy\{Sid}\scratchpad";

        CodexAttribution.Fold(new[] { wrong, right }, new[] { Codex("companion", scratch) });

        Assert.Equal(1, right.BackgroundTasks);
        Assert.Equal(0, wrong.BackgroundTasks);
    }

    [Theory]
    [InlineData(@"C:\Users\J\AppData\Local\Temp\claude\C--Users-J\efddf6a2-fd39-4dcd-ad6b-fcaf0b100e8e\scratchpad", Sid)]
    [InlineData(@"C:\Repos\X", null)]
    [InlineData("", null)]
    // A GUID that isn't under a "claude" segment must not be read as a session id.
    [InlineData(@"C:\builds\efddf6a2-fd39-4dcd-ad6b-fcaf0b100e8e\out", null)]
    public void Scratchpad_ids_are_only_read_under_a_claude_segment(string path, string? expected)
        => Assert.Equal(expected, CodexAttribution.ScratchpadSessionId(path));

    // Several sessions in one folder is normal (a few all in the home directory). The busy one is the
    // one blocked waiting on a second opinion, so it takes the tie.
    [Fact]
    public void When_sessions_share_a_folder_the_busy_one_wins()
    {
        var idle = Claude("idle", @"C:\Users\Jeremy");
        var busy = Claude("busy", @"C:\Users\Jeremy", status: "busy");

        CodexAttribution.Fold(new[] { idle, busy }, new[] { Codex("companion", @"C:\Users\Jeremy") });

        Assert.Equal(1, busy.BackgroundTasks);
        Assert.Equal(0, idle.BackgroundTasks);
    }

    [Fact]
    public void Folder_matching_ignores_case_and_a_trailing_separator()
    {
        var owner = Claude("a", @"C:\Repos\X");
        CodexAttribution.Fold(new[] { owner }, new[] { Codex("companion", @"c:\repos\x\") });
        Assert.Equal(1, owner.BackgroundTasks);
    }

    // The badge answers "is this session waiting on something?", so both kinds count into it.
    [Fact]
    public void The_badge_counts_subagents_and_codex_tasks_together()
    {
        var s = Claude("a", @"C:\Repos\X");
        s.SubagentsActive = 2; s.SubagentsTotal = 3; s.BackgroundTasks = 1;
        var row = new SessionRow(s);

        Assert.Equal(3, row.SubagentsActive);
        Assert.True(row.HasActiveSubagents);
        Assert.Contains("2 subagents working", row.SubagentTooltip);
        Assert.Contains("1 Codex task running", row.SubagentTooltip);
    }

    [Fact]
    public void A_session_with_no_background_work_shows_no_badge()
    {
        var row = new SessionRow(Claude("a", @"C:\Repos\X"));
        Assert.False(row.HasActiveSubagents);
        Assert.Equal("", row.SubagentTooltip);
    }
}
