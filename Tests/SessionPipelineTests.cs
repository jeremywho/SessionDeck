using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class SessionPipelineTests
{
    static ScanResult Scan(List<SessionInfo> sessions, List<HostRecord> hosts, Dictionary<int, int>? parents = null) =>
        new(sessions, hosts, parents ?? new(), new(), sessions.Count, 0, 0);

    [Fact]
    public void Pairing_applies_the_hosts_hook_state_to_the_scanned_session()
    {
        var host = new HostRecord { Id = "h", SessionId = "s1", Provider = "Claude", ChildPid = 5, AgentStatus = "busy", LastEvent = "PreToolUse", LastTool = "Bash", StatusAt = DateTime.UtcNow, Protocol = 4 };
        var info = new SessionInfo { SessionId = "s1", Pid = 7, Status = "idle", Kind = "interactive", StatusUpdatedAt = DateTime.Now.AddMinutes(-5) };
        var pairs = SessionPipeline.Pair(Scan(new() { info }, new() { host }));
        Assert.Same(info, pairs.Single().Info);
        Assert.Equal("busy", pairs.Single().Info.Status);
        Assert.Equal("Bash", pairs.Single().Info.LastTool);
    }

    [Fact]
    public void A_host_with_no_scanned_session_gets_a_placeholder_of_its_own_provider()
    {
        var host = new HostRecord { Id = "h", Provider = "Agy", ChildPid = 5 };
        var pairs = SessionPipeline.Pair(Scan(new(), new() { host }));
        Assert.Equal(SessionProvider.Agy, pairs.Single().Info.Provider);
        Assert.Equal("h", pairs.Single().Host.Id);
    }

    [Fact]
    public void A_codex_tui_is_paired_to_its_host_by_process_descent_and_unowned_sessions_stay_unclaimed()
    {
        var host = new HostRecord { Id = "h", Provider = "Codex", ChildPid = 10 };
        var tui = new SessionInfo { SessionId = "t1", Pid = 30, Provider = SessionProvider.Codex, Kind = "tui" };
        var elsewhere = new SessionInfo { SessionId = "t2", Pid = 99, Provider = SessionProvider.Codex, Kind = "tui" };
        var parents = new Dictionary<int, int> { [30] = 20, [20] = 10, [99] = 1 };
        var claimed = new HashSet<SessionInfo>();
        var pairs = SessionPipeline.Pair(Scan(new() { tui, elsewhere }, new() { host }, parents), claimed);
        Assert.Same(tui, pairs.Single().Info);
        Assert.Contains(tui, claimed);
        Assert.DoesNotContain(elsewhere, claimed);
    }
}
