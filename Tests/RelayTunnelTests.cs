using System.IO;
using SessionDeck.Gateway;
using Xunit;

namespace SessionDeck.Tests;

public class RelayTunnelTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-relay-" + Guid.NewGuid().ToString("N"));
    readonly List<RelayStatus> _statuses = new();
    readonly List<IReadOnlyList<string>> _starts = new();

    public RelayTunnelTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, RelayTunnel.KeyFileName), "");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    sealed class FakeChild : IRelayChild
    {
        readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeChild(string stderr = "", bool exitNow = false) { StderrTail = stderr; if (exitNow) _exit.SetResult(255); }
        public Task<int> Exited => _exit.Task;
        public string StderrTail { get; }
        public bool Killed { get; private set; }
        public void Exit(int code) => _exit.TrySetResult(code);
        public void Kill() { Killed = true; _exit.TrySetResult(-1); }
    }

    RelayTunnel Tunnel(Func<IReadOnlyList<string>, IRelayChild> start, Func<CancellationToken, Task<string?>>? probe = null,
                       Func<int, TimeSpan>? backoff = null, TimeSpan? probeEvery = null) =>
        new(new RelaySettings(), 7420, _dir,
            args => { _starts.Add(args); return start(args); },
            probe ?? (_ => Task.FromResult<string?>(null)),
            s => { lock (_statuses) _statuses.Add(s); },
            backoff, probeEvery);

    RelayStatus Last { get { lock (_statuses) return _statuses[^1]; } }

    async Task Until(Func<bool> condition, int ms = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(condition(), "condition not met in time");
    }

    [Fact]
    public void Arguments_match_the_spec()
    {
        var args = RelayTunnel.Arguments(new RelaySettings(), 7420, @"C:\d");
        Assert.Equal(new[]
        {
            "-F", "none", "-N", "-T", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=20", "-o", "ServerAliveCountMax=3", "-o", "StrictHostKeyChecking=accept-new",
            "-o", @"UserKnownHostsFile=C:\d\relay_known_hosts", "-i", @"C:\d\relay_ed25519", "-p", "2222",
            "-R", "127.0.0.1:8103:127.0.0.1:7420", "sdtunnel@0a.gg",
        }, args);
    }

    [Fact]
    public void Backoff_doubles_from_two_seconds_to_a_minute()
    {
        Assert.Equal(new[] { 2, 4, 8, 16, 32, 60, 60 }, Enumerable.Range(0, 7).Select(a => (int)RelayTunnel.Backoff(a).TotalSeconds));
    }

    [Fact]
    public void Permission_denied_and_host_key_changes_are_terminal()
    {
        Assert.Equal(RelayExit.Terminal, RelayTunnel.Classify("sdtunnel@0a.gg: Permission denied (publickey)."));
        Assert.Equal(RelayExit.Terminal, RelayTunnel.Classify("@@@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!"));
        Assert.Equal(RelayExit.Terminal, RelayTunnel.Classify("Host key verification failed."));
        Assert.Equal(RelayExit.Retry, RelayTunnel.Classify("ssh: connect to host 0a.gg port 2222: Connection timed out"));
        Assert.Equal(RelayExit.Retry, RelayTunnel.Classify("Error: remote port forwarding failed for listen port 8103"));
        Assert.Equal(RelayExit.Retry, RelayTunnel.Classify(""));
    }

    [Fact]
    public async Task Instant_exits_back_off_to_the_cap()
    {
        var attempts = new List<int>();
        using var cts = new CancellationTokenSource();
        var tunnel = Tunnel(_ => new FakeChild("ssh: connect to host 0a.gg port 2222: Connection timed out", exitNow: true),
            backoff: a => { lock (attempts) { attempts.Add(a); if (attempts.Count == 6) cts.Cancel(); } return TimeSpan.FromMilliseconds(1); });
        await tunnel.RunAsync(cts.Token);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, attempts);
        Assert.Equal(6, _starts.Count);
        Assert.Equal(new[] { "connecting", "down" }, _statuses.Take(2).Select(s => s.State));
        Assert.Contains("Connection timed out", _statuses[1].Reason);
    }

    [Fact]
    public async Task A_terminal_exit_stops_the_loop()
    {
        var tunnel = Tunnel(_ => new FakeChild("sdtunnel@0a.gg: Permission denied (publickey).", exitNow: true));
        await tunnel.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(_starts);
        Assert.Equal("down", Last.State);
        Assert.Contains("Permission denied", Last.Reason);
    }

    [Fact]
    public async Task A_failed_probe_reports_down_without_restarting_the_tunnel()
    {
        string? trouble = "503 from caddy";
        var child = new FakeChild();
        using var cts = new CancellationTokenSource();
        var tunnel = Tunnel(_ => child, probe: _ => Task.FromResult<string?>(Volatile.Read(ref trouble)), probeEvery: TimeSpan.FromMilliseconds(10));
        var run = tunnel.RunAsync(cts.Token);
        await Until(() => Last.State == "down" && Last.Reason == "503 from caddy");
        Assert.Single(_starts);
        Volatile.Write(ref trouble, null);
        await Until(() => Last.State == "connected");
        Assert.NotNull(Last.ConnectedSinceUtc);
        Assert.NotNull(Last.LastProbeUtc);
        Assert.Single(_starts);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_missing_key_file_is_terminal_before_any_child()
    {
        File.Delete(Path.Combine(_dir, RelayTunnel.KeyFileName));
        var tunnel = Tunnel(_ => new FakeChild());
        await tunnel.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(_starts);
        Assert.Equal(("down", "no key file"), (Last.State, Last.Reason));
    }

    [Fact]
    public async Task Cancelling_kills_the_child()
    {
        var child = new FakeChild();
        using var cts = new CancellationTokenSource();
        var tunnel = Tunnel(_ => child, probeEvery: TimeSpan.FromMilliseconds(10));
        var run = tunnel.RunAsync(cts.Token);
        await Until(() => Last.State == "connected");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(child.Killed);
        Assert.Equal(("down", "stopped"), (Last.State, Last.Reason));
    }

    [Fact]
    public async Task A_long_lived_child_resets_the_backoff()
    {
        var attempts = new List<int>();
        var children = new Queue<FakeChild>(new[] { new FakeChild("timed out", exitNow: true), new FakeChild("timed out", exitNow: true), new FakeChild() });
        using var cts = new CancellationTokenSource();
        var tunnel = Tunnel(_ => children.Dequeue(), backoff: a => { attempts.Add(a); return TimeSpan.FromMilliseconds(1); }, probeEvery: TimeSpan.FromMilliseconds(10));
        var run = tunnel.RunAsync(cts.Token);
        await Until(() => _starts.Count == 3);
        Assert.Equal(new[] { 0, 1 }, attempts);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Status_round_trips_through_relay_json()
    {
        var status = new RelayStatus("connected", null, new DateTime(2026, 10, 30, 21, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 30, 21, 5, 0, DateTimeKind.Utc));
        RelayTunnel.WriteStatus(_dir, status);
        Assert.Equal(status, RelayTunnel.ReadStatus(_dir));
        Assert.Null(RelayTunnel.ReadStatus(Path.Combine(_dir, "nowhere")));
    }
}
