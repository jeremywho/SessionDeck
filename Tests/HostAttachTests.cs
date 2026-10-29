using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>Runs the real host process against a throwaway shell; nothing here touches the deck's own hosts.</summary>
public class HostAttachTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-attach-" + Guid.NewGuid().ToString("N"));
    Process? _host;

    public HostAttachTests() => Directory.CreateDirectory(Path.Combine(_dir, "hosts"));

    public void Dispose()
    {
        try { if (_host is { HasExited: false }) _host.Kill(true); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    static string HostExe => Path.Combine(Path.GetDirectoryName(typeof(HostProgram).Assembly.Location)!, "SessionDeck.exe");

    [Fact]
    public async Task A_viewer_learns_the_pty_size_on_attach_and_on_every_resize()
    {
        var spec = new HostSpec
        {
            Id = "attachtest01", SessionId = "x", Provider = "Shell", Title = "probe",
            CommandLine = $"\"{SessionLauncher.ResolvePowerShell()}\" -NoLogo -Command \"Start-Sleep 20\"",
            Cwd = _dir, HostsDir = Path.Combine(_dir, "hosts"), Cols = 120, Rows = 30, RingBytes = 65536,
        };
        var psi = new ProcessStartInfo(HostExe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add("--host");
        _host = Process.Start(psi)!;
        _host.StandardInput.Write(JsonSerializer.Serialize(spec));
        _host.StandardInput.Close();
        Assert.Equal("ready", await _host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));

        var record = JsonSerializer.Deserialize<HostRecord>(File.ReadAllText(Path.Combine(_dir, "hosts", "attachtest01.json")))!;
        Assert.Equal(4, record.Protocol);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{record.Port}/attach?token={record.Token}&after=0"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var first = await NextText(ws);
        Assert.Equal("snapshot", first.GetProperty("type").GetString());
        Assert.Equal(120, first.GetProperty("cols").GetInt32());
        Assert.Equal(30, first.GetProperty("rows").GetInt32());

        await Send(ws, "{\"type\":\"resize\",\"cols\":100,\"rows\":25}");
        var size = await NextText(ws, type: "size");
        Assert.Equal(100, size.GetProperty("cols").GetInt32());
        Assert.Equal(25, size.GetProperty("rows").GetInt32());

        await Send(ws, "{\"type\":\"kill\"}");
        Assert.True(_host.WaitForExit(15000), "host did not exit after kill");
    }

    static Task Send(ClientWebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<JsonElement> NextText(ClientWebSocket ws, string? type = null)
    {
        var buf = new byte[1 << 16];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, CancellationToken.None).WaitAsync(deadline - DateTime.UtcNow);
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            var el = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
            if (type == null || el.GetProperty("type").GetString() == type) return el;
        }
        throw new TimeoutException("no text frame" + (type == null ? "" : " of type " + type));
    }
}
