using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeSessionMonitor;

/// <summary>
/// Tiny loopback listener that Claude Code hooks ping (via curl) so the app can refresh instantly
/// instead of waiting for the 1s poll. Raw TcpListener — no HttpListener URL-ACL / admin needed.
/// We don't care about the request body; any connection means "something changed → refresh".
/// </summary>
internal sealed class HookServer
{
    public const int Port = 53127;

    readonly Action _onEvent;
    TcpListener? _listener;
    volatile bool _running;

    public HookServer(Action onEvent) => _onEvent = onEvent;

    public void Start()
    {
        if (_running) return;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _ = AcceptLoop();
        }
        catch { _running = false; _listener = null; }   // port taken etc. — polling still works
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    async Task AcceptLoop()
    {
        var listener = _listener;
        while (_running && listener != null)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Respond(client);
            try { _onEvent(); } catch { }
        }
    }

    static async Task Respond(TcpClient client)
    {
        try
        {
            using (client)
            {
                var ns = client.GetStream();
                var buf = new byte[4096];
                try { await ns.ReadAsync(buf, 0, buf.Length); } catch { }   // drain request, ignore body
                var resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await ns.WriteAsync(resp, 0, resp.Length);
            }
        }
        catch { }
    }
}
