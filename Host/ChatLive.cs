using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SessionDeck.Host;

/// <summary>
/// What viewers attached right now have on screen that the scrollback does not hold: a message
/// still being said, and (for a translated agent) that it is at work. A viewer that attaches later
/// is given the scrollback; without this it would show less than one that had been watching, and
/// if what was being said never became a line of the conversation, it would show less for good.
/// <para>It follows the pane's own rules for a message being said (<c>ChatModel.live</c> and
/// <c>assistant</c> in chat.js), so that what it hands a new viewer is what the others show: a
/// message starts with <c>content_block_start</c>, grows with each delta, and is over when a kept
/// line of its kind arrives. A turn that ends with one still open (an interrupt) gets it as a kept
/// line first, so the scrollback is all there is to see. Every caller holds the lock the scrollback
/// is appended under, which makes the two one state.</para>
/// </summary>
internal sealed class ChatLive
{
    /// <summary>How a line that was interrupted is told, as the CLI says it and the pane reads it.</summary>
    const string Interrupted = "[Request interrupted by user";

    sealed class Draft
    {
        public string Kind = "";
        public readonly StringBuilder Text = new();
    }

    readonly List<Draft> _drafts = new();
    readonly bool _saysWork;
    Draft? _current;
    byte[]? _working;
    long _made;

    /// <param name="saysWork">The agent's translator says when work starts with nothing kept (a live <c>system init</c>); a CLI's own is not that.</param>
    public ChatLive(bool saysWork) => _saysWork = saysWork;

    /// <summary>Another conversation is served: nothing of the last one is being said.</summary>
    public void Reset()
    {
        _drafts.Clear();
        _current = null;
        _working = null;
    }

    /// <summary>A line only the viewers attached now are sent.</summary>
    public void Said(byte[] line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "parent_tool_use_id").Length > 0) return;
            string type = Text(root, "type");
            if (type == "system" && Text(root, "subtype") == "init") { if (_saysWork) _working = line; return; }
            if (type != "stream_event" || !root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object) return;
            switch (Text(ev, "type"))
            {
                case "message_start":
                    _drafts.Clear();
                    _current = null;
                    break;
                case "content_block_start":
                    _current = null;
                    string kind = ev.TryGetProperty("content_block", out var block) ? Text(block, "type") : "";
                    if (kind is "text" or "thinking") _drafts.Add(_current = new Draft { Kind = kind });
                    break;
                case "content_block_delta":
                    if (_current == null || !ev.TryGetProperty("delta", out var delta)) break;
                    _current.Text.Append(Text(delta, "type") switch { "text_delta" => Text(delta, "text"), "thinking_delta" => Text(delta, "thinking"), _ => "" });
                    break;
            }
        }
        catch (JsonException) { }
    }

    /// <summary>
    /// The lines to keep for a line about to be kept, in order: the line, and ahead of it what was
    /// still being said when it ends the turn.
    /// </summary>
    public List<byte[]> Keep(byte[] line, DateTime now)
    {
        var kept = new List<byte[]>(1);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = root.ValueKind == JsonValueKind.Object ? Text(root, "type") : "";
            bool top = root.ValueKind == JsonValueKind.Object && Text(root, "parent_tool_use_id").Length == 0;
            if (type == "assistant" && top && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    string kind = Text(block, "type");
                    int at = kind is "text" or "thinking" ? _drafts.FindIndex(d => d.Kind == kind) : -1;
                    if (at < 0) continue;
                    if (_current == _drafts[at]) _current = null;
                    _drafts.RemoveAt(at);
                }
            }
            else if (type == "result" || (type == "user" && top && Words(root).TrimStart().StartsWith(Interrupted, StringComparison.Ordinal)))
            {
                kept.AddRange(Ending(now));
                if (type == "result") _working = null;
            }
        }
        catch (JsonException) { }
        kept.Add(line);
        return kept;
    }

    /// <summary>What was being said, as lines to keep, when nothing more of it will come: the turn or the session is over.</summary>
    public List<byte[]> Ending(DateTime now)
    {
        var lines = new List<byte[]>();
        string at = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        foreach (var draft in _drafts)
        {
            string text = draft.Text.ToString();
            if (text.Trim().Length == 0) continue;
            object block = draft.Kind == "text" ? new { type = "text", text } : new { type = "thinking", thinking = text };
            lines.Add(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["type"] = "assistant", ["uuid"] = "cut-" + ++_made, ["partial"] = true,
                ["message"] = new { role = "assistant", content = new[] { block } }, ["parent_tool_use_id"] = null, ["timestamp"] = at,
            }));
        }
        _drafts.Clear();
        _current = null;
        return lines;
    }

    /// <summary>The live lines that bring a viewer attaching now to where the others are, after the scrollback.</summary>
    public List<byte[]> Now()
    {
        var lines = new List<byte[]>();
        if (_working != null) lines.Add(_working);
        foreach (var draft in _drafts)
        {
            lines.Add(Event(new { type = "content_block_start", content_block = new { type = draft.Kind } }));
            string text = draft.Text.ToString();
            if (text.Length == 0) continue;
            object delta = draft.Kind == "text" ? new { type = "text_delta", text } : new { type = "thinking_delta", thinking = text };
            lines.Add(Event(new { type = "content_block_delta", delta }));
        }
        // The pane adds to the message started last. When that is not the one still growing (a call is being
        // written out after it), a block of no kind it shows takes its place, as it did for the others.
        if (_drafts.Count > 0 && _current != _drafts[^1]) lines.Add(Event(new { type = "content_block_start", content_block = new { type = "other" } }));
        return lines;
    }

    static byte[] Event(object ev) => JsonSerializer.SerializeToUtf8Bytes(new { type = "stream_event", @event = ev, parent_tool_use_id = (string?)null });

    /// <summary>What a user line says: its content when that is words, else its first text.</summary>
    static string Words(JsonElement line)
    {
        if (!line.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        foreach (var block in content.EnumerateArray())
            if (Text(block, "type") == "text") return Text(block, "text");
        return "";
    }

    static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
