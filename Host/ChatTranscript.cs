using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SessionDeck.Host;

/// <summary>
/// The CLI's own transcript read as conversation, newest first: what a chat host shows of the time
/// before it started, and what a viewer pages back through. A transcript is appended to for as long
/// as the session lives and can be very large, so it is read from the end in pieces and never whole.
/// </summary>
internal static class ChatTranscript
{
    const int Chunk = 256 * 1024;

    /// <summary>Lines oldest first, each carrying <c>at</c>, its place in the file; <see cref="Next"/> is the place to ask before for the page ahead of this one, or -1 when the file starts here.</summary>
    public sealed record Page(List<byte[]> Lines, long Next);

    static readonly Page Nothing = new(new List<byte[]>(), -1);

    /// <summary>
    /// The conversation lines that end before byte <paramref name="before"/>, as many as fit in
    /// <paramref name="maxBytes"/> and <paramref name="maxLines"/> and never fewer than one when one
    /// is there: a line larger than the whole budget is still a line someone asked to see.
    /// </summary>
    public static Page Before(string path, long before, int maxBytes, int maxLines)
    {
        if (path.Length == 0) return Nothing;
        try
        {
            if (!File.Exists(path)) return Nothing;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long end = Math.Min(before, file.Length);
            var newestFirst = new List<byte[]>();
            int used = 0;
            long next = -1;
            // `tail` is the start of a line whose earlier bytes have not been read yet.
            byte[] tail = Array.Empty<byte>();
            long position = end;
            while (position > 0)
            {
                long from = Math.Max(0, position - Chunk);
                var block = new byte[(int)(position - from) + tail.Length];
                file.Position = from;
                file.ReadExactly(block, 0, (int)(position - from));
                tail.CopyTo(block, (int)(position - from));
                position = from;

                int stop = block.Length;
                // A file that does not end in a newline ends in a line still being written: it is not read.
                if (from + block.Length == end && end == file.Length && stop > 0 && block[stop - 1] != (byte)'\n')
                {
                    int cut = Array.LastIndexOf(block, (byte)'\n', stop - 1);
                    if (cut < 0) { tail = block; continue; }
                    stop = cut + 1;
                }
                while (stop > 0)
                {
                    int lineEnd = block[stop - 1] == (byte)'\n' ? stop - 1 : stop;
                    int start = lineEnd == 0 ? 0 : Array.LastIndexOf(block, (byte)'\n', lineEnd - 1) + 1;
                    if (start == 0 && from > 0) break;   // its beginning is in the block before this one
                    long at = from + start;
                    if (Line(block.AsSpan(start, lineEnd - start), at) is { } line)
                    {
                        if (newestFirst.Count > 0 && (used + line.Length + 1 > maxBytes || newestFirst.Count >= maxLines))
                        {
                            newestFirst.Reverse();
                            return new Page(newestFirst, next);
                        }
                        newestFirst.Add(line);
                        used += line.Length + 1;
                        next = at;
                    }
                    stop = start;
                }
                tail = block.AsSpan(0, stop).ToArray();
            }
            newestFirst.Reverse();
            return new Page(newestFirst, -1);
        }
        catch (IOException) { return Nothing; }
        catch (UnauthorizedAccessException) { return Nothing; }
    }

    /// <summary>Where the line with this uuid starts, or -1. A viewer whose oldest message came live knows its uuid and not its place in the file.</summary>
    public static long Find(string path, string uuid)
    {
        if (path.Length == 0 || uuid.Length == 0 || uuid.Length > 64 || uuid.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return -1;
        try
        {
            if (!File.Exists(path)) return -1;
            byte[] needle = Encoding.ASCII.GetBytes("\"uuid\":\"" + uuid + "\"");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long position = file.Length;
            byte[] overlap = Array.Empty<byte>();
            while (position > 0)
            {
                long from = Math.Max(0, position - Chunk);
                var block = new byte[(int)(position - from) + overlap.Length];
                file.Position = from;
                file.ReadExactly(block, 0, (int)(position - from));
                overlap.CopyTo(block, (int)(position - from));
                for (int hit = block.AsSpan().LastIndexOf(needle); hit >= 0; hit = hit == 0 ? -1 : block.AsSpan(0, hit).LastIndexOf(needle))
                {
                    long start = LineStart(file, from + hit);
                    if (IsLineOf(file, start, uuid)) return start;
                }
                overlap = block.AsSpan(0, Math.Min(needle.Length - 1, block.Length)).ToArray();
                position = from;
            }
            return -1;
        }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }

    static long LineStart(FileStream file, long within)
    {
        var buf = new byte[4096];
        long position = within;
        while (position > 0)
        {
            long from = Math.Max(0, position - buf.Length);
            file.Position = from;
            int n = (int)(position - from);
            file.ReadExactly(buf, 0, n);
            int nl = Array.LastIndexOf(buf, (byte)'\n', n - 1);
            if (nl >= 0) return from + nl + 1;
            position = from;
        }
        return 0;
    }

    /// <summary>The uuid can also sit inside another line (a message quoting one): only the line whose own uuid it is counts.</summary>
    static bool IsLineOf(FileStream file, long start, string uuid)
    {
        file.Position = start;
        using var line = new MemoryStream();
        var buf = new byte[65536];
        while (true)
        {
            int n = file.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            int nl = Array.IndexOf(buf, (byte)'\n', 0, n);
            line.Write(buf, 0, nl < 0 ? n : nl);
            if (nl >= 0) break;
        }
        try
        {
            using var doc = JsonDocument.Parse(line.GetBuffer().AsMemory(0, (int)line.Length));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("uuid", out var own)
                && own.ValueKind == JsonValueKind.String && string.Equals(own.GetString(), uuid, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// One transcript line in the shape the live stream uses, or null when it is not part of the
    /// conversation a person sees: bookkeeping, a subagent's own exchange, text meant for the model.
    /// </summary>
    internal static byte[]? Line(ReadOnlySpan<byte> transcriptLine, long at)
    {
        while (transcriptLine.Length > 0 && (transcriptLine[^1] == (byte)'\r' || transcriptLine[^1] == (byte)' ')) transcriptLine = transcriptLine[..^1];
        if (transcriptLine.Length < 2) return null;
        try
        {
            var reader = new Utf8JsonReader(transcriptLine);
            using var doc = JsonDocument.ParseValue(ref reader);
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
                if (at >= 0) w.WriteNumber("at", at);
                if (Text(root, "uuid") is { Length: > 0 } uuid) w.WriteString("uuid", uuid);
                if (True(root, "isCompactSummary")) w.WriteBoolean("compactSummary", true);
                if (Text(root, "timestamp") is { Length: > 0 } when) w.WriteString("timestamp", when);
                w.WritePropertyName("message");
                message.WriteTo(w);
                w.WriteEndObject();
            }
            return ChatStream.WithoutImageData(buffer.WrittenSpan.ToArray());
        }
        catch (JsonException) { return null; }
    }

    static bool True(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
