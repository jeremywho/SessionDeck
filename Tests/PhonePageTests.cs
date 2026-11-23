using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SessionDeck.Gateway;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>The one page of the deck's machine a paired phone may read through the gateway, against a made-up page.</summary>
public class PhonePageTests : IAsyncDisposable
{
    /// <summary>A page server on loopback that answers what it is told to and remembers what it was asked.</summary>
    sealed class FakePage : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public readonly List<string> Asked = new();
        public readonly Dictionary<string, (int Status, string Type, byte[] Body, string Extra)> Answers = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakePage()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var head = new StringBuilder();
                            var one = new byte[1];
                            while (!head.ToString().EndsWith("\r\n\r\n") && await stream.ReadAsync(one) == 1) head.Append((char)one[0]);
                            string[] first = head.ToString().Split("\r\n")[0].Split(' ');
                            if (first.Length < 2) return;
                            lock (Asked) Asked.Add(first[0] + " " + first[1]);
                            var (status, type, body, extra) = Answers.TryGetValue(first[1], out var a) ? a : (404, "text/plain", Encoding.UTF8.GetBytes("no"), "");
                            // "unsized" leaves the length out: the body is whatever comes before the connection closes.
                            string length = extra == "unsized" ? "" : $"Content-Length: {body.Length}\r\n{extra}";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\n{length}Connection: close\r\n\r\n"));
                            await stream.WriteAsync(body);
                        }
                    });
                }
            });
        }

        public void Say(string path, string type, string body, int status = 200, string extra = "") => Answers[path] = (status, type, Encoding.UTF8.GetBytes(body), extra);
        public void Dispose() => _listener.Stop();
    }

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-page-" + Guid.NewGuid().ToString("N"));
    readonly DeviceStore _devices;
    readonly GatewayServer _server;
    readonly FakePage _fake = new();

    public PhonePageTests()
    {
        Directory.CreateDirectory(_dir);
        _devices = new DeviceStore(Path.Combine(_dir, "devices.json"));
        _server = new GatewayServer(IPAddress.Loopback, 0, _devices, () => new List<CatalogEntry>(), _ => null,
            name => typeof(GatewayServer).Assembly.GetManifestResourceStream("www/" + name), "ADMINSECRET");
        _server.StartAsync().GetAwaiter().GetResult();
        _fake.Say("/", "text/html; charset=utf-8", "<h1>Usage</h1>");
        _fake.Say("/api/usage", "application/json", """{"rows":[{"name":"someone@example.com","pct":42}]}""");
        _fake.Say("/api/reset", "application/json", """{"spent":true}""");
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _fake.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    PhonePage Named(params string[] paths) =>
        PhonePage.Parse(JsonSerializer.Serialize(new { name = "Usage", url = $"http://127.0.0.1:{_fake.Port}/", paths }))!;

    HttpClient Client(string? token = null)
    {
        var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}"), Timeout = TimeSpan.FromSeconds(20) };
        if (token != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    async Task<string> PairAsync()
    {
        string code = _devices.IssuePairingCode(DateTime.UtcNow);
        using var c = Client();
        var res = await c.PostAsync("/pair", new StringContent(JsonSerializer.Serialize(new { code, deviceName = "Test phone" }), Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    // ---- what may be named ----

    [Fact]
    public void A_page_is_a_name_a_loopback_address_and_the_paths_it_fetches()
    {
        var page = PhonePage.Parse("""{ "name": " Usage ", "url": "http://127.0.0.1:9000/dash/", "paths": ["/api/usage", "/static/app.js"] }""")!;
        Assert.Equal(("Usage", "http://127.0.0.1:9000/dash/"), (page.Name, page.Page.ToString()));
        Assert.Equal(new[] { "/api/usage", "/static/app.js" }, page.Paths);
        // The page itself, and each listed path from the root of its server; nothing else.
        Assert.Equal("http://127.0.0.1:9000/dash/", page.Target("/page")!.ToString());
        Assert.Equal("http://127.0.0.1:9000/dash/", page.Target("/page/")!.ToString());
        Assert.Equal("http://127.0.0.1:9000/api/usage", page.Target("/page/api/usage")!.ToString());
        foreach (string other in new[] { "/page/api/login", "/page/api/usage/", "/page/api", "/page//api/usage", "/pages", "/page/API/usage", "/page/api/usage/../login", "/sessions" })
            Assert.Null(page.Target(other));
        Assert.Empty(PhonePage.Parse("""{ "name": "Usage", "url": "http://localhost:9000" }""")!.Paths);
        Assert.NotNull(PhonePage.Parse("""{ "name": "Usage", "url": "http://[::1]:9000/" }"""));
    }

    [Theory]
    [InlineData("""{ "name": "Usage", "url": "http://192.168.1.20:9000/" }""")]
    [InlineData("""{ "name": "Usage", "url": "http://example.com/" }""")]
    [InlineData("""{ "name": "Usage", "url": "https://127.0.0.1:9000/" }""")]
    [InlineData("""{ "name": "Usage", "url": "file:///C:/secrets.txt" }""")]
    [InlineData("""{ "name": "Usage", "url": "http://user:pw@127.0.0.1:9000/" }""")]
    [InlineData("""{ "name": "Usage", "url": "http://127.0.0.1:9000/?next=http://example.com" }""")]
    [InlineData("""{ "name": "Usage", "url": "127.0.0.1:9000" }""")]
    [InlineData("""{ "name": "", "url": "http://127.0.0.1:9000/" }""")]
    [InlineData("""{ "url": "http://127.0.0.1:9000/" }""")]
    [InlineData("""{ "name": "Usage" }""")]
    [InlineData("""{ "name": "Usage", "url": "http://127.0.0.1:9000/", "paths": ["api/usage"] }""")]
    [InlineData("""{ "name": "Usage", "url": "http://127.0.0.1:9000/", "paths": ["/api/../admin"] }""")]
    [InlineData("""{ "name": "Usage", "url": "http://127.0.0.1:9000/", "paths": ["/api/usage?all=1"] }""")]
    [InlineData("""{ "name": "Usage", "url": "http://127.0.0.1:9000/", "paths": ["//example.com/x"] }""")]
    [InlineData("""{ "name": "Usage", "url": "http://127.0.0.1:9000/", "paths": "/api/usage" }""")]
    [InlineData("""{ "name": "Usage", "url": "http://127.0.0.1:9000/", "paths": [7] }""")]
    [InlineData("""["http://127.0.0.1:9000/"]""")]
    [InlineData("not json")]
    public void What_does_not_name_a_page_on_this_machine_names_none(string json) => Assert.Null(PhonePage.Parse(json));

    [Fact]
    public void The_file_is_read_when_asked_and_no_file_is_no_page()
    {
        string file = Path.Combine(_dir, PhonePage.FileName);
        Assert.Null(PhonePage.Load(file));
        File.WriteAllText(file, """{ "name": "Usage", "url": "http://127.0.0.1:9000/" }""");
        Assert.Equal("Usage", PhonePage.Load(file)!.Name);
        File.WriteAllText(file, "{ half");
        Assert.Null(PhonePage.Load(file));
    }

    // ---- through the gateway ----

    [Fact]
    public async Task A_paired_phone_reads_the_page_and_what_it_fetches_and_nothing_else()
    {
        string token = await PairAsync();
        using var phone = Client(token);

        // No page named: the phone is told there is none, and nothing answers under /page.
        using (var meta = JsonDocument.Parse(await phone.GetStringAsync("/meta")))
            Assert.Equal(JsonValueKind.Null, meta.RootElement.GetProperty("page").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync("/page/")).StatusCode);

        _server.Page = () => Named("/api/usage");
        using (var meta = JsonDocument.Parse(await phone.GetStringAsync("/meta")))
            Assert.Equal("Usage", meta.RootElement.GetProperty("page").GetProperty("name").GetString());

        var html = await phone.GetAsync("/page/");
        Assert.Equal((HttpStatusCode.OK, "text/html", "<h1>Usage</h1>"), (html.StatusCode, html.Content.Headers.ContentType!.MediaType, await html.Content.ReadAsStringAsync()));
        var data = await phone.GetAsync("/page/api/usage");
        Assert.Equal((HttpStatusCode.OK, "application/json"), (data.StatusCode, data.Content.Headers.ContentType!.MediaType));
        Assert.Contains("someone@example.com", await data.Content.ReadAsStringAsync());

        // A path the page has but the deck did not list, and anything that is not a read.
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync("/page/api/reset")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await phone.PostAsync("/page/api/usage", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await phone.PostAsync("/page/api/reset", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await phone.DeleteAsync("/page/")).StatusCode);
        // A query is not passed on.
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/page/api/usage?file=x")).StatusCode);
        lock (_fake.Asked) Assert.Equal(new[] { "GET /", "GET /api/usage", "GET /api/usage" }, _fake.Asked);
    }

    [Fact]
    public async Task Nobody_reads_the_page_without_being_paired()
    {
        _server.Page = () => Named("/api/usage");
        using var stranger = Client();
        using var wrong = Client("not-a-token");
        foreach (var c in new[] { stranger, wrong })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/page/")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/page/api/usage")).StatusCode);
        }
        lock (_fake.Asked) Assert.Empty(_fake.Asked);
    }

    [Fact]
    public async Task What_the_page_does_wrong_is_said_and_never_passed_on()
    {
        string token = await PairAsync();
        using var phone = Client(token);
        _server.Page = () => Named("/gone", "/moved", "/big", "/zip", "/api/usage");

        async Task<(HttpStatusCode, string)> Ask(string path)
        {
            var res = await phone.GetAsync(path);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }

        _fake.Say("/gone", "text/plain", "boom", status: 500);
        Assert.Equal((HttpStatusCode.BadGateway, "Usage answered 500."), await Ask("/page/gone"));
        // A redirect is not followed: it could lead anywhere.
        _fake.Say("/moved", "text/plain", "", status: 302, extra: $"Location: http://127.0.0.1:{_fake.Port}/api/reset\r\n");
        Assert.Equal((HttpStatusCode.BadGateway, "Usage answered 302."), await Ask("/page/moved"));
        lock (_fake.Asked) Assert.DoesNotContain("GET /api/reset", _fake.Asked);
        _fake.Say("/big", "text/html", new string('x', PhonePage.MostBytes + 1));
        Assert.Equal((HttpStatusCode.BadGateway, "Usage sent more than can be shown."), await Ask("/page/big"));
        // And when it does not say how much is coming, the count is kept as it arrives.
        _fake.Say("/big", "text/html", new string('x', PhonePage.MostBytes + 1), extra: "unsized");
        Assert.Equal((HttpStatusCode.BadGateway, "Usage sent more than can be shown."), await Ask("/page/big"));
        _fake.Say("/big", "text/html", "small enough", extra: "unsized");
        Assert.Equal((HttpStatusCode.OK, "small enough"), await Ask("/page/big"));
        _fake.Say("/zip", "application/zip", "PK");
        Assert.Equal((HttpStatusCode.BadGateway, "Usage sent something that is not a page."), await Ask("/page/zip"));

        // The page's server is not running.
        _fake.Dispose();
        Assert.Equal((HttpStatusCode.BadGateway, "Usage is not answering on the deck's computer."), await Ask("/page/api/usage"));
    }
}
