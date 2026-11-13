using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SessionDeck.Host;

/// <summary>
/// The conversations a chat session can switch to. The CLI's own <c>/resume</c> is a picker drawn in
/// a terminal and is refused on pipes ("isn't available in this environment"), so the host lists the
/// transcripts beside its own and starts the CLI again on the one picked. Every conversation in the
/// folder is listed, whichever kind of session wrote it: a transcript is the same either way
/// (measured, Claude Code 2.1.323: a terminal session's conversation resumed on pipes with its
/// context, also while that terminal session was still running).
/// </summary>
internal static class ChatSessions
{
    /// <summary>Stands for the session id in <see cref="HostSpec.ChatResumeCommand"/>.</summary>
    public const string SessionPlaceholder = "{SD_CHAT_SESSION}";

    /// <summary><paramref name="At"/> is when the transcript was last written, in Unix milliseconds; <paramref name="Live"/> says another process has the conversation open now.</summary>
    public sealed record Entry(string Id, string Title, long At, bool Live);

    const int HeadBytes = 256 * 1024, TailBytes = 128 * 1024, TitleLength = 140;

    /// <summary>A session id as the CLI writes one. Anything else never reaches a path or a command line.</summary>
    public static bool IsSessionId(string id) => id.Length == 36 && Guid.TryParseExact(id, "D", out _);

    /// <summary>The transcript of <paramref name="id"/> in <paramref name="projectDir"/>, or "" when the id is not one or there is no such file there.</summary>
    public static string TranscriptOf(string projectDir, string id)
    {
        if (projectDir.Length == 0 || !IsSessionId(id)) return "";
        try
        {
            string path = Path.Combine(projectDir, id + ".jsonl");
            return File.Exists(path) ? path : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }

    /// <summary>The command that starts the CLI on <paramref name="session"/>: the spawner's resume form, with the model the chat is on last, where it wins.</summary>
    public static string Command(string template, string session, string model) =>
        template.Replace(SessionPlaceholder, session) + (string.IsNullOrWhiteSpace(model) ? "" : $" --model \"{model.Trim()}\"");

    /// <summary>
    /// Conversations in the folder, newest first, without the one open here. A transcript nobody
    /// spoke in is left out: there is nothing in it to go back to.
    /// </summary>
    public static List<Entry> List(string projectDir, string current, IReadOnlySet<string> live, int max = 200)
    {
        var entries = new List<Entry>();
        if (projectDir.Length == 0) return entries;
        try
        {
            if (!Directory.Exists(projectDir)) return entries;
            var files = new DirectoryInfo(projectDir).EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                .Where(f => IsSessionId(Path.GetFileNameWithoutExtension(f.Name)))
                .OrderByDescending(f => f.LastWriteTimeUtc);
            foreach (var file in files)
            {
                string id = Path.GetFileNameWithoutExtension(file.Name);
                if (string.Equals(id, current, StringComparison.OrdinalIgnoreCase)) continue;
                string? title = Title(file.FullName);
                if (title == null) continue;
                entries.Add(new Entry(id, title, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(), live.Contains(id)));
                if (entries.Count >= max) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return entries;
    }

    /// <summary>
    /// What to call a conversation: the name it was given, else the name the CLI gave it, else what
    /// was first said in it ("" when that cannot be read). Null when nothing was said. Only the
    /// two ends of the file are read: a transcript can be hundreds of megabytes.
    /// </summary>
    internal static string? Title(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] head = new byte[(int)Math.Min(HeadBytes, file.Length)];
            file.ReadExactly(head);
            byte[] tail = head;
            if (file.Length > head.Length)
            {
                tail = new byte[(int)Math.Min(TailBytes, file.Length)];
                file.Position = file.Length - tail.Length;
                file.ReadExactly(tail);
            }

            string named = "", generated = "";
            foreach (var line in LinesOf(tail))
            {
                if (line.Span.IndexOf("\"custom-title\""u8) < 0 && line.Span.IndexOf("\"ai-title\""u8) < 0) continue;
                if (!TryParse(line, out var doc)) continue;
                using (doc)
                {
                    if (Text(doc.RootElement, "customTitle") is { Length: > 0 } custom) named = custom;
                    else if (Text(doc.RootElement, "aiTitle") is { Length: > 0 } ai) generated = ai;
                }
            }

            string? first = null;
            bool spoken = head.AsSpan().IndexOf("\"type\":\"user\""u8) >= 0;
            foreach (var line in LinesOf(head))
            {
                if (line.Span.IndexOf("\"type\":\"user\""u8) < 0 || !TryParse(line, out var doc)) continue;
                using (doc)
                {
                    if (Said(doc.RootElement) is not { Length: > 0 } said) continue;
                    first = said;
                    break;
                }
            }
            if (first == null && !spoken && named.Length == 0 && generated.Length == 0) return null;
            return OneLine(named.Length > 0 ? named : generated.Length > 0 ? generated : first ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>What a person typed in this line, or null: not a tool result, a note for the model or a subagent's exchange.</summary>
    static string? Said(JsonElement line)
    {
        if (Text(line, "type") != "user") return null;
        if (line.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
        if (line.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return null;
        if (!line.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return null;
        string text = "";
        if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
        else if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (Text(block, "type") == "tool_result") return null;
                if (Text(block, "type") == "text" && text.Length == 0) text = Text(block, "text");
            }
        text = text.Trim();
        return text.Length == 0 || text.StartsWith('<') ? null : text;
    }

    static string OneLine(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, TitleLength));
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length >= TitleLength) { sb.Append('…'); break; }
        }
        return sb.ToString();
    }

    /// <summary>Whole lines of a block cut from a file: the first and last may be fragments, which fail to parse and are skipped by the caller.</summary>
    static List<ReadOnlyMemory<byte>> LinesOf(byte[] block)
    {
        var lines = new List<ReadOnlyMemory<byte>>();
        int from = 0;
        for (int i = 0; i <= block.Length; i++)
        {
            if (i < block.Length && block[i] != (byte)'\n') continue;
            if (i > from) lines.Add(block.AsMemory(from, i - from));
            from = i + 1;
        }
        return lines;
    }

    static bool TryParse(ReadOnlyMemory<byte> line, out JsonDocument doc)
    {
        try { doc = JsonDocument.Parse(line); return true; }
        catch (JsonException) { doc = null!; return false; }
    }

    static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// Conversations some other running CLI has open, from the CLI's own registry of its processes
    /// (one file per process). Such a conversation can still be picked: the CLI resumes it, and the
    /// person is told it is open elsewhere, since two processes then write one transcript.
    /// </summary>
    public static HashSet<string> LiveElsewhere(string registryDir, int ownChildPid) =>
        LiveElsewhere(registryDir, ownChildPid, pid =>
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        });

    internal static HashSet<string> LiveElsewhere(string registryDir, int ownChildPid, Func<int, bool> alive)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (registryDir.Length == 0 || !Directory.Exists(registryDir)) return live;
            foreach (string file in Directory.EnumerateFiles(registryDir, "*.json"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(stream);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("pid", out var p) || !p.TryGetInt32(out int pid)) continue;
                    string session = Text(root, "sessionId");
                    if (session.Length > 0 && pid != ownChildPid && alive(pid)) live.Add(session);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return live;
    }

    /// <summary>The list as the frame a viewer asked for.</summary>
    public static byte[] ListFrame(string request, string current, List<Entry> entries)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "sessions");
            w.WriteString("request", request);
            w.WriteString("current", current);
            w.WriteStartArray("sessions");
            foreach (var e in entries)
            {
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                w.WriteString("title", e.Title);
                w.WriteNumber("at", e.At);
                w.WriteBoolean("live", e.Live);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The answer to a request to switch that was not carried out, and why.</summary>
    public static byte[] RefusedFrame(string request, string error) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type = "resumed", request, ok = false, error });
}
