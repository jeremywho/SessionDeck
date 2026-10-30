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

public class GatewayServerTests : IAsyncDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-gw-" + Guid.NewGuid().ToString("N"));
    readonly DeviceStore _devices;
    readonly GatewayServer _server;
    readonly Dictionary<string, HostRecord> _hosts = new();
    List<CatalogEntry> _catalog = new() { new("h1", "s1", "Claude", "alpha", "Opus 5", "", "Working", 1, 0, "", 0, 0, @"C:\x", false, true) };

    public GatewayServerTests()
    {
        Directory.CreateDirectory(_dir);
        _devices = new DeviceStore(Path.Combine(_dir, "devices.json"));
        _server = new GatewayServer(IPAddress.Loopback, 0, _devices, () => _catalog, id => _hosts.GetValueOrDefault(id),
            name => typeof(GatewayServer).Assembly.GetManifestResourceStream("www/" + name), "ADMINSECRET");
        _server.StartAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    string Base => $"http://127.0.0.1:{_server.Port}";
    Uri Ws(string pathAndQuery) => new($"ws://127.0.0.1:{_server.Port}{pathAndQuery}");

    HttpClient Client(string? token = null)
    {
        var c = new HttpClient { BaseAddress = new Uri(Base), Timeout = TimeSpan.FromSeconds(10) };
        if (token != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    async Task<string> PairAsync()
    {
        string code = _devices.IssuePairingCode(DateTime.UtcNow);
        using var c = Client();
        var res = await c.PostAsync("/pair", new StringContent(JsonSerializer.Serialize(new { code, deviceName = "Test phone" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

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
    public async Task Pairing_then_listing_then_live_events()
    {
        string token = await PairAsync();
        using var c = Client(token);
        var list = await c.GetStringAsync("/sessions");
        Assert.Contains("\"hostId\":\"h1\"", list);
        Assert.DoesNotContain("token", list, StringComparison.OrdinalIgnoreCase);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(Ws($"/events?token={token}"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var first = JsonDocument.Parse(await NextText(ws)).RootElement;
        Assert.Equal("sessions", first.GetProperty("type").GetString());
        Assert.Equal("alpha", first.GetProperty("sessions")[0].GetProperty("name").GetString());
        Assert.Equal(1, _server.Subscribers);

        _catalog = new() { _catalog[0] with { Name = "beta", State = "Completed" } };
        _server.CatalogChanged();
        var second = JsonDocument.Parse(await NextText(ws)).RootElement;
        Assert.Equal("beta", second.GetProperty("sessions")[0].GetProperty("name").GetString());
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task A_bad_pairing_code_is_refused()
    {
        using var c = Client();
        var res = await c.PostAsync("/pair", new StringContent("{\"code\":\"NOPE1234\",\"deviceName\":\"x\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var garbage = await c.PostAsync("/pair", new StringContent("not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, garbage.StatusCode);
    }

    [Fact]
    public async Task Unknown_tokens_are_refused_everywhere_and_revocation_cuts_live_sockets()
    {
        using (var anon = Client())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/sessions")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/devices")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/mobile/xterm.js")).StatusCode);
        }
        using (var wrong = Client("nope"))
            Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/sessions")).StatusCode);

        using (var bad = new ClientWebSocket())
            await Assert.ThrowsAsync<WebSocketException>(() => bad.ConnectAsync(Ws("/events?token=nope"), CancellationToken.None));

        string token = await PairAsync();
        using (var evil = new ClientWebSocket())
        {
            evil.Options.SetRequestHeader("Origin", "http://evil.example");
            await Assert.ThrowsAsync<WebSocketException>(() => evil.ConnectAsync(Ws($"/events?token={token}"), CancellationToken.None));
        }
        using (var own = new ClientWebSocket())
        {
            own.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{_server.Port}");
            await own.ConnectAsync(Ws($"/events?token={token}"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("sessions", await NextText(own));
            await own.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }

        using var live = new ClientWebSocket();
        await live.ConnectAsync(Ws($"/events?token={token}"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await NextText(live);
        string deviceId = _devices.Devices.Single().Id;
        using (var c = Client(token))
            Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/devices/{deviceId}")).StatusCode);
        var closed = await NextText(live).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("<close 4401>", closed);
        using (var after = Client(token))
            Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/sessions")).StatusCode);
    }

    [Fact]
    public async Task A_device_revoked_while_its_socket_is_still_being_set_up_is_cut_and_types_nothing()
    {
        await using var host = new FakeHost();
        _hosts[host.Record.Id] = host.Record;
        string token = await PairAsync();
        string deviceId = _devices.Devices.Single().Id;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.BeforeRegisterForTests = async () => { paused.TrySetResult(); await resume.Task; };

        using var ws = new ClientWebSocket();
        var connecting = ws.ConnectAsync(Ws($"/sessions/{host.Record.Id}/attach?token={token}"), CancellationToken.None);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_server.Revoke(deviceId));
        resume.TrySetResult();
        await connecting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("<close 4401>", await NextText(ws));
        try { await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"input\",\"data\":\"typed\"}"), WebSocketMessageType.Text, true, CancellationToken.None); } catch (WebSocketException) { }
        await Task.Delay(300);
        Assert.Empty(host.Received);
        _server.BeforeRegisterForTests = null;
    }

    [Fact]
    public async Task Static_files_come_from_the_embedded_site_and_stay_inside_it()
    {
        using var c = Client();
        var js = await c.GetAsync("/mobile/xterm.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("text/javascript", js.Content.Headers.ContentType!.MediaType);
        Assert.True((await js.Content.ReadAsByteArrayAsync()).Length > 1000);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/mobile/missing.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/mobile/..%2Fdeck.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/mobile/a%5Cb.js")).StatusCode);
    }

    [Fact]
    public async Task Attach_refuses_unknown_exited_and_protocol_3_hosts_before_the_upgrade()
    {
        string token = await PairAsync();
        _hosts["old0"] = new HostRecord { Id = "old0", Protocol = 3, Port = 1, Token = "x" };
        _hosts["gone"] = new HostRecord { Id = "gone", Protocol = 4, Port = 1, Token = "x", ExitCode = 0, ExitedAt = DateTime.UtcNow };
        foreach (var id in new[] { "nope", "gone", "old0" })
        {
            using var ws = new ClientWebSocket();
            await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(Ws($"/sessions/{id}/attach?token={token}"), CancellationToken.None));
        }
        using var raw = new HttpClient { BaseAddress = new Uri(Base) };
        var req = new HttpRequestMessage(HttpMethod.Get, $"/sessions/old0/attach?token={token}");
        req.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        var res = await raw.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("restart from the desktop", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Attach_relays_a_fake_host_to_the_phone()
    {
        await using var host = new FakeHost();
        _hosts[host.Record.Id] = host.Record;
        string token = await PairAsync();
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(Ws($"/sessions/{host.Record.Id}/attach?token={token}&after=7"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("after=7", await host.Target.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("\"type\":\"snapshot\"", await NextText(ws));
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"input\",\"data\":\"hi\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Received.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal("{\"type\":\"input\",\"data\":\"hi\"}", host.Received.Single());
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Admin_routes_need_the_admin_token()
    {
        using var anon = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/admin/pair-code", new StringContent(""))).StatusCode);
        var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.QuitRequested += () => quit.TrySetResult();
        using var admin = Client();
        admin.DefaultRequestHeaders.Add("X-Admin-Token", "ADMINSECRET");
        var res = await admin.PostAsync("/admin/pair-code", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        string code = doc.RootElement.GetProperty("code").GetString()!;
        Assert.NotNull(_devices.Redeem(code, "phone", DateTime.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/admin/quit", new StringContent(""))).StatusCode);
        await quit.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_qr_payload_carries_address_port_and_code()
    {
        Assert.Equal("sessiondeck://pair?host=192.0.2.10&port=7420&code=ABCD2345", QrPayload.For("192.0.2.10", 7420, "ABCD2345"));
        string lan = QrPayload.LanAddress();
        Assert.True(lan.Length == 0 || System.Net.IPAddress.TryParse(lan, out _));
    }

    [Fact]
    public async Task The_loop_pushes_only_changes_and_slows_down_with_no_subscribers()
    {
        var builds = new Queue<List<CatalogEntry>>(new[]
        {
            new List<CatalogEntry> { _catalog[0] },
            new List<CatalogEntry> { _catalog[0] },
            new List<CatalogEntry> { _catalog[0] with { Name = "beta" } },
            new List<CatalogEntry> { _catalog[0] with { Name = "beta" } },
        });
        var waits = new List<TimeSpan>();
        int pushes = 0, subscribers = 1;
        using var cts = new CancellationTokenSource();
        var busy = TimeSpan.FromSeconds(5);
        var idle = TimeSpan.FromSeconds(30);
        var loop = new CatalogLoop(
            () => builds.Count > 0 ? builds.Dequeue() : new List<CatalogEntry> { _catalog[0] with { Name = "beta" } },
            _ => pushes++, () => subscribers, busy, idle,
            (wait, _) => { waits.Add(wait); if (waits.Count == 2) subscribers = 0; if (waits.Count == 4) cts.Cancel(); return Task.CompletedTask; });
        await loop.RunAsync(cts.Token);
        Assert.Equal(2, pushes);
        Assert.Equal(new[] { busy, busy, idle, idle }, waits);
        Assert.Equal("beta", loop.Current.Single().Name);
    }
}
