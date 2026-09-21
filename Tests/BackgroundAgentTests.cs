using Xunit;

namespace SessionDeck.Tests;

/// <summary>
/// Companion threads — codex threads another app drives through the app server (the Claude Code
/// codex plugin's "Codex Companion Task" second opinions) — must be classified as background
/// agents, not terminal sessions: no focus target, no restore, an agent badge instead of a
/// session identity. Header facts observed live: a companion rollout carries
/// originator "Claude Code" / source "vscode", the interactive TUI carries "codex-tui" / "cli".
/// </summary>
public class BackgroundAgentTests
{
    [Theory]
    [InlineData(100, 100, 15000)]
    [InlineData(2000, 100, 30000)]
    [InlineData(100, 1000, 30000)]
    [InlineData(5000, 100, 60000)]
    [InlineData(100, 3000, 60000)]
    public void Desktop_resolver_backs_off_from_slow_UIA_or_session_scans(
        long uiaMs, long scanMs, int expectedDelayMs)
        => Assert.Equal(expectedDelayMs, SessionsWindow.DesktopResolverDelayMs(uiaMs, scanMs));

    [Fact]
    public void Known_owner_verification_is_bounded_and_rotates_to_the_remainder()
    {
        var now = DateTime.UtcNow;
        var owners = Enumerable.Range(1, 12).Select(i => $"rollout-{i}").ToList();
        var verified = new Dictionary<string, DateTime>();

        var first = CodexScanner.DueForVerification(owners, verified, now);
        Assert.Equal(8, first.Count);
        foreach (var path in first) verified[path] = now;

        var second = CodexScanner.DueForVerification(owners, verified, now);
        Assert.Equal(owners.Skip(8), second);
    }

    [Fact]
    public void Recently_verified_owner_is_not_due_again()
    {
        var now = DateTime.UtcNow;
        var verified = new Dictionary<string, DateTime> { ["rollout"] = now - TimeSpan.FromSeconds(29) };

        Assert.Empty(CodexScanner.DueForVerification(new[] { "rollout" }, verified, now));
        Assert.Single(CodexScanner.DueForVerification(new[] { "rollout" }, verified,
            now + TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("codex-tui", "tui")]           // a terminal a human sits in
    [InlineData("codex_exec", "exec")]         // headless one-shot
    [InlineData("Claude Code", "companion")]   // the codex plugin's companion tasks
    [InlineData("VS Code", "companion")]       // IDE extensions land in the same bucket
    [InlineData("", "tui")]                    // unknown stays a terminal: wrong-as-companion breaks focus+restore
    public void Originator_maps_to_kind(string originator, string expected)
        => Assert.Equal(expected, CodexScanner.KindFor(originator));

    [Fact]
    public void Companion_row_is_a_background_agent_and_never_restorable()
    {
        var row = new SessionRow(new SessionInfo
        {
            Provider = SessionProvider.Codex,
            SessionId = "019fe4df-0403-78b0-9e90-5ff3198a90b8",
            Kind = CodexScanner.KindFor("Claude Code"),
            Name = "Codex Companion Task: <task>Give a second opinion on renderer strategy",
        });

        Assert.True(row.IsBackgroundAgent);
        Assert.Equal("Give a second opinion on renderer strategy", row.Name);
        Assert.Contains("Background agent", row.RowTooltip);
        Assert.Contains("Codex Companion Task", row.RowTooltip);   // the raw name stays inspectable
    }

    [Fact]
    public void Tui_row_is_a_plain_session()
    {
        var row = new SessionRow(new SessionInfo
        {
            Provider = SessionProvider.Codex,
            SessionId = "019fcf26-6918-7ff3-8bc4-b7e817722df5",
            Kind = CodexScanner.KindFor("codex-tui"),
            Name = "sweep",
        });

        Assert.False(row.IsBackgroundAgent);
        Assert.Equal("sweep", row.Name);
        Assert.DoesNotContain("Background agent", row.RowTooltip);
    }

    [Theory]
    [InlineData("Codex Companion Task: <task>Fix the flaky test", "Fix the flaky test")]
    [InlineData("Codex Companion Task: plain excerpt", "plain excerpt")]
    [InlineData("Codex Companion Task", "Codex agent")]      // bare prefix -> a name, not an empty cell
    [InlineData("my own thread name", "my own thread name")] // non-companion names pass through
    public void Companion_name_shows_the_task_text(string raw, string expected)
        => Assert.Equal(expected, SessionRow.CompanionName(raw));
}
