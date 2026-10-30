using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SessionDeck.Host;

namespace SessionDeck.Gateway;

/// <summary>
/// What a paired phone talks to: the session list, a live feed of it, a relayed terminal per host,
/// and the terminal page itself. Same plain TcpListener dialect as a host, so it needs no URL
/// reservation and no elevation. Every request carries a device token; a WebSocket from a web view
/// cannot send a header, so upgrades take the token in the query and must come from no origin (a
/// native client) or this gateway's own.
/// </summary>
internal sealed class GatewayServer : IAsyncDisposable
{
    const int MaxBody = 64 * 1024;
    const int RevokedClose = 4401;
    static readonly Regex SafeFile = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly IPAddress _bind;
    readonly int _requestedPort;
    readonly DeviceStore _devices;
    readonly Func<IReadOnlyList<CatalogEntry>> _catalog;
    readonly Func<string, HostRecord?> _hostById;
    readonly Func<string, Stream?> _embedded;
    readonly string _adminToken;
    readonly string _relayKey;
    readonly ConcurrentDictionary<Connection, byte> _connections = new();

    /// <summary>The one device that is a key in the settings rather than a row in devices.json: the phone build that comes in through the relay.</summary>
    internal const string RelayDeviceId = "relay";
    static readonly PairedDevice RelayDevice = new(RelayDeviceId, "Relay", "", DateTime.UnixEpoch, DateTime.UnixEpoch);

    /// <summary>Starts a session for a phone and returns its host id; null when the request cannot be honoured. Set by the process.</summary>
    public Func<StartRequest, string?>? StartSession;
    /// <summary>Folders a phone may start a session in.</summary>
    public Func<List<string>> Folders = () => new();
    /// <summary>The latest machine load sample, or null before the first.</summary>
    public Func<MachineLoad?> Machine = () => null;
    readonly CancellationTokenSource _stop = new();
    TcpListener? _listener;
    Task? _accept;

    sealed class Connection
    {
        public required string DeviceId;
        public required WebSocket Socket;
        public required CancellationTokenSource Cancel;
        public Channel<string>? Events;
    }

    public GatewayServer(IPAddress bind, int port, DeviceStore devices, Func<IReadOnlyList<CatalogEntry>> catalog,
                         Func<string, HostRecord?> hostById, Func<string, Stream?> embedded, string adminToken, string relayKey = "")
    {
        _bind = bind;
        _requestedPort = port;
        _devices = devices;
        _catalog = catalog;
        _hostById = hostById;
        _embedded = embedded;
        _adminToken = adminToken;
        _relayKey = relayKey.Trim();
    }

    public int Port { get; private set; }
    public int Subscribers => _connections.Keys.Count(c => c.Events != null);
    public event Action? QuitRequested;

    public Task StartAsync()
    {
        _listener = new TcpListener(_bind, _requestedPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptLoop);
        return Task.CompletedTask;
    }

    /// <summary>Tell every listening phone the list now.</summary>
    public void CatalogChanged()
    {
        string frame = EventsFrame();
        foreach (var c in _connections.Keys)
            c.Events?.Writer.TryWrite(frame);
    }

    /// <summary>Forget a device and cut every socket it holds, so a revoked phone cannot keep typing.</summary>
    public bool Revoke(string deviceId)
    {
        if (deviceId == RelayDeviceId) return false;
        bool removed = _devices.Revoke(deviceId);
        foreach (var c in _connections.Keys.Where(c => c.DeviceId == deviceId))
        {
            _ = CloseAsync(c.Socket, (WebSocketCloseStatus)RevokedClose, "revoked");
            c.Cancel.Cancel();
        }
        return removed;
    }

    string EventsFrame() => JsonSerializer.Serialize(new { type = "sessions", sessions = _catalog(), machine = Machine() }, Json);

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(_stop.Token); }
            catch { break; }
            _ = Task.Run(() => Serve(tcp));
        }
    }

    async Task Serve(TcpClient tcp)
    {
        tcp.NoDelay = true;
        using var _ = tcp;
        var stream = tcp.GetStream();
        try
        {
            var r = await MiniHttp.ReadRequest(stream);
            if (r == null) return;
            await Route(stream, r);
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or OperationCanceledException) { }
        catch (Exception ex) { App.LogError(ex); }
    }

    async Task Route(NetworkStream s, MiniHttp.Request r)
    {
        var now = DateTime.UtcNow;
        if (r.Method == "GET" && r.Path == "/health") { await MiniHttp.WriteStatus(s, 204, "No Content"); return; }
        // Through the proxy there is no pairing code to guess and no admin: those belong to the LAN.
        if (Forwarded(r) && (r.Path == "/pair" || r.Path.StartsWith("/admin/"))) { await MiniHttp.WriteStatus(s, 404, "Not Found"); return; }
        switch (r.Method, r.Path)
        {
            case ("POST", "/pair"): await Pair(s, r, now); return;
            case ("POST", "/admin/pair-code"):
                if (!IsAdmin(r)) { await MiniHttp.WriteStatus(s, 401, "Unauthorized"); return; }
                string code = _devices.IssuePairingCode(now);
                await MiniHttp.WriteJson(s, 200, "OK", JsonSerializer.Serialize(new { code, expiresAtUtc = _devices.CodeExpiresUtc }, Json));
                return;
            case ("POST", "/admin/quit"):
                if (!IsAdmin(r)) { await MiniHttp.WriteStatus(s, 401, "Unauthorized"); return; }
                await MiniHttp.WriteStatus(s, 204, "No Content");
                QuitRequested?.Invoke();
                return;
        }

        if (r.Method == "GET" && r.Path.StartsWith("/mobile/")) { await Static(s, r.Path["/mobile/".Length..]); return; }

        string? presented = BearerToken(r);
        var device = KeyMatches(presented ?? "", _relayKey) ? RelayDevice : _devices.Authenticate(presented, now);
        bool admin = IsAdmin(r);
        if (r.IsWebSocketUpgrade)
        {
            if (device == null) { await MiniHttp.WriteStatus(s, 401, "Unauthorized"); return; }
            if (!OriginAllowed(r)) { await MiniHttp.WriteStatus(s, 403, "Forbidden"); return; }
            if (r.Path == "/events") { await Events(s, r, device); return; }
            var attach = Regex.Match(r.Path, @"^/sessions/([A-Za-z0-9]+)/attach$");
            if (attach.Success) { await Attach(s, r, device, attach.Groups[1].Value); return; }
            await MiniHttp.WriteStatus(s, 404, "Not Found");
            return;
        }

        if (device == null && !admin) { await MiniHttp.WriteStatus(s, 401, "Unauthorized"); return; }
        switch (r.Method, r.Path)
        {
            case ("GET", "/sessions"):
                await MiniHttp.WriteJson(s, 200, "OK", SessionCatalog.ToJson(_catalog()));
                return;
            case ("GET", "/meta"):
                await MiniHttp.WriteJson(s, 200, "OK", JsonSerializer.Serialize(new { folders = Folders(), providers = SessionStarter.Providers, machine = Machine() }, Json));
                return;
            case ("POST", "/sessions"):
                await Start(s, r);
                return;
            case ("GET", "/devices"):
                await MiniHttp.WriteJson(s, 200, "OK", JsonSerializer.Serialize(_devices.Devices.Select(d => new { d.Id, d.Name, d.PairedAtUtc, d.LastSeenUtc }), Json));
                return;
        }
        var del = Regex.Match(r.Path, @"^/devices/([A-Za-z0-9]+)$");
        if (r.Method == "DELETE" && del.Success)
        {
            bool revoked = Revoke(del.Groups[1].Value);
            await MiniHttp.WriteStatus(s, revoked ? 204 : 404, revoked ? "No Content" : "Not Found");
            return;
        }
        await MiniHttp.WriteStatus(s, 404, "Not Found");
    }

    async Task Start(NetworkStream s, MiniHttp.Request r)
    {
        if (StartSession == null) { await MiniHttp.WriteJson(s, 501, "Not Implemented", "{\"error\":\"starting sessions is off\"}"); return; }
        var body = await MiniHttp.ReadBody(s, r, MaxBody);
        StartRequest? request = null;
        try { request = JsonSerializer.Deserialize<StartRequest>(body, Json); } catch (JsonException) { }
        if (request == null || string.IsNullOrWhiteSpace(request.Provider)) { await MiniHttp.WriteJson(s, 400, "Bad Request", "{\"error\":\"provider and cwd are required\"}"); return; }
        string? hostId = StartSession(request);
        if (hostId == null) { await MiniHttp.WriteJson(s, 400, "Bad Request", "{\"error\":\"unknown provider or folder\"}"); return; }
        await MiniHttp.WriteJson(s, 201, "Created", JsonSerializer.Serialize(new { hostId }, Json));
    }

    async Task Pair(NetworkStream s, MiniHttp.Request r, DateTime now)
    {
        var body = await MiniHttp.ReadBody(s, r, MaxBody);
        string code = "", name = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("deviceName", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString() ?? "";
        }
        catch (JsonException) { }
        var paired = _devices.Redeem(code, name, now);
        if (paired == null) { await MiniHttp.WriteJson(s, 403, "Forbidden", "{\"error\":\"bad code\"}"); return; }
        await MiniHttp.WriteJson(s, 200, "OK", JsonSerializer.Serialize(new { token = paired.Value.Token, deviceId = paired.Value.Device.Id }, Json));
    }

    async Task Static(NetworkStream s, string file)
    {
        if (!SafeFile.IsMatch(file)) { await MiniHttp.WriteStatus(s, 404, "Not Found"); return; }
        using var stream = _embedded(file);
        if (stream == null) { await MiniHttp.WriteStatus(s, 404, "Not Found"); return; }
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        string type = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            _ => "application/octet-stream",
        };
        await MiniHttp.WriteBody(s, 200, "OK", type, ms.ToArray());
    }

    async Task Events(NetworkStream s, MiniHttp.Request r, PairedDevice device)
    {
        await MiniHttp.WriteUpgrade(s, r.WebSocketKey);
        using var ws = WebSocket.CreateFromStream(s, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
        var conn = new Connection { DeviceId = device.Id, Socket = ws, Cancel = new CancellationTokenSource(), Events = Channel.CreateUnbounded<string>() };
        if (BeforeRegisterForTests != null) await BeforeRegisterForTests();
        _connections[conn] = 0;
        try
        {
            if (!await StillPaired(conn)) return;
            conn.Events.Writer.TryWrite(EventsFrame());
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(conn.Cancel.Token, _stop.Token);
            var send = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in conn.Events.Reader.ReadAllAsync(linked.Token))
                        await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, linked.Token);
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            });
            var buf = new byte[1024];
            try
            {
                while (ws.State == WebSocketState.Open && !linked.IsCancellationRequested)
                {
                    var res = await ws.ReceiveAsync(buf, linked.Token);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            linked.Cancel();
            await send;
            await CloseAsync(ws, WebSocketCloseStatus.NormalClosure, "bye");
        }
        finally { _connections.TryRemove(conn, out _); }
    }

    async Task Attach(NetworkStream s, MiniHttp.Request r, PairedDevice device, string hostId)
    {
        var host = _hostById(hostId);
        if (host == null || host.HasExited) { await MiniHttp.WriteStatus(s, 404, "Not Found"); return; }
        if (host.Protocol < 4) { await MiniHttp.WriteJson(s, 409, "Conflict", "{\"error\":\"restart from the desktop to open here\"}"); return; }
        long after = r.Query.TryGetValue("after", out var a) && long.TryParse(a, out var parsed) ? parsed : 0;
        await MiniHttp.WriteUpgrade(s, r.WebSocketKey);
        using var ws = WebSocket.CreateFromStream(s, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
        var conn = new Connection { DeviceId = device.Id, Socket = ws, Cancel = new CancellationTokenSource() };
        if (BeforeRegisterForTests != null) await BeforeRegisterForTests();
        _connections[conn] = 0;
        try
        {
            if (!await StillPaired(conn)) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(conn.Cancel.Token, _stop.Token);
            await AttachRelay.RunAsync(ws, host, after, linked.Token);
        }
        catch (WebSocketException) { await CloseAsync(ws, WebSocketCloseStatus.EndpointUnavailable, "host unreachable"); }
        finally { _connections.TryRemove(conn, out _); }
    }

    /// <summary>Test seam: runs after a socket is authenticated and before it is registered, where a revoke used to slip through.</summary>
    internal Func<Task>? BeforeRegisterForTests;

    /// <summary>A revoke between authentication and registration would miss this socket; checked again once it is registered.</summary>
    async Task<bool> StillPaired(Connection conn)
    {
        bool paired = conn.DeviceId == RelayDeviceId || _devices.Devices.Any(d => d.Id == conn.DeviceId);
        if (paired && !conn.Cancel.IsCancellationRequested) return true;
        await CloseAsync(conn.Socket, (WebSocketCloseStatus)RevokedClose, "revoked");
        return false;
    }

    static string? BearerToken(MiniHttp.Request r)
    {
        string auth = r.Header("Authorization");
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        return r.Query.TryGetValue("token", out var t) ? t : null;
    }

    bool IsAdmin(MiniHttp.Request r) => _adminToken.Length > 0 && r.Header("X-Admin-Token") == _adminToken;

    /// <summary>Caddy on the relay adds this; nothing on the LAN does.</summary>
    internal static bool Forwarded(MiniHttp.Request r) => r.Header("X-Forwarded-For").Length > 0;

    /// <summary>The relay key compared in constant time; an empty key on either side never matches.</summary>
    internal static bool KeyMatches(string presented, string key)
    {
        string p = presented.Trim(), k = key.Trim();
        if (p.Length == 0 || k.Length == 0) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(p), Encoding.UTF8.GetBytes(k));
    }

    /// <summary>A page may only open sockets to the host it was served from; behind the relay that host speaks https, which the proxy says so in X-Forwarded-Proto.</summary>
    internal static bool OriginAllowed(MiniHttp.Request r)
    {
        string origin = r.Header("Origin");
        if (origin.Length == 0) return true;
        string proto = r.Header("X-Forwarded-Proto");
        string scheme = proto.Length > 0 ? proto : "http";
        return string.Equals(origin, scheme + "://" + r.Header("Host"), StringComparison.OrdinalIgnoreCase);
    }

    static async Task CloseAsync(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(status, reason, new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener?.Stop(); } catch { }
        foreach (var c in _connections.Keys) { c.Cancel.Cancel(); await CloseAsync(c.Socket, WebSocketCloseStatus.EndpointUnavailable, "gateway stopping"); }
        if (_accept != null) { try { await _accept; } catch { } }
    }
}
