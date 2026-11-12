using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SessionDeck.Host;

/// <summary>
/// A chat host runs <c>claude -p</c> on stream-json pipes: no terminal, one JSON object per line
/// each way. This reads the outbound lines for what the host record needs (status, the live
/// conversation, the model, the name the CLI gives the session) and decides which lines a viewer
/// attaching later must still get.
/// </summary>
internal sealed class ChatStream
{
    public const string Kind = "chat";

    /// <summary>Flags every chat launch carries; the session flags and the hook settings follow them.</summary>
    public const string ProtocolFlags = "-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages --replay-user-messages --permission-prompt-tool stdio";

    readonly HashSet<string> _openRequests = new(StringComparer.Ordinal);

    /// <summary>
    /// Kept for a viewer that attaches later, or only passed to the viewers attached now. Token deltas
    /// and per-turn bookkeeping are the bulk of the stream and are worth nothing once the finished
    /// message has arrived, so they never take scrollback from the conversation itself.
    /// </summary>
    public static bool Durable(JsonElement line)
    {
        switch (Text(line, "type"))
        {
            case "stream_event":
            case "keep_alive":
                return false;
            case "system":
                return Text(line, "subtype") is not ("init" or "status" or "thinking_tokens" or "hook_started" or "hook_progress" or "hook_response" or "session_title_changed");
            default:
                return true;
        }
    }

    /// <summary>Fold one outbound line into the record; true when the record changed.</summary>
    public bool Apply(JsonElement line, HostRecord record, DateTime now)
    {
        string before = Signature(record);
        switch (Text(line, "type"))
        {
            case "system":
                if (Text(line, "subtype") == "init")
                {
                    if (Text(line, "session_id") is { Length: > 0 } session) record.SessionId = session;
                    if (Text(line, "model") is { Length: > 0 } model) record.Model = model;
                    Status(record, "busy", now);
                }
                else if (Text(line, "subtype") == "session_title_changed" && Text(line, "title") is { Length: > 0 } named) record.Title = named;
                break;
            case "user":
                if (line.TryGetProperty("isReplay", out var replay) && replay.ValueKind == JsonValueKind.True)
                {
                    record.LastEvent = "UserPromptSubmit";
                    Status(record, "busy", now);
                }
                break;
            case "assistant":
                if (ToolName(line) is { Length: > 0 } tool) record.LastTool = tool;
                if (_openRequests.Count == 0) Status(record, "busy", now);
                break;
            case "control_request":
                if (line.TryGetProperty("request", out var request) && Text(request, "subtype") == "can_use_tool")
                {
                    _openRequests.Add(Text(line, "request_id"));
                    if (Text(request, "tool_name") is { Length: > 0 } asked) record.LastTool = asked;
                    record.LastEvent = "PermissionRequest";
                    Status(record, "waiting", now);
                }
                break;
            case "control_response":
                if (line.TryGetProperty("response", out var response) && _openRequests.Remove(Text(response, "request_id")) && _openRequests.Count == 0)
                    Status(record, "busy", now);
                break;
            case "control_cancel_request":
                if (_openRequests.Remove(Text(line, "request_id")) && _openRequests.Count == 0) Status(record, "busy", now);
                break;
            case "result":
                _openRequests.Clear();
                record.LastEvent = "Stop";
                Status(record, "idle", now);
                break;
        }
        return Signature(record) != before;
    }

    static void Status(HostRecord record, string status, DateTime now)
    {
        if (record.AgentStatus == status) return;
        record.AgentStatus = status;
        record.StatusAt = now;
    }

    static string Signature(HostRecord r) => $"{r.AgentStatus}|{r.StatusAt?.Ticks}|{r.SessionId}|{r.Model}|{r.Title}|{r.LastTool}|{r.LastEvent}";

    /// <summary>
    /// The CLI asks its controller things over the same pipe. A viewer answers permission requests;
    /// anything else has nobody to answer it, and an unanswered request stalls the turn, so the host
    /// declines it here. Null when the line needs no answer from the host.
    /// </summary>
    public static string? Decline(JsonElement line)
    {
        if (Text(line, "type") != "control_request" || !line.TryGetProperty("request", out var request)) return null;
        string subtype = Text(request, "subtype");
        if (subtype == "can_use_tool") return null;
        return JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { subtype = "error", request_id = Text(line, "request_id"), error = $"Session Deck does not handle {subtype} requests" },
        });
    }

    /// <summary>One person's message, as the CLI reads it from its input.</summary>
    public static string UserLine(string text) =>
        JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text }, parent_tool_use_id = (string?)null, session_id = "" });

    /// <summary>What a viewer sent, as whole lines the CLI can read: anything that is not a JSON object is dropped rather than left to break the stream.</summary>
    public static List<string> InputLines(string data)
    {
        var lines = new List<string>();
        foreach (var raw in data.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) lines.Add(line);
            }
            catch (JsonException) { }
        }
        return lines;
    }

    /// <summary>A live-only line wrapped as the text frame a viewer gets it in.</summary>
    public static byte[] LiveFrame(ReadOnlySpan<byte> line)
    {
        var head = "{\"type\":\"live\",\"line\":"u8;
        var frame = new byte[head.Length + line.Length + 1];
        head.CopyTo(frame);
        line.CopyTo(frame.AsSpan(head.Length));
        frame[^1] = (byte)'}';
        return frame;
    }

    /// <summary>A line the host itself adds to the conversation: something the CLI printed outside the protocol, or why it ended.</summary>
    public static byte[] HostLine(string subtype, string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "host");
            w.WriteString("subtype", subtype);
            w.WriteString("text", text);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The conversation so far, read from the CLI's own transcript, in the shape the live stream uses.
    /// A resumed session starts a new process that says nothing about what came before; this is what
    /// a viewer shows above the first new message. Newest lines win when the transcript is larger
    /// than <paramref name="budget"/> bytes.
    /// </summary>
    public static List<byte[]> History(string transcriptPath, int budget)
    {
        var kept = new List<byte[]>();
        if (transcriptPath.Length == 0 || !File.Exists(transcriptPath)) return kept;
        string[] lines;
        try
        {
            using var file = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (IOException) { return kept; }
        catch (UnauthorizedAccessException) { return kept; }

        int used = 0;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (HistoryLine(lines[i]) is not { } line) continue;
            if (used + line.Length + 1 > budget) break;
            used += line.Length + 1;
            kept.Add(line);
        }
        kept.Reverse();
        return kept;
    }

    internal static byte[]? HistoryLine(string transcriptLine)
    {
        if (transcriptLine.Length < 2) return null;
        try
        {
            using var doc = JsonDocument.Parse(transcriptLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string type = Text(root, "type");
            if (type is not ("user" or "assistant")) return null;
            if (True(root, "isSidechain") || True(root, "isMeta")) return null;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("type", type);
                w.WriteBoolean("history", true);
                if (True(root, "isCompactSummary")) w.WriteBoolean("compactSummary", true);
                if (Text(root, "timestamp") is { Length: > 0 } at) w.WriteString("timestamp", at);
                w.WritePropertyName("message");
                message.WriteTo(w);
                w.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }
        catch (JsonException) { return null; }
    }

    static string? ToolName(JsonElement assistantLine)
    {
        if (!assistantLine.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        string? name = null;
        foreach (var block in content.EnumerateArray())
            if (Text(block, "type") == "tool_use") name = Text(block, "name");
        return name;
    }

    static bool True(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
