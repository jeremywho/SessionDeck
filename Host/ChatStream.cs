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

    /// <summary>The host's own requests to the CLI carry these ids, so their answers are told from a viewer's.</summary>
    public const string InitializeId = "sd-initialize";
    public const string SettingsId = "sd-settings";

    /// <summary>A viewer changing the model, the effort or the permission mode names its request with this prefix; the host then asks what is in force.</summary>
    public const string SetPrefix = "sd-set-";

    readonly HashSet<string> _openRequests = new(StringComparer.Ordinal);
    string _catalog = "";
    string _mode = "", _model = "", _effort = "";

    /// <summary>What <see cref="Apply"/> changed: the host record, the lists the CLI offers, the settings in force; and whether to ask the CLI for its settings again.</summary>
    public readonly record struct Change(bool Record, bool Catalog, bool Settings, bool Refresh);

    /// <summary>
    /// Kept for a viewer that attaches later, or only passed to the viewers attached now. Token deltas
    /// and per-turn bookkeeping are the bulk of the stream and are worth nothing once the finished
    /// message has arrived, so they never take scrollback from the conversation itself. An answer to
    /// a control request is kept only when it settles a permission request: that is part of the
    /// conversation, where the answer to "which models are there" is not. Ask before <see cref="Apply"/>.
    /// </summary>
    public bool Durable(JsonElement line)
    {
        switch (Text(line, "type"))
        {
            case "stream_event":
            case "keep_alive":
                return false;
            case "system":
                return Text(line, "subtype") is not ("init" or "status" or "thinking_tokens" or "hook_started" or "hook_progress" or "hook_response" or "session_title_changed");
            case "control_response":
                return line.TryGetProperty("response", out var response) && _openRequests.Contains(Text(response, "request_id"));
            default:
                return true;
        }
    }

    /// <summary>Fold one outbound line into the record and into what this knows of the session's lists and settings.</summary>
    public Change Apply(JsonElement line, HostRecord record, DateTime now)
    {
        string before = Signature(record), catalog = _catalog, settings = SettingsFrame();
        bool refresh = false;
        switch (Text(line, "type"))
        {
            case "system":
                if (Text(line, "subtype") == "init")
                {
                    if (Text(line, "session_id") is { Length: > 0 } session) record.SessionId = session;
                    if (Text(line, "model") is { Length: > 0 } model) record.Model = _model = model;
                    if (Text(line, "permissionMode") is { Length: > 0 } mode) _mode = mode;
                    Status(record, "busy", now);
                }
                else if (Text(line, "subtype") == "session_title_changed" && Text(line, "title") is { Length: > 0 } named) record.Title = named;
                break;
            case "user":
                // The CLI replays what a local command printed ("Set model to …") as if it were typed; that starts no turn.
                if (line.TryGetProperty("isReplay", out var replay) && replay.ValueKind == JsonValueKind.True && !IsLocalCommandOutput(line))
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
                if (!line.TryGetProperty("response", out var response)) break;
                string id = Text(response, "request_id");
                if (_openRequests.Remove(id)) { if (_openRequests.Count == 0) Status(record, "busy", now); break; }
                if (Text(response, "subtype") != "success") break;
                response.TryGetProperty("response", out var answer);
                if (id == InitializeId) Introduced(answer);
                else if (id == SettingsId) InForce(answer, record);
                else if (id.StartsWith(SetPrefix, StringComparison.Ordinal))
                {
                    if (Text(answer, "mode") is { Length: > 0 } set) _mode = set;
                    refresh = true;
                }
                break;
            case "control_cancel_request":
                if (_openRequests.Remove(Text(line, "request_id")) && _openRequests.Count == 0) Status(record, "busy", now);
                break;
            case "result":
                _openRequests.Clear();
                record.LastEvent = "Stop";
                Status(record, "idle", now);
                // A typed /model or /effort changes them with no answer of its own; the end of a turn is when to look.
                refresh = true;
                break;
        }
        return new Change(Signature(record) != before, _catalog != catalog, SettingsFrame() != settings, refresh);
    }

    /// <summary>The CLI's answer to <c>initialize</c>: the models and commands it offers, cut to what a picker and a menu show.</summary>
    void Introduced(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object) return;
        if (Text(answer, "current_permission_mode") is { Length: > 0 } mode) _mode = mode;
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "catalog");
            w.WriteStartArray("models");
            if (answer.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var m in models.EnumerateArray())
                {
                    if (Text(m, "value").Length == 0) continue;
                    w.WriteStartObject();
                    w.WriteString("value", Text(m, "value"));
                    w.WriteString("resolved", Text(m, "resolvedModel"));
                    w.WriteString("name", Text(m, "displayName"));
                    w.WriteString("description", Text(m, "description"));
                    w.WriteStartArray("efforts");
                    if (m.TryGetProperty("supportedEffortLevels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                        foreach (var level in levels.EnumerateArray())
                            if (level.ValueKind == JsonValueKind.String) w.WriteStringValue(level.GetString());
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteStartArray("commands");
            if (answer.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array)
                foreach (var c in commands.EnumerateArray())
                {
                    string name = Text(c, "name");
                    if (name.Length == 0 || name.StartsWith("__", StringComparison.Ordinal)) continue;
                    string description = Text(c, "description");
                    w.WriteStartObject();
                    w.WriteString("name", name);
                    w.WriteString("description", description.Length <= 240 ? description : description[..239] + "…");
                    w.WriteString("hint", Text(c, "argumentHint"));
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        _catalog = Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The CLI's answer to <c>get_settings</c>: the model and effort in force now.</summary>
    void InForce(JsonElement answer, HostRecord record)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("applied", out var applied)) return;
        if (Text(applied, "model") is { Length: > 0 } model) record.Model = _model = model;
        _effort = Text(applied, "effort");
    }

    /// <summary>The frame that tells a viewer what the CLI offers; empty until the CLI has said.</summary>
    public string CatalogFrame() => _catalog;

    /// <summary>The frame that tells a viewer the permission mode, model and effort in force.</summary>
    public string SettingsFrame() => JsonSerializer.Serialize(new { type = "settings", mode = _mode, model = _model, effort = _effort });

    /// <summary>The CLI's answer to one of the host's own questions: tens of kilobytes no viewer reads; viewers get the frames made from it.</summary>
    public static bool AnswersTheHost(JsonElement line) =>
        Text(line, "type") == "control_response" && line.TryGetProperty("response", out var response)
        && Text(response, "request_id") is InitializeId or SettingsId;

    public static string InitializeLine() => ControlLine(InitializeId, "initialize");

    public static string SettingsLine() => ControlLine(SettingsId, "get_settings");

    static string ControlLine(string id, string subtype) =>
        JsonSerializer.Serialize(new { type = "control_request", request_id = id, request = new { subtype } });

    static bool IsLocalCommandOutput(JsonElement userLine) =>
        userLine.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
        && content.ValueKind == JsonValueKind.String && (content.GetString() ?? "").TrimStart().StartsWith("<local-command-", StringComparison.Ordinal);

    /// <summary>
    /// The line without the bytes of any image in it. A pasted screenshot is megabytes of base64, and
    /// one of them would push the conversation out of the scrollback; what stays says an image was there.
    /// </summary>
    public static byte[] WithoutImageData(byte[] line)
    {
        if (line.AsSpan().IndexOf("\"base64\""u8) < 0) return line;
        System.Text.Json.Nodes.JsonNode? root;
        try { root = System.Text.Json.Nodes.JsonNode.Parse(line); }
        catch (JsonException) { return line; }
        if (root == null || !Strip(root)) return line;
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    static bool Strip(System.Text.Json.Nodes.JsonNode node)
    {
        bool changed = false;
        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array)
                if (item != null) changed |= Strip(item);
        }
        else if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            if (obj["type"] is System.Text.Json.Nodes.JsonValue type && type.TryGetValue<string>(out var kind) && kind == "image"
                && obj["source"] is System.Text.Json.Nodes.JsonObject source
                && source["data"] is System.Text.Json.Nodes.JsonValue data && data.TryGetValue<string>(out var bytes) && bytes.Length > 0)
            {
                source["omitted"] = bytes.Length;
                source["data"] = "";
                return true;
            }
            foreach (var pair in obj.ToList())
                if (pair.Value != null) changed |= Strip(pair.Value);
        }
        return changed;
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
            return WithoutImageData(buffer.WrittenSpan.ToArray());
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
