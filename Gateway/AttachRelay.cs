using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using SessionDeck.Host;

namespace SessionDeck.Gateway;

/// <summary>
/// A phone's view of one host: the host's attach stream relayed both ways. The phone may type and
/// nothing else; a <c>resize</c> would redraw the desktop pane and a <c>kill</c> would end the session,
/// so only <c>input</c> is forwarded and every other message is dropped where it arrives.
/// </summary>
internal static class AttachRelay
{
    public static Uri HostUri(HostRecord host, long after) =>
        new($"ws://127.0.0.1:{host.Port}/attach?token={Uri.EscapeDataString(host.Token)}&after={Math.Max(0, after)}");

    public static async Task RunAsync(WebSocket phone, HostRecord host, long after, CancellationToken ct)
    {
        using var hostWs = new ClientWebSocket();
        await hostWs.ConnectAsync(HostUri(host, after), ct);
        var toPhone = Pump(hostWs, phone, forwardAll: true, ct);
        var toHost = Pump(phone, hostWs, forwardAll: false, ct);
        await Task.WhenAny(toPhone, toHost);
        // Closing our side makes the peer answer with its own Close, which ends the other pump's
        // receive; cancelling that receive instead would abort the socket before the frame went out.
        await CloseQuietly(phone, "relay ended");
        await CloseQuietly(hostWs, "relay ended");
        await Task.WhenAny(Task.WhenAll(toPhone, toHost), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    /// <summary>Whether a message from the phone may reach the host: only a well-formed <c>input</c>.</summary>
    public static bool AllowedTowardHost(ReadOnlySpan<byte> message, WebSocketMessageType type)
    {
        if (type != WebSocketMessageType.Text) return false;
        try
        {
            using var doc = JsonDocument.Parse(message.ToArray());
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "input"
                && doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String;
        }
        catch (JsonException) { return false; }
    }

    static async Task Pump(WebSocket from, WebSocket to, bool forwardAll, CancellationToken ct)
    {
        var buf = new byte[32 * 1024];
        var msg = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && from.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r;
                msg.SetLength(0);
                do
                {
                    r = await from.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    msg.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                var bytes = msg.ToArray();
                if (!forwardAll && !AllowedTowardHost(bytes, r.MessageType)) continue;
                if (to.State != WebSocketState.Open) return;
                await to.SendAsync(bytes, r.MessageType, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
    }

    static async Task CloseQuietly(WebSocket ws, string reason)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        }
        catch { }
    }
}
