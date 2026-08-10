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

    // Claude's own bg sessions (what /tr:pr spawns) have no terminal either — double-click could only
    // fail on them. Unlike a Codex thread, a bg session IS a child process of the session that started
    // it, so the process tree names the parent outright.
    [Fact]
    public void A_claude_bg_session_folds_onto_the_session_that_spawned_it()
    {
        var parent = Claude("p", @"C:\Repos\X"); parent.Pid = 100;
        var bg = new SessionInfo { Provider = SessionProvider.Claude, SessionId = "bg", Kind = "bg", Pid = 300, Cwd = @"C:\Nowhere" };
        var tree = new Dictionary<int, int> { [300] = 200, [200] = 100 };   // bg <- shell <- parent

        var rows = CodexAttribution.Fold(new[] { parent, bg }, Array.Empty<SessionInfo>(), tree);

        Assert.Equal(new[] { parent }, rows);       // the bg row is gone
        Assert.Equal(1, parent.BackgroundTasks);
    }

    [Fact]
    public void A_bg_session_with_no_findable_parent_is_hidden()
    {
        var other = Claude("p", @"C:\Repos\X"); other.Pid = 100;
        var bg = new SessionInfo { Provider = SessionProvider.Claude, SessionId = "bg", Kind = "bg", Pid = 300, Cwd = @"C:\Nowhere" };

        var rows = CodexAttribution.Fold(new[] { other, bg }, Array.Empty<SessionInfo>(), parents: null);

        Assert.Equal(new[] { other }, rows);
        Assert.Equal(0, other.BackgroundTasks);
    }

    // An interactive session is never folded away, however it's nested.
    [Fact]
    public void An_interactive_claude_session_is_never_hidden()
    {
        var parent = Claude("p", @"C:\Repos\X"); parent.Pid = 100;
        var child = Claude("c", @"C:\Repos\X"); child.Pid = 300;
        var tree = new Dictionary<int, int> { [300] = 100 };

        var rows = CodexAttribution.Fold(new[] { parent, child }, Array.Empty<SessionInfo>(), tree);

        Assert.Contains(child, rows);
        Assert.Equal(0, parent.BackgroundTasks);
    }

    // A headless session must not become somebody's parent, or work ends up hidden behind something
    // that is itself hidden.
    [Fact]
    public void A_headless_session_is_not_a_candidate_parent()
    {
        var hiddenParent = new SessionInfo { Provider = SessionProvider.Claude, SessionId = "h", Kind = "bg", Pid = 100, Cwd = @"C:\Repos\X" };
        var bg = new SessionInfo { Provider = SessionProvider.Claude, SessionId = "bg", Kind = "bg", Pid = 300, Cwd = @"C:\Repos\X" };
        var tree = new Dictionary<int, int> { [300] = 100 };

        var rows = CodexAttribution.Fold(new[] { hiddenParent, bg }, Array.Empty<SessionInfo>(), tree);

        Assert.Empty(rows);
        Assert.Equal(0, hiddenParent.BackgroundTasks);
    }

    // Codex threads come from a shared daemon that is nobody's child, so the tree must not be consulted
    // for them -- an unrelated ancestor would be attributed as the parent.
    [Fact]
    public void The_process_tree_walk_stops_rather_than_guessing_at_depth()
    {
        var far = Claude("far", @"C:\Repos\X"); far.Pid = 1;
        var bg = new SessionInfo { Provider = SessionProvider.Claude, SessionId = "bg", Kind = "bg", Pid = 500, Cwd = @"C:\Nowhere" };
        var tree = new Dictionary<int, int>();
        for (int i = 500; i > 1; i--) tree[i] = i - 1;      // a 500-deep chain ending at the session

        Assert.Null(CodexAttribution.OwnerByProcessTree(bg, new[] { far }, tree));
    }

    [Fact]
    public void A_session_with_no_background_work_shows_no_badge()
    {
        var row = new SessionRow(Claude("a", @"C:\Repos\X"));
        Assert.False(row.HasActiveSubagents);
        Assert.Equal("", row.SubagentTooltip);
    }
}

/// <summary>
/// A Codex session launched from the buttons has to carry the bypass flag, the same way the Claude
/// box carries --dangerously-skip-permissions. It's filled in once for files written before the box
/// existed, and must not come back if you clear it on purpose.
/// </summary>
public class CodexFlagDefaultTests
{
    [Fact]
    public void A_fresh_install_launches_codex_with_the_bypass_flag()
    {
        var s = new Settings();
        Assert.Equal(Settings.DefaultCodexFlags, s.CodexFlags);
        Assert.Equal("codex --dangerously-bypass-approvals-and-sandbox",
            SessionLauncher.NewCodexCommand(s.CodexFlags));
    }

    [Fact]
    public void An_older_settings_file_gets_the_flag_filled_in_once()
    {
        var s = new Settings { CodexFlags = "", FlagsVersion = 0 };   // written before the box existed
        Assert.True(s.ApplyNewDefaults());
        Assert.Equal(Settings.DefaultCodexFlags, s.CodexFlags);
        Assert.False(s.ApplyNewDefaults());                           // and only once
    }

    [Fact]
    public void Clearing_the_box_on_purpose_is_not_undone()
    {
        var s = new Settings { CodexFlags = "", FlagsVersion = 1 };   // already migrated, then cleared
        Assert.False(s.ApplyNewDefaults());
        Assert.Equal("", s.CodexFlags);
    }

    [Fact]
    public void Flags_someone_chose_are_left_alone()
    {
        var s = new Settings { CodexFlags = "--search", FlagsVersion = 0 };
        s.ApplyNewDefaults();
        Assert.Equal("--search", s.CodexFlags);
    }

    // Resume uses the same box, so a restored Codex thread gets the flag too.
    [Fact]
    public void A_resumed_codex_session_carries_the_flag_as_well()
    {
        var saved = new SavedSession { Id = "019f", Provider = SessionProvider.Codex };
        Assert.Equal("codex resume 019f --dangerously-bypass-approvals-and-sandbox",
            SessionLauncher.ResumeCommand(saved, new Settings().CodexFlags));
    }
}
