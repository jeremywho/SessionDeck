using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SessionDeck.Gateway;

/// <summary>
/// Just enough HTTP for a loopback or LAN listener on a plain <see cref="TcpListener"/>: read a request
/// head, parse its first line and query, answer, or finish a WebSocket handshake for
/// <see cref="System.Net.WebSockets.WebSocket.CreateFromStream"/>. No URL ACL, no admin rights.
/// </summary>
internal static class MiniHttp
{
    const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public sealed record Request(string Method, string Path, string Target, Dictionary<string, string> Headers, Dictionary<string, string> Query, byte[] Leftover)
    {
        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";
        public string WebSocketKey => Header("Sec-WebSocket-Key");
        public bool IsWebSocketUpgrade => WebSocketKey.Length > 0;
    }

    public static async Task<(string? Head, byte[] Leftover)> ReadHead(NetworkStream s)
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

    public static async Task<Request?> ReadRequest(NetworkStream s)
    {
        var (head, leftover) = await ReadHead(s);
        if (head == null) return null;
        return Parse(head, leftover);
    }

    public static Request? Parse(string head, byte[] leftover)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            int colon = l.IndexOf(':');
            if (colon > 0) headers[l[..colon].Trim()] = l[(colon + 1)..].Trim();
        }
        string target = first[1];
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        return new Request(first[0], path, target, headers, ParseQuery(target), leftover);
    }

    public static Dictionary<string, string> ParseQuery(string target)
    {
        var d = new Dictionary<string, string>();
        int q = target.IndexOf('?');
        if (q < 0) return d;
        foreach (var part in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) d[Uri.UnescapeDataString(part)] = "";
            else d[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return d;
    }

    /// <summary>The body of a request whose head has been read: what came with the head plus the rest of Content-Length, capped.</summary>
    public static async Task<byte[]> ReadBody(NetworkStream s, Request r, int maxBytes)
    {
        int length = int.TryParse(r.Header("Content-Length"), out var l) ? Math.Clamp(l, 0, maxBytes) : 0;
        var body = new byte[length];
        int have = Math.Min(r.Leftover.Length, length);
        Array.Copy(r.Leftover, body, have);
        while (have < length)
        {
            int n = await s.ReadAsync(body.AsMemory(have));
            if (n <= 0) break;
            have += n;
        }
        return have == length ? body : body[..have];
    }

    public static string AcceptKey(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

    public static Task WriteAscii(NetworkStream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public static Task WriteStatus(NetworkStream s, int status, string reason) =>
        WriteAscii(s, $"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    public static async Task WriteBody(NetworkStream s, int status, string reason, string contentType, byte[] body)
    {
        await WriteAscii(s, $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(body);
    }

    public static Task WriteJson(NetworkStream s, int status, string reason, string json) =>
        WriteBody(s, status, reason, "application/json", Encoding.UTF8.GetBytes(json));

    public static Task WriteUpgrade(NetworkStream s, string key) =>
        WriteAscii(s, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {AcceptKey(key)}\r\n\r\n");
}
