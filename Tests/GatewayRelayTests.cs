using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SessionDeck.Gateway;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>The public path: one baked-in relay key as the credential, and no pairing or admin for requests that came through the proxy.</summary>
public class GatewayRelayTests : IAsyncDisposable
{
    const string RelayKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-gwr-" + Guid.NewGuid().ToString("N"));
    readonly DeviceStore _devices;
    readonly GatewayServer _server;
    readonly List<CatalogEntry> _catalog = new() { new("h1", "s1", "Claude", "alpha", "Opus 5", "", "Working", 1, 0, "", 0, 0, @"C:\x", false, true) };

    public GatewayRelayTests()
    {
        Directory.CreateDirectory(_dir);
        _devices = new DeviceStore(Path.Combine(_dir, "devices.json"));
        _server = Server(_devices, RelayKey);
    }

    GatewayServer Server(DeviceStore devices, string relayKey)
    {
        var s = new GatewayServer(IPAddress.Loopback, 0, devices, () => _catalog, _ => null,
            name => typeof(GatewayServer).Assembly.GetManifestResourceStream("www/" + name), "ADMINSECRET", relayKey);
        s.StartAsync().GetAwaiter().GetResult();
        return s;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    static HttpClient Client(GatewayServer server, string? token = null)
    {
        var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"), Timeout = TimeSpan.FromSeconds(10) };
        if (token != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    Uri Ws(string pathAndQuery) => new($"ws://127.0.0.1:{_server.Port}{pathAndQuery}");

    static async Task<string> NextText(ClientWebSocket ws)
    {
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        if (r.MessageType == WebSocketMessageType.Close) return "<close " + (int?)r.CloseStatus + ">";
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task Relay_key_lists_sessions_and_opens_sockets_as_the_relay_device()
    {
        using var c = Client(_server, RelayKey);
        var list = await c.GetStringAsync("/sessions");
        Assert.Contains("\"hostId\":\"h1\"", list);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(Ws($"/events?token={RelayKey}"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var first = JsonDocument.Parse(await NextText(ws)).RootElement;
        Assert.Equal("sessions", first.GetProperty("type").GetString());
        Assert.Empty(_devices.Devices);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Relay_key_is_matched_exactly_after_trimming()
    {
        using (var padded = Client(_server))
        {
            padded.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer  " + RelayKey + " ");
            Assert.Equal(HttpStatusCode.OK, (await padded.GetAsync("/sessions")).StatusCode);
        }
        using (var upper = Client(_server, RelayKey.ToUpperInvariant()))
            Assert.Equal(HttpStatusCode.Unauthorized, (await upper.GetAsync("/sessions")).StatusCode);

        await using var noKey = Server(new DeviceStore(Path.Combine(_dir, "devices2.json")), "");
        using (var empty = Client(noKey))
        {
            empty.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");
            Assert.Equal(HttpStatusCode.Unauthorized, (await empty.GetAsync("/sessions")).StatusCode);
        }
        using (var anyKey = Client(noKey, RelayKey))
            Assert.Equal(HttpStatusCode.Unauthorized, (await anyKey.GetAsync("/sessions")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_requests_cannot_pair_or_reach_admin()
    {
        string code = _devices.IssuePairingCode(DateTime.UtcNow);
        var body = () => new StringContent(JsonSerializer.Serialize(new { code, deviceName = "Far phone" }), Encoding.UTF8, "application/json");
        using (var remote = Client(_server))
        {
            remote.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
            Assert.Equal(HttpStatusCode.NotFound, (await remote.PostAsync("/pair", body())).StatusCode);
            remote.DefaultRequestHeaders.Add("X-Admin-Token", "ADMINSECRET");
            Assert.Equal(HttpStatusCode.NotFound, (await remote.PostAsync("/admin/pair-code", new StringContent(""))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await remote.PostAsync("/admin/quit", new StringContent(""))).StatusCode);
        }
        using (var lan = Client(_server))
            Assert.Equal(HttpStatusCode.OK, (await lan.PostAsync("/pair", body())).StatusCode);
    }

    [Fact]
    public async Task Health_is_open_and_says_nothing()
    {
        using var c = Client(_server);
        var res = await c.GetAsync("/health");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("", await res.Content.ReadAsStringAsync());
        c.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
        Assert.Equal(HttpStatusCode.NoContent, (await c.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Origin_follows_the_forwarded_scheme()
    {
        using (var secure = new ClientWebSocket())
        {
            secure.Options.SetRequestHeader("Origin", $"https://127.0.0.1:{_server.Port}");
            secure.Options.SetRequestHeader("X-Forwarded-Proto", "https");
            await secure.ConnectAsync(Ws($"/events?token={RelayKey}"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("sessions", await NextText(secure));
            await secure.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        using (var mixed = new ClientWebSocket())
        {
            mixed.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{_server.Port}");
            mixed.Options.SetRequestHeader("X-Forwarded-Proto", "https");
            await Assert.ThrowsAsync<WebSocketException>(() => mixed.ConnectAsync(Ws($"/events?token={RelayKey}"), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Revoking_the_relay_device_is_not_a_thing()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(Ws($"/events?token={RelayKey}"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("sessions", await NextText(ws));

        using var admin = Client(_server);
        admin.DefaultRequestHeaders.Add("X-Admin-Token", "ADMINSECRET");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/devices/relay")).StatusCode);

        await Task.Delay(500);
        Assert.Equal(WebSocketState.Open, ws.State);
        _server.CatalogChanged();
        Assert.Contains("sessions", await NextText(ws));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }
}
