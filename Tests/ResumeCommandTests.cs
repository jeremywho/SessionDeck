using System.Text.Json;
using Xunit;

namespace ClaudeSessionMonitor.Tests;

public class ResumeCommandTests
{
    static SavedSession Claude(string name = "pr quality pipeline") => new()
    {
        Id = "ce7cc3e4-8133-48ce-ab17-6f49db935eb1", Cwd = @"C:\x", Name = name,
        Provider = SessionProvider.Claude,
    };

    static SavedSession Codex(string name = "sweep") => new()
    {
        Id = "019fcf26-6918-7ff3-8bc4-b7e817722df5", Cwd = @"C:\x", Name = name,
        Provider = SessionProvider.Codex,
    };

    [Fact]
    public void Claude_resumes_by_id_and_carries_its_name_across()
    {
        var cmd = SessionLauncher.ResumeCommand(Claude(), "");
        Assert.Equal("claude --resume ce7cc3e4-8133-48ce-ab17-6f49db935eb1 --name 'pr quality pipeline'", cmd);
    }

    // `codex resume` has no --name: on Codex's side the name already belongs to the thread, so
    // passing one would just be an unknown argument and the CLI would exit.
    [Fact]
    public void Codex_resumes_by_id_alone_with_no_name_flag()
    {
        var cmd = SessionLauncher.ResumeCommand(Codex(), "");
        Assert.Equal("codex resume 019fcf26-6918-7ff3-8bc4-b7e817722df5", cmd);
        Assert.DoesNotContain("--name", cmd);
        Assert.DoesNotContain("--resume", cmd);   // it's a subcommand, not a flag
    }

    [Fact]
    public void Each_cli_gets_its_own_flags_appended()
    {
        Assert.EndsWith("--dangerously-skip-permissions",
            SessionLauncher.ResumeCommand(Claude(), "--dangerously-skip-permissions"));
        Assert.EndsWith("--dangerously-bypass-approvals-and-sandbox",
            SessionLauncher.ResumeCommand(Codex(), "  --dangerously-bypass-approvals-and-sandbox  "));
    }

    // A name that's just the short id is Claude's own fallback display name, not something the user
    // set — passing it back as --name would invent a name for a session that never had one.
    [Fact]
    public void A_short_id_placeholder_name_is_not_passed_back_as_a_name()
    {
        var s = Claude("ce7cc3e4");
        Assert.DoesNotContain("--name", SessionLauncher.ResumeCommand(s, ""));
    }

    [Fact]
    public void A_quote_in_a_session_name_is_escaped_for_pwsh()
    {
        var cmd = SessionLauncher.ResumeCommand(Claude("jeremy's run"), "");
        Assert.Contains("--name 'jeremy''s run'", cmd);
    }

    // active-sessions.json predates Codex support, so existing entries carry no Provider at all.
    // They have to come back as Claude -- resuming one with `codex resume` would go nowhere.
    [Fact]
    public void A_registry_entry_written_before_codex_support_reads_back_as_claude()
    {
        const string legacy = """
        [{"Id":"ce7cc3e4-8133-48ce-ab17-6f49db935eb1","Cwd":"C:\\x","Name":"old","Model":"claude-opus-4-8","LastSeen":1785898168671}]
        """;
        var list = JsonSerializer.Deserialize<List<SavedSession>>(legacy)!;
        Assert.Equal(SessionProvider.Claude, Assert.Single(list).Provider);
        Assert.StartsWith("claude --resume", SessionLauncher.ResumeCommand(list[0], ""));
    }

    [Fact]
    public void Provider_round_trips_through_the_registry_file_as_a_readable_name()
    {
        string json = JsonSerializer.Serialize(new List<SavedSession> { Codex() });
        Assert.Contains("\"Provider\":\"Codex\"", json);   // a name, not the enum's number
        var back = JsonSerializer.Deserialize<List<SavedSession>>(json)!;
        Assert.Equal(SessionProvider.Codex, back[0].Provider);
    }
}
