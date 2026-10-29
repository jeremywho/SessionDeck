using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class SessionCatalogTests
{
    static DateTime At(int minutes) => new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);

    static (HostRecord, SessionInfo) Pair(string hostId, string sessionId, string provider, string status, string title, string model = "", string kind = "interactive", int protocol = 4, DateTime? statusAt = null)
    {
        var at = statusAt ?? At(0);
        var host = new HostRecord { Id = hostId, SessionId = sessionId, Provider = provider, Port = 5555, Token = "SECRETTOKEN", AgentStatus = status, LastEvent = "Stop", StatusAt = at, Protocol = protocol, Title = title, StartedAt = At(-60) };
        var info = new SessionInfo
        {
            SessionId = sessionId, Status = status, Kind = kind, Model = model, Name = title,
            Provider = SessionRow.ProviderOf(provider), StatusUpdatedAt = at.ToLocalTime(), UpdatedAt = at.ToLocalTime(),
        };
        return (host, info);
    }

    [Fact]
    public void Entries_carry_state_group_and_order_and_never_a_port_or_token()
    {
        var settings = new Settings { RowOrder = Settings.RowOrderState, SessionGroups = new() { new SessionGroup { Name = "tr", Members = new() { "s2" } } } };
        var a = Pair("h1", "s1", "Claude", "busy", "alpha", "claude-opus-5");
        var b = Pair("h2", "s2", "Codex", "idle", "beta", "gpt-5.6-sol", kind: "tui");
        var entries = new SessionCatalog().Build(new() { a, b }, settings, At(1));
        Assert.Equal(new[] { "h1", "h2" }, entries.Select(e => e.HostId));
        Assert.Equal("Working", entries[0].State);
        Assert.Equal("Opus 5", entries[0].ModelChip);
        Assert.Equal("", entries[0].Group);
        Assert.Equal("tr", entries[1].Group);
        Assert.Equal(1, entries[1].GroupOrder);
        Assert.Equal("Codex", entries[1].Provider);
        Assert.True(entries[0].MobileReady);
        string json = SessionCatalog.ToJson(entries);
        Assert.DoesNotContain("SECRETTOKEN", json);
        Assert.DoesNotContain("5555", json);
        Assert.Contains("\"hostId\":\"h1\"", json);
        Assert.Contains("\"state\":\"Working\"", json);
        Assert.Equal(1, settings.SessionGroups.Count);
        Assert.Equal(new[] { "s2" }, settings.SessionGroups[0].Members);
    }

    [Fact]
    public void Exited_hosts_and_shell_panes_are_entries_without_surprises()
    {
        var exitedHost = new HostRecord { Id = "h3", SessionId = "s3", Provider = "Claude", ExitCode = 0, ExitedAt = At(0), Protocol = 4 };
        var shellHost = new HostRecord { Id = "h4", Provider = "Shell", Title = "Terminal", Protocol = 4 };
        var entries = new SessionCatalog().Build(new()
        {
            (exitedHost, new SessionInfo { SessionId = "s3", Kind = "interactive" }),
            (shellHost, SessionRow.Placeholder(shellHost)),
        }, new Settings(), At(1));
        var exited = entries.Single(e => e.HostId == "h3");
        Assert.Equal("Idle", exited.State);
        Assert.True(exited.HostExited);
        Assert.False(exited.MobileReady);
        var shell = entries.Single(e => e.HostId == "h4");
        Assert.Equal("Terminal", shell.Name);
        Assert.Equal("Shell", shell.Provider);
        Assert.True(shell.MobileReady);
    }

    [Fact]
    public void Only_hosts_that_report_their_size_are_mobile_ready()
    {
        var old = Pair("h5", "s5", "Claude", "idle", "old", protocol: 3);
        var fresh = Pair("h6", "s6", "Claude", "idle", "fresh", protocol: 4);
        var entries = new SessionCatalog().Build(new() { old, fresh }, new Settings(), At(1));
        Assert.False(entries.Single(e => e.HostId == "h5").MobileReady);
        Assert.True(entries.Single(e => e.HostId == "h6").MobileReady);
    }

    // The window keeps its rows and updates them; a catalog that made fresh rows each cycle would
    // restart StateSince on Claude's idle notification a minute after the turn, and the phone would
    // show a viewed session as unread again (ReadLedgerTests pins the same rule for the window).
    [Fact]
    public void A_rebuilt_catalog_keeps_the_time_a_session_completed()
    {
        var catalog = new SessionCatalog();
        var first = Pair("h1", "s1", "Claude", "idle", "alpha", statusAt: At(10));
        long since = catalog.Build(new() { first }, new Settings(), At(10)).Single().StateSinceMs;
        Assert.Equal(new DateTimeOffset(At(10)).ToUnixTimeMilliseconds(), since);

        var later = Pair("h1", "s1", "Claude", "idle", "alpha", statusAt: At(10));
        later.Item2.StatusUpdatedAt = At(11).ToLocalTime();
        later.Item2.UpdatedAt = At(11).ToLocalTime();
        var again = catalog.Build(new() { later }, new Settings(), At(11)).Single();
        Assert.Equal("Completed", again.State);
        Assert.Equal(since, again.StateSinceMs);

        Assert.Empty(catalog.Build(new(), new Settings(), At(12)));
        var back = catalog.Build(new() { later }, new Settings(), At(13)).Single();
        Assert.Equal(new DateTimeOffset(At(11)).ToUnixTimeMilliseconds(), back.StateSinceMs);
    }

    [Fact]
    public void Name_order_sorts_alphabetically_within_groups_with_exited_hosts_last()
    {
        var settings = new Settings { RowOrder = Settings.RowOrderName };
        var z = Pair("h1", "s1", "Claude", "busy", "zeta");
        var a = Pair("h2", "s2", "Claude", "idle", "alpha");
        var gone = Pair("h3", "s3", "Claude", "idle", "aardvark");
        gone.Item1.ExitCode = 0; gone.Item1.ExitedAt = At(0);
        var entries = new SessionCatalog().Build(new() { z, a, gone }, settings, At(1));
        Assert.Equal(new[] { "alpha", "zeta", "aardvark" }, entries.Select(e => e.Name));
    }
}
