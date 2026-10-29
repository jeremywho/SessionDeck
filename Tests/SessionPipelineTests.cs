using System.IO;
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

public class ReadOnlyReadersTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-ro-" + Guid.NewGuid().ToString("N"));
    public ReadOnlyReadersTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Peek_never_writes_and_load_does()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{\"FlagsVersion\":0,\"CodexFlags\":\"\",\"Theme\":\"Light\"}");
        byte[] before = File.ReadAllBytes(path);
        var peeked = Settings.Peek(path);
        Assert.Equal("Light", peeked.Theme);
        Assert.Equal("", peeked.CodexFlags);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_dir).Where(f => f != path));

        File.WriteAllText(path, "not json");
        Assert.Equal("Dark", Settings.Peek(path).Theme);
        Assert.Equal("not json", File.ReadAllText(path));

        var loaded = Settings.Load(Path.Combine(_dir, "other.json"));
        Assert.True(File.Exists(Path.Combine(_dir, "other.json")));
        Assert.Equal(Settings.DefaultCodexFlags, loaded.CodexFlags);
    }

    [Fact]
    public void Discover_without_prune_leaves_dead_records_and_with_prune_removes_them()
    {
        string hosts = Path.Combine(_dir, "hosts");
        Directory.CreateDirectory(hosts);
        string dead = Path.Combine(hosts, "dead.json");
        File.WriteAllText(dead, System.Text.Json.JsonSerializer.Serialize(new Host.HostRecord { Id = "dead", HostPid = 1, HostStartTicks = 1, StartedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }));
        Assert.Empty(HostManager.Discover(hosts, prune: false));
        Assert.True(File.Exists(dead));
        Assert.Empty(HostManager.Discover(hosts, prune: true));
        Assert.False(File.Exists(dead));
    }
}
