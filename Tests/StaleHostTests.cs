using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>A host record that outlived its process, as after a reboot, must neither count as alive nor take a session from the host that runs it.</summary>
public class StaleHostTests
{
    static readonly long Boot = new DateTime(2026, 10, 15, 8, 22, 39, DateTimeKind.Utc).Ticks;
    static long At(int hoursFromBoot) => Boot + TimeSpan.TicksPerHour * hoursFromBoot;

    [Fact]
    public void A_record_from_before_this_boot_is_dead_whatever_now_holds_its_pid()
    {
        Assert.False(HostLiveness.Alive(At(-14), null, "svchost", Boot));
        Assert.False(HostLiveness.Alive(At(-14), null, "SessionDeck", Boot));
        Assert.False(HostLiveness.Alive(At(-14), At(1), "SessionDeck", Boot));
    }

    [Fact]
    public void A_record_from_this_boot_is_alive_only_when_the_process_matches()
    {
        Assert.True(HostLiveness.Alive(At(1), At(1), "SessionDeck", Boot));
        Assert.False(HostLiveness.Alive(At(1), At(2), "SessionDeck", Boot));
        Assert.True(HostLiveness.Alive(At(1), null, "SessionDeck", Boot));
        Assert.False(HostLiveness.Alive(At(1), null, "svchost", Boot));
        Assert.False(HostLiveness.Alive(At(1), null, "", Boot));
    }

    [Theory]
    [InlineData(0x40010004u, true)]
    [InlineData(0xC000013Au, true)]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    public void A_cli_closed_under_it_counts_as_killed_and_a_clean_exit_does_not(uint code, bool killed)
    {
        var h = new HostRecord { Id = "h", ExitCode = code };
        Assert.True(h.HasExited);
        Assert.Equal(killed, h.WasKilled);
        Assert.False(new HostRecord { Id = "live" }.WasKilled);
    }

    static HostRecord Host(string id, string session, int startHour, bool exited) => new()
    {
        Id = id, SessionId = session, Provider = "Claude", HostPid = 1, ChildPid = 1,
        StartedAt = new DateTime(2026, 10, 15, startHour, 0, 0, DateTimeKind.Utc), ExitCode = exited ? 1u : null,
    };

    [Fact]
    public void One_session_goes_to_one_host_and_the_running_host_wins_over_the_exited_one()
    {
        var stale = Host("old", "s1", 1, exited: true);
        var fresh = Host("new", "s1", 9, exited: false);
        var info = new SessionInfo { SessionId = "s1", Pid = 7, Status = "idle", Kind = "interactive" };
        var claimed = new HashSet<SessionInfo>();

        var pairs = HostPairing.Pair(new[] { stale, fresh }, new[] { info }, (_, _) => false, claimed);

        Assert.Equal(2, pairs.Count);
        Assert.Same(info, pairs.Single(p => p.Host == fresh).Info);
        Assert.NotSame(info, pairs.Single(p => p.Host == stale).Info);
        Assert.Equal("s1", pairs.Single(p => p.Host == stale).Info.SessionId);
        Assert.Equal(pairs.Count, pairs.Select(p => p.Info).Distinct().Count());
    }

    [Fact]
    public void Between_two_running_hosts_the_newer_claims_and_descent_only_matches_unclaimed_sessions()
    {
        var older = Host("a", "", 1, exited: false);
        var newer = Host("b", "s1", 9, exited: false);
        var info = new SessionInfo { SessionId = "s1", Pid = 7, Status = "busy", Kind = "interactive" };
        var pairs = HostPairing.Pair(new[] { older, newer }, new[] { info }, (_, _) => true, new HashSet<SessionInfo>());
        Assert.Same(info, pairs.Single(p => p.Host == newer).Info);
        Assert.NotSame(info, pairs.Single(p => p.Host == older).Info);
    }
}
