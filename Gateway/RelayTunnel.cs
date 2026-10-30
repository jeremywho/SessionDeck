using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SessionDeck.Host;

namespace SessionDeck.Gateway;

/// <summary>What the relay looks like from here: <c>connecting</c>, <c>connected</c> (the public URL answered) or <c>down</c> with the reason. Written to <c>relay.json</c>.</summary>
internal sealed record RelayStatus(string State, string? Reason, DateTime? ConnectedSinceUtc, DateTime? LastProbeUtc);

/// <summary>The ssh child as the tunnel sees it; a fake stands in for it in tests.</summary>
internal interface IRelayChild
{
    Task<int> Exited { get; }
    string StderrTail { get; }
    void Kill();
}

internal enum RelayExit { Retry, Terminal }

/// <summary>
/// Keeps one <c>ssh -N -R</c> to the VPS alive so the gateway's port appears on the server's
/// loopback for Caddy to front. Network trouble is retried with a growing pause; a refused key or a
/// changed host key is final, because retrying those would only feed fail2ban on the server. Whether
/// the path works is measured by asking the public URL, not by the child being alive.
/// </summary>
internal sealed class RelayTunnel : IAsyncDisposable
{
    public const string KeyFileName = "relay_ed25519";
    public const string KnownHostsFileName = "relay_known_hosts";
    static readonly TimeSpan LongLived = TimeSpan.FromSeconds(60);
    static readonly TimeSpan FirstProbe = TimeSpan.FromSeconds(2);

    readonly RelaySettings _settings;
    readonly int _gatewayPort;
    readonly string _dataDir;
    readonly Func<IReadOnlyList<string>, IRelayChild> _start;
    readonly Func<CancellationToken, Task<string?>> _probe;
    readonly Action<RelayStatus> _publish;
    readonly Func<int, TimeSpan> _backoff;
    readonly TimeSpan _probeEvery;
    IRelayChild? _child;

    public RelayTunnel(RelaySettings settings, int gatewayPort, string dataDir,
                       Func<IReadOnlyList<string>, IRelayChild> start,
                       Func<CancellationToken, Task<string?>> probe,
                       Action<RelayStatus> publish,
                       Func<int, TimeSpan>? backoff = null, TimeSpan? probeEvery = null)
    {
        _settings = settings;
        _gatewayPort = gatewayPort;
        _dataDir = dataDir;
        _start = start;
        _probe = probe;
        _publish = publish;
        _backoff = backoff ?? Backoff;
        _probeEvery = probeEvery ?? TimeSpan.FromSeconds(30);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(_dataDir, KeyFileName))) { Publish("down", "no key file"); return; }
        int attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Publish("connecting", null);
                var startedAt = DateTime.UtcNow;
                IRelayChild child;
                try { child = _start(Arguments(_settings, _gatewayPort, _dataDir)); }
                catch (Exception ex)
                {
                    Publish("down", ex.Message);
                    await Task.Delay(_backoff(attempt++), ct);
                    continue;
                }
                _child = child;
                await WatchUntilExit(child, ct);
                _child = null;

                string tail = child.StderrTail.Trim();
                string reason = tail.Length > 0 ? LastLine(tail) : "tunnel exited";
                if (Classify(tail) == RelayExit.Terminal) { Publish("down", reason); return; }
                if (DateTime.UtcNow - startedAt >= LongLived) attempt = 0;
                Publish("down", reason);
                await Task.Delay(_backoff(attempt++), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _child?.Kill();
            _child = null;
            if (ct.IsCancellationRequested) Publish("down", "stopped");
        }
    }

    async Task WatchUntilExit(IRelayChild child, CancellationToken ct)
    {
        DateTime? since = null;
        var wait = _probeEvery < FirstProbe ? _probeEvery : FirstProbe;
        while (true)
        {
            var done = await Task.WhenAny(child.Exited, Task.Delay(wait, ct));
            ct.ThrowIfCancellationRequested();
            if (done == child.Exited) return;
            wait = _probeEvery;
            string? trouble;
            try { trouble = await _probe(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { trouble = ex.Message; }
            if (child.Exited.IsCompleted) return;
            if (trouble == null)
            {
                since ??= DateTime.UtcNow;
                Publish("connected", null, since, DateTime.UtcNow);
            }
            else
            {
                since = null;
                Publish("down", trouble, null, DateTime.UtcNow);
            }
        }
    }

    void Publish(string state, string? reason, DateTime? since = null, DateTime? probed = null) =>
        _publish(new RelayStatus(state, reason, since, probed));

    static string LastLine(string tail)
    {
        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : tail;
    }

    public ValueTask DisposeAsync()
    {
        _child?.Kill();
        _child = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>The ssh command line, exactly as the design fixes it; nothing of the user's own ssh configuration or agent takes part.</summary>
    public static IReadOnlyList<string> Arguments(RelaySettings s, int gatewayPort, string dataDir) => new[]
    {
        "-F", "none", "-N", "-T",
        "-o", "BatchMode=yes",
        "-o", "IdentitiesOnly=yes",
        "-o", "ExitOnForwardFailure=yes",
        "-o", "ServerAliveInterval=20",
        "-o", "ServerAliveCountMax=3",
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "UserKnownHostsFile=" + Path.Combine(dataDir, KnownHostsFileName),
        "-i", Path.Combine(dataDir, KeyFileName),
        "-p", s.SshPort.ToString(),
        "-R", $"127.0.0.1:{s.RemotePort}:127.0.0.1:{gatewayPort}",
        $"{s.SshUser}@{s.SshHost}",
    };

    /// <summary>A refused key, a changed host key or a missing identity file will not get better by trying again.</summary>
    public static RelayExit Classify(string stderrTail)
    {
        if (stderrTail.Contains("Permission denied", StringComparison.Ordinal)) return RelayExit.Terminal;
        if (stderrTail.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal)) return RelayExit.Terminal;
        if (stderrTail.Contains("Host key verification failed", StringComparison.Ordinal)) return RelayExit.Terminal;
        if (stderrTail.Contains("No such file or directory", StringComparison.Ordinal)) return RelayExit.Terminal;
        return RelayExit.Retry;
    }

    /// <summary>2 s, 4 s, 8 s … capped at a minute.</summary>
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(Math.Max(attempt, 0), 10))));

    public static string SshExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");

    public static string StatusPath(string dataDir) => Path.Combine(dataDir, "relay.json");

    /// <summary>A plain shared read: this file is rewritten every half minute by the gateway, so a reader that hits the swap must come back empty, never set the file aside as corrupt.</summary>
    public static RelayStatus? ReadStatus(string dataDir)
    {
        try
        {
            using var fs = new FileStream(StatusPath(dataDir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<RelayStatus>(fs);
        }
        catch { return null; }
    }

    public static void WriteStatus(string dataDir, RelayStatus status) =>
        AtomicFile.Write(StatusPath(dataDir), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));

    static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>GET <c>&lt;publicUrl&gt;/health</c> with the relay key: null when it answered 204, otherwise what went wrong.</summary>
    public static Func<CancellationToken, Task<string?>> HttpProbe(string publicUrl, string key)
    {
        if (!Uri.TryCreate(publicUrl.Trim().TrimEnd('/') + "/health", UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return _ => Task.FromResult<string?>($"relay URL is not an http(s) address: {publicUrl}");
        return async ct =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                using var res = await ProbeClient.SendAsync(req, ct);
                return res.StatusCode == System.Net.HttpStatusCode.NoContent ? null : $"{(int)res.StatusCode} from {uri.Host}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return ex.InnerException?.Message ?? ex.Message; }
        };
    }

    /// <summary>The real child: Windows' bundled ssh, stderr kept (last lines) for the reason when it exits, in a kill-on-close job.</summary>
    public static IRelayChild StartSsh(IReadOnlyList<string> args) => new SshChild(args);

    sealed class SshChild : IRelayChild
    {
        readonly Process _process;
        readonly Queue<string> _stderr = new();
        readonly object _gate = new();
        IntPtr _job;

        public SshChild(IReadOnlyList<string> args)
        {
            var psi = new ProcessStartInfo(SshExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("ssh did not start");
            _job = ChildJob.CreateKillOnClose();
            if (_job != IntPtr.Zero && !ChildJob.Assign(_job, _process.Handle)) { ChildJob.Close(_job); _job = IntPtr.Zero; }
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_gate) { _stderr.Enqueue(e.Data); while (_stderr.Count > 20) _stderr.Dequeue(); }
            };
            _process.BeginErrorReadLine();
            Exited = WaitAsync();
        }

        async Task<int> WaitAsync()
        {
            await _process.WaitForExitAsync();
            _process.WaitForExit();
            ReleaseJob();
            return _process.ExitCode;
        }

        public Task<int> Exited { get; }

        public string StderrTail { get { lock (_gate) return string.Join('\n', _stderr); } }

        public void Kill()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            ReleaseJob();
        }

        /// <summary>Exactly one of the exit path and Kill closes the job handle.</summary>
        void ReleaseJob()
        {
            var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
            if (job != IntPtr.Zero) ChildJob.Close(job);
        }
    }
}
