using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace SessionDeck.Host;

/// <summary>What the app hands a new host on stdin.</summary>
internal sealed class HostSpec
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Provider { get; set; } = "Claude";
    public string CommandLine { get; set; } = "";
    public string Cwd { get; set; } = "";
    public short Cols { get; set; } = 120;
    public short Rows { get; set; } = 30;
    public string HostsDir { get; set; } = "";
    public int RingBytes { get; set; } = 4 * 1024 * 1024;
    /// <summary>Typed into the session once it has drawn something and then stayed quiet for a
    /// moment: the CLI's prompt is up by then. Text first, Enter as a separate write, because the
    /// Codex TUI does not submit both when they arrive in one chunk.</summary>
    public string InitialPrompt { get; set; } = "";
}

/// <summary>
/// The host's public record, <c>hosts/&lt;id&gt;.json</c>. Written at start, rewritten when the
/// title changes and when the child exits. The app reads it to reattach after its own restart.
/// </summary>
internal sealed class HostRecord
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Provider { get; set; } = "Claude";
    public string CommandLine { get; set; } = "";
    public string Cwd { get; set; } = "";
    public int HostPid { get; set; }
    public long HostStartTicks { get; set; }
    public int ChildPid { get; set; }
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public uint? ExitCode { get; set; }
    public DateTime? ExitedAt { get; set; }
    public string AgentStatus { get; set; } = "";
    public string LastEvent { get; set; } = "";
    public string LastTool { get; set; } = "";
    public DateTime? StatusAt { get; set; }
    public string TranscriptPath { get; set; } = "";
    public int HookEvents { get; set; }

    /// <summary>The stamped copy this host (and its hook forwarders) run from; the app must not prune it while the host lives.</summary>
    public string HostBin { get; set; } = "";

    /// <summary>The current turn armed something that will wake the session by itself.</summary>
    public bool Pending { get; set; }

    /// <summary>How this host derives status. 2: idle notifications no longer mean waiting; turns can end scheduled.</summary>
    public int Protocol { get; set; }

    [JsonIgnore] public bool HasExited => ExitCode.HasValue;
}

/// <summary>
/// <c>SessionDeck.exe --host</c>: one process owning one pseudoconsole. It outlives the app window
/// on purpose — the app is a viewer that attaches over a loopback WebSocket and can come and go.
/// </summary>
internal static class HostProgram
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly byte[] DsrQuery = "\x1b[6n"u8.ToArray();
    static readonly byte[] DsrReply = "\x1b[1;1R"u8.ToArray();

    sealed class Client
    {
        public readonly Channel<(bool Text, byte[] Data)> Out = Channel.CreateUnbounded<(bool, byte[])>(new UnboundedChannelOptions { SingleReader = true });
    }

    static readonly List<Client> Clients = new();
    static readonly object ClientsLock = new();
    static Ring _ring = null!;
    static ConPty _pty = null!;
    static HostRecord _record = null!;
    static string _recordPath = "";
    static volatile bool _exited;
    static uint _exitCode;
    static readonly MemoryStream _oscBuf = new();
    static bool _inOsc;

    public static int Run()
    {
        HostSpec spec;
        try
        {
            string json = Console.In.ReadToEnd();
            spec = JsonSerializer.Deserialize<HostSpec>(json) ?? throw new InvalidDataException("empty spec");
        }
        catch (Exception ex)
        {
            Log($"bad spec: {ex}");
            return 2;
        }

        try { return Serve(spec); }
        catch (Exception ex)
        {
            Log($"host {spec.Id}: {ex}");
            return 1;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);
    const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;

    /// <summary>
    /// A ConPTY child inherits its parent's standard-handle VALUES when they are redirected, and
    /// those values mean nothing in the child, so it reads EOF and exits at once. The spec arrives on
    /// our stdin, so before the child is created our std slots are cleared; a process with no std
    /// handles is what the console subsystem expects and it then hands the child real console handles.
    /// </summary>
    static void DetachStdHandles()
    {
        SetStdHandle(STD_INPUT_HANDLE, IntPtr.Zero);
        SetStdHandle(STD_OUTPUT_HANDLE, IntPtr.Zero);
        SetStdHandle(STD_ERROR_HANDLE, IntPtr.Zero);
    }

    static int Serve(HostSpec spec)
    {
        Directory.CreateDirectory(spec.HostsDir);
        _recordPath = Path.Combine(spec.HostsDir, spec.Id + ".json");
        _ring = new Ring(Math.Max(spec.RingBytes, 64 * 1024));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        string hookCmd = Hooks.HookCommand(Environment.ProcessPath!, port, token);
        string commandLine = spec.CommandLine.Replace(Hooks.CommandPlaceholder, hookCmd);
        if (commandLine.Contains(Hooks.ClaudeSettingsPlaceholder))
        {
            string settingsDir = Path.Combine(Path.GetDirectoryName(spec.HostsDir)!, "hook-settings");
            Directory.CreateDirectory(settingsDir);
            string settingsPath = Path.Combine(settingsDir, spec.Id + ".claude.json");
            File.WriteAllText(settingsPath, Hooks.ClaudeSettingsJson(hookCmd));
            commandLine = commandLine.Replace(Hooks.ClaudeSettingsPlaceholder, settingsPath.Replace('\\', '/'));
        }

        var ready = Console.Out;
        DetachStdHandles();
        _pty = new ConPty(commandLine, spec.Cwd, spec.Cols, spec.Rows);
        var self = Process.GetCurrentProcess();
        _record = new HostRecord
        {
            Id = spec.Id,
            SessionId = spec.SessionId,
            Provider = spec.Provider,
            CommandLine = spec.CommandLine,
            Cwd = spec.Cwd,
            HostPid = self.Id,
            HostStartTicks = self.StartTime.ToUniversalTime().Ticks,
            HostBin = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "",
            Protocol = 2,
            ChildPid = _pty.Pid,
            Port = port,
            Token = token,
            StartedAt = DateTime.UtcNow,
        };
        WriteRecord();
        ready.WriteLine("ready");
        ready.Flush();

        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" };
        reader.Start();
        if (spec.InitialPrompt.Length > 0)
            new Thread(() => TypeWhenQuiet(spec.InitialPrompt)) { IsBackground = true, Name = "initial-prompt" }.Start();
        var waiter = new Thread(() =>
        {
            _exitCode = _pty.WaitForExit();
            _exited = true;
            _pty.ClosePseudoConsole();
        }) { IsBackground = true, Name = "pty-wait" };
        waiter.Start();

        var accept = Task.Run(() => AcceptLoop(listener));
        reader.Join();
        waiter.Join();

        _record.ExitCode = _exitCode;
        _record.ExitedAt = DateTime.UtcNow;
        WriteRecord();
        try { File.Delete(Path.Combine(Path.GetDirectoryName(spec.HostsDir)!, "hook-settings", spec.Id + ".claude.json")); } catch { }
        Broadcast(true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "exit", code = _exitCode })));
        lock (ClientsLock) foreach (var c in Clients) c.Out.Writer.TryComplete();
        listener.Stop();
        Thread.Sleep(300);
        _pty.Dispose();
        return 0;
    }

    static long _lastOutputTicks;
    static long _outputBytes;

    /// <summary>
    /// Ready means: the CLI has drawn something, has been quiet for two seconds, is not showing a
    /// folder-trust question, and (Claude) has reported its first hook event — that event only fires
    /// once the conversation exists, i.e. after any trust dialog. Codex has no start-of-session hook
    /// in its TUI, so the trust check is what guards it. Waits up to five minutes, then gives up.
    /// </summary>
    static void TypeWhenQuiet(string prompt)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        bool claude = string.Equals(_record.Provider, "Claude", StringComparison.OrdinalIgnoreCase);
        while (DateTime.UtcNow < deadline && !_exited)
        {
            Thread.Sleep(250);
            long bytes = Interlocked.Read(ref _outputBytes);
            long last = Interlocked.Read(ref _lastOutputTicks);
            if (bytes < 200 || last == 0) continue;
            if (DateTime.UtcNow.Ticks - last < TimeSpan.TicksPerSecond * 2) continue;
            if (claude && _record.HookEvents == 0) continue;
            var (_, tail) = _ring.Since(Math.Max(0, _ring.End - 6000));
            string recent = Encoding.UTF8.GetString(tail);
            if (recent.Contains("trust this folder", StringComparison.OrdinalIgnoreCase)
                || recent.Contains("trust", StringComparison.OrdinalIgnoreCase) && recent.Contains("Enter to confirm", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var text = Encoding.UTF8.GetBytes(prompt.Replace("\r\n", "\n").Replace('\n', ' '));
                lock (_pty) { _pty.Input.Write(text); _pty.Input.Flush(); }
                Thread.Sleep(400);
                lock (_pty) { _pty.Input.Write("\r"u8); _pty.Input.Flush(); }
            }
            catch (Exception ex) { Log($"initial prompt: {ex.Message}"); }
            return;
        }
    }

    static void ReadLoop()
    {
        var buf = new byte[65536];
        while (true)
        {
            int n;
            try { n = _pty.Output.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            var chunk = buf.AsSpan(0, n);
            Interlocked.Add(ref _outputBytes, n);
            Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
            _ring.Append(chunk);
            ScanTitle(chunk);
            bool anyViewer;
            lock (ClientsLock) anyViewer = Clients.Count > 0;
            if (!anyViewer && chunk.IndexOf(DsrQuery) >= 0)
            {
                try { _pty.Input.Write(DsrReply); _pty.Input.Flush(); } catch { }
            }
            Broadcast(false, chunk.ToArray());
        }
    }

    /// <summary>OSC 0 / OSC 2 window-title sequences, the way a terminal would read them.</summary>
    static void ScanTitle(ReadOnlySpan<byte> chunk)
    {
        for (int i = 0; i < chunk.Length; i++)
        {
            byte b = chunk[i];
            if (!_inOsc)
            {
                if (b == 0x1b && i + 1 < chunk.Length && chunk[i + 1] == (byte)']')
                {
                    _inOsc = true;
                    _oscBuf.SetLength(0);
                    i++;
                }
                continue;
            }
            if (b == 0x07 || b == 0x1b)
            {
                _inOsc = false;
                string s = Encoding.UTF8.GetString(_oscBuf.GetBuffer(), 0, (int)_oscBuf.Length);
                if (s.StartsWith("0;") || s.StartsWith("2;"))
                {
                    string title = s[2..].Trim();
                    if (title.Length > 0 && title != _record.Title)
                    {
                        _record.Title = title;
                        WriteRecord();
                        Broadcast(true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "title", title })));
                    }
                }
                continue;
            }
            if (_oscBuf.Length < 512) _oscBuf.WriteByte(b);
        }
    }

    static void Broadcast(bool text, byte[] data)
    {
        Client[] snapshot;
        lock (ClientsLock) snapshot = Clients.ToArray();
        foreach (var c in snapshot) c.Out.Writer.TryWrite((text, data));
    }

    static async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => ServeClient(tcp));
        }
    }

    static async Task ServeClient(TcpClient tcp)
    {
        tcp.NoDelay = true;
        using var _ = tcp;
        var stream = tcp.GetStream();
        var (request, leftover) = await ReadHttpHead(stream);
        if (request == null) return;
        var lines = request.Split("\r\n");
        var reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return;
        string path = reqLine[1];
        if (reqLine[0] == "POST")
        {
            await ServeHook(stream, lines, path, leftover);
            return;
        }
        if (reqLine[0] != "GET") return;
        string? key = null;
        foreach (var l in lines.Skip(1))
            if (l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = l[18..].Trim();
        if (key == null) return;

        var query = ParseQuery(path);
        if (!query.TryGetValue("token", out var token) || token != _record.Token)
        {
            await WriteAscii(stream, "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
            return;
        }
        long after = query.TryGetValue("after", out var a) && long.TryParse(a, out var parsed) ? parsed : 0;

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await WriteAscii(stream, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");

        using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
        var client = new Client();

        (long start, byte[] data) = _ring.Since(after);
        long end = start + data.Length;
        client.Out.Writer.TryWrite((true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "snapshot", start, end, title = _record.Title }))));
        if (data.Length > 0) client.Out.Writer.TryWrite((false, data));
        lock (ClientsLock) Clients.Add(client);
        if (_exited)
        {
            client.Out.Writer.TryWrite((true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "exit", code = _exitCode }))));
            client.Out.Writer.TryComplete();
        }

        var send = Task.Run(async () =>
        {
            try
            {
                await foreach (var (text, bytes) in client.Out.Reader.ReadAllAsync())
                    await ws.SendAsync(bytes, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "exit", CancellationToken.None);
            }
            catch { }
        });

        try
        {
            var inbuf = new byte[16384];
            var msg = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(inbuf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                msg.Write(inbuf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                HandleMessage(msg.ToArray());
                msg.SetLength(0);
            }
        }
        catch { }
        finally
        {
            lock (ClientsLock) Clients.Remove(client);
            client.Out.Writer.TryComplete();
            try { await send; } catch { }
        }
    }

    static void HandleMessage(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "input":
                    if (_exited) return;
                    var bytes = Encoding.UTF8.GetBytes(root.GetProperty("data").GetString() ?? "");
                    lock (_pty) { _pty.Input.Write(bytes); _pty.Input.Flush(); }
                    break;
                case "resize":
                    if (_exited) return;
                    _pty.Resize((short)root.GetProperty("cols").GetInt32(), (short)root.GetProperty("rows").GetInt32());
                    break;
                case "kill":
                    _pty.KillTree();
                    break;
            }
        }
        catch (Exception ex) { Log($"message: {ex.Message}"); }
    }

    static async Task<(string? Head, byte[] Leftover)> ReadHttpHead(NetworkStream s)
    {
        var buf = new byte[65536];
        int len = 0;
        while (len < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(len));
            if (n <= 0) return (null, Array.Empty<byte>());
            len += n;
            int at = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
            if (at >= 0) return (Encoding.ASCII.GetString(buf, 0, at), buf.AsSpan(at + 4, len - at - 4).ToArray());
        }
        return (null, Array.Empty<byte>());
    }

    /// <summary><c>POST /hook?token=…</c> from <c>SessionDeck.exe --hook</c>: one CLI hook event as
    /// JSON. Updates the record (status, last tool, identity) and tells attached viewers.</summary>
    static async Task ServeHook(NetworkStream stream, string[] lines, string path, byte[] leftover)
    {
        var query = ParseQuery(path);
        if (!path.StartsWith("/hook") || !query.TryGetValue("token", out var token) || token != _record.Token)
        {
            await WriteAscii(stream, "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
            return;
        }
        int length = 0;
        foreach (var l in lines.Skip(1))
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l[15..].Trim(), out length);
        length = Math.Min(length, 1 << 20);
        var body = new byte[length];
        int have = Math.Min(leftover.Length, length);
        Array.Copy(leftover, body, have);
        while (have < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(have));
            if (n <= 0) break;
            have += n;
        }
        try
        {
            using var doc = JsonDocument.Parse(body.AsMemory(0, have));
            ApplyHook(doc.RootElement);
        }
        catch (Exception ex) { Log($"hook: {ex.Message}"); }
        await WriteAscii(stream, "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
    }

    static void ApplyHook(JsonElement root)
    {
        string ev = root.TryGetProperty("hook_event_name", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        if (ev.Length == 0) return;
        lock (_record)
        {
            _record.HookEvents++;
            _record.LastEvent = ev;
            _record.StatusAt = DateTime.UtcNow;
            bool turnStarts = ev is "UserPromptSubmit" or "PreToolUse" && _record.AgentStatus is "idle" or "scheduled" or "waiting" or "";
            if (ev == "SessionStart" || turnStarts) _record.Pending = false;
            if (ev == "PreToolUse" && Hooks.Defers(root)) _record.Pending = true;
            string? status = Hooks.StatusFor(ev, root, _record.Pending);
            if (status != null) _record.AgentStatus = status;
            if (root.TryGetProperty("tool_name", out var t) && t.ValueKind == JsonValueKind.String && ev == "PreToolUse")
                _record.LastTool = t.GetString() ?? "";
            if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String && _record.SessionId.Length == 0)
                _record.SessionId = sid.GetString() ?? "";
            if (root.TryGetProperty("transcript_path", out var tp) && tp.ValueKind == JsonValueKind.String && _record.TranscriptPath.Length == 0)
                _record.TranscriptPath = tp.GetString() ?? "";
        }
        WriteRecord();
        Broadcast(true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "hook", @event = ev, status = _record.AgentStatus, tool = _record.LastTool })));
    }

    static Dictionary<string, string> ParseQuery(string path)
    {
        var d = new Dictionary<string, string>();
        int q = path.IndexOf('?');
        if (q < 0) return d;
        foreach (var part in path[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) d[Uri.UnescapeDataString(part)] = "";
            else d[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return d;
    }

    static Task WriteAscii(NetworkStream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    static void WriteRecord()
    {
        try
        {
            lock (_record)
            {
                string tmp = _recordPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_record, Json));
                File.Move(tmp, _recordPath, overwrite: true);
            }
        }
        catch (Exception ex) { Log($"record: {ex.Message}"); }
    }

    static void Log(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "sessiondeck-host.log"),
                $"{DateTime.Now:O} [{Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch { }
    }
}
