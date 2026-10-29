using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using SessionDeck.Gateway;
using Xunit;

namespace SessionDeck.Tests;

public class AttachRelayTests
{
    /// <summary>A phone-side socket pair: the server end goes to the relay, the client end is the test's phone.</summary>
    static async Task<(WebSocket Server, ClientWebSocket Phone, TcpListener Listener)> PhonePair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var phone = new ClientWebSocket();
        var connect = phone.ConnectAsync(new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"), CancellationToken.None);
        var tcp = await listener.AcceptTcpClientAsync();
        Keep.Add(tcp);
        var stream = tcp.GetStream();
        var r = (await MiniHttp.ReadRequest(stream))!;
        await MiniHttp.WriteUpgrade(stream, r.WebSocketKey);
        var server = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        await connect;
        return (server, phone, listener);
    }

    static readonly List<TcpClient> Keep = new();

    static Task Send(WebSocket ws, string text) => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<(WebSocketMessageType Type, byte[] Data)> Receive(WebSocket ws)
    {
        var buf = new byte[65536];
        var r = await ws.ReceiveAsync(buf, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        return (r.MessageType, buf[..r.Count]);
    }

    [Fact]
    public async Task Only_input_reaches_the_host()
    {
        await using var host = new FakeHost();
        var (server, phone, listener) = await PhonePair();
        using var cts = new CancellationTokenSource();
        var relay = AttachRelay.RunAsync(server, host.Record, 0, cts.Token);
        await host.Connected.WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = await Receive(phone);
        Assert.Equal(WebSocketMessageType.Text, snapshot.Type);

        await Send(phone, "{\"type\":\"resize\",\"cols\":10,\"rows\":5}");
        await Send(phone, "{\"type\":\"kill\"}");
        await Send(phone, "not json");
        await phone.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        await Send(phone, "{\"type\":\"input\",\"data\":\"ls\\r\"}");
        await Send(phone, "{\"type\":\"input\"}");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Received.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(300);
        Assert.Equal(new[] { "{\"type\":\"input\",\"data\":\"ls\\r\"}" }, host.Received.ToArray());

        await phone.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
    }

    [Fact]
    public async Task The_after_sequence_is_passed_to_the_host_and_frames_come_back_in_order()
    {
        await using var host = new FakeHost();
        host.Script.Add((false, new byte[] { 65, 66 }));
        host.Script.Add((false, new byte[] { 67 }));
        var (server, phone, listener) = await PhonePair();
        using var cts = new CancellationTokenSource();
        var relay = AttachRelay.RunAsync(server, host.Record, 4096, cts.Token);

        string target = await host.Target.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("/attach?token=FAKETOKEN&after=4096", target);

        var first = await Receive(phone);
        Assert.Equal(WebSocketMessageType.Text, first.Type);
        Assert.Contains("\"cols\":120", Encoding.UTF8.GetString(first.Data));
        var second = await Receive(phone);
        Assert.Equal(WebSocketMessageType.Binary, second.Type);
        Assert.Equal(new byte[] { 65, 66 }, second.Data);
        var third = await Receive(phone);
        Assert.Equal(new byte[] { 67 }, third.Data);

        cts.Cancel();
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
    }

    [Fact]
    public async Task Closing_either_side_closes_the_other()
    {
        await using (var host = new FakeHost())
        {
            var (server, phone, listener) = await PhonePair();
            var relay = AttachRelay.RunAsync(server, host.Record, 0, CancellationToken.None);
            await host.Connected.WaitAsync(TimeSpan.FromSeconds(10));
            await phone.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            await host.ClientClosed.WaitAsync(TimeSpan.FromSeconds(5));
            await relay.WaitAsync(TimeSpan.FromSeconds(10));
            listener.Stop();
        }
        await using (var host = new FakeHost())
        {
            var (server, phone, listener) = await PhonePair();
            var relay = AttachRelay.RunAsync(server, host.Record, 0, CancellationToken.None);
            await host.Connected.WaitAsync(TimeSpan.FromSeconds(10));
            await Receive(phone);
            await host.CloseAsync();
            var closing = await Receive(phone);
            Assert.Equal(WebSocketMessageType.Close, closing.Type);
            await relay.WaitAsync(TimeSpan.FromSeconds(10));
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("{\"type\":\"input\",\"data\":\"x\"}", true)]
    [InlineData("{\"type\":\"input\",\"data\":\"\"}", true)]
    [InlineData("{\"type\":\"input\"}", false)]
    [InlineData("{\"type\":\"resize\",\"cols\":1,\"rows\":1}", false)]
    [InlineData("{\"type\":\"kill\"}", false)]
    [InlineData("[]", false)]
    [InlineData("nope", false)]
    public void The_input_filter_lets_only_well_formed_input_through(string message, bool allowed) =>
        Assert.Equal(allowed, AttachRelay.AllowedTowardHost(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text));
}
