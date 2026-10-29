using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using SessionDeck.Gateway;
using SessionDeck.Host;

namespace SessionDeck.Tests;

/// <summary>A stand-in for a host's attach server: records what it is asked, sends what the test enqueues.</summary>
internal sealed class FakeHost : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly TaskCompletionSource<string> _target = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _clientClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    WebSocket? _ws;

    public FakeHost(int protocol = 4)
    {
        _listener.Start();
        Record = new HostRecord
        {
            Id = "fakehost0001", SessionId = "fake-session", Provider = "Claude", Protocol = protocol,
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port, Token = "FAKETOKEN", HostPid = Environment.ProcessId,
        };
        _ = Task.Run(Accept);
    }

    public HostRecord Record { get; }
    public ConcurrentQueue<string> Received { get; } = new();
    public Task<string> Target => _target.Task;
    public Task ClientClosed => _clientClosed.Task;
    public Task Connected => _connected.Task;
    public List<(bool Text, byte[] Data)> Script { get; } = new() { (true, Encoding.UTF8.GetBytes(HostProgram.SnapshotJson(0, 0, "fake", 120, 30))) };

    async Task Accept()
    {
        try
        {
            using var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
            var stream = tcp.GetStream();
            var r = await MiniHttp.ReadRequest(stream);
            if (r == null) return;
            _target.TrySetResult(r.Target);
            if (!r.Query.TryGetValue("token", out var t) || t != Record.Token) { await MiniHttp.WriteStatus(stream, 403, "Forbidden"); return; }
            await MiniHttp.WriteUpgrade(stream, r.WebSocketKey);
            _ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            _connected.TrySetResult();
            foreach (var (text, data) in Script)
                await _ws.SendAsync(data, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, _stop.Token);
            var buf = new byte[16384];
            while (_ws.State == WebSocketState.Open)
            {
                var res = await _ws.ReceiveAsync(buf, _stop.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;
                Received.Enqueue(Encoding.UTF8.GetString(buf, 0, res.Count));
            }
            _clientClosed.TrySetResult();
        }
        catch (Exception) { _clientClosed.TrySetResult(); }
    }

    public async Task SendAsync(string text) =>
        await _ws!.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    public async Task CloseAsync()
    {
        if (_ws is { State: WebSocketState.Open }) await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fake host closing", CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        _ws?.Dispose();
        await Task.CompletedTask;
    }
}
