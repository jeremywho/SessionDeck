using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace SessionDeck.Host;

/// <summary>
/// When a line of the conversation was said, written on it as Claude writes it on its own lines
/// (<c>timestamp</c>, ISO 8601 in UTC), so the pane reads one field whatever the agent.
/// </summary>
internal static class ChatLines
{
    /// <summary>The line with its time on it, when it is a message (user or assistant) that says none; any other line as given.</summary>
    internal static byte[] Stamped(byte[] line, DateTimeOffset at) => Stamp(line, at, evenHistory: true);

    /// <summary>
    /// The same for a line said now, by the host's clock. A line read back from an earlier
    /// conversation is left as it is: when it was said is not known, and it was not now.
    /// </summary>
    internal static byte[] StampedNow(byte[] line, DateTime now) => Stamp(line, new DateTimeOffset(now.ToUniversalTime()), evenHistory: false);

    static byte[] Stamp(byte[] line, DateTimeOffset at, bool evenHistory)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return line;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() is not ("user" or "assistant")) return line;
            if (root.TryGetProperty("timestamp", out _)) return line;
            if (!evenHistory && root.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.True) return line;
            var buffer = new ArrayBufferWriter<byte>(line.Length + 48);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                foreach (var p in root.EnumerateObject()) p.WriteTo(w);
                w.WriteString("timestamp", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }
        catch (JsonException) { return line; }
    }
}

/// <summary>What a chat translator asks the host to do with something it was told.</summary>
internal enum ChatEffectKind
{
    /// <summary>A line to write to the agent's process.</summary>
    ToCli,
    /// <summary>A line of the conversation: into the scrollback and to every viewer.</summary>
    Durable,
    /// <summary>A line only the viewers attached now get (token deltas, the start of a turn).</summary>
    Live,
    /// <summary>A text frame for every viewer (the catalog, the settings, the title).</summary>
    Frame,
    /// <summary>A text frame for the one viewer named by <see cref="ChatEffect.Tag"/>.</summary>
    Reply,
    /// <summary>The host now serves another conversation: empty the scrollback and fill it with <see cref="ChatEffect.Seed"/>.</summary>
    Reset,
}

/// <summary>What the scrollback is started with when the conversation changes.</summary>
internal sealed record ChatSeed(string Session, string Title, string Path, List<byte[]> Lines, bool Earlier);

internal readonly record struct ChatEffect(ChatEffectKind Kind, byte[] Data, string Tag = "", ChatSeed? Seed = null);

/// <summary>
/// An agent that has no stream-json mode of its own, spoken to in whatever it does have and shown
/// in the one chat pane: a translator between its protocol and the lines a chat viewer reads from
/// Claude. Pure: it is told things and answers with effects, and the host carries them out
/// (<c>HostProgram.Agent</c>). <see cref="CodexChat"/> is Codex's app server; <see cref="AcpChat"/>
/// is any agent that speaks the Agent Client Protocol.
/// </summary>
internal interface IChatAgent
{
    /// <summary>The first thing said to the agent's process.</summary>
    List<ChatEffect> Start();
    /// <summary>One line the agent's process said.</summary>
    List<ChatEffect> FromCli(JsonElement message, HostRecord record, DateTime now);
    /// <summary>One line a viewer typed or clicked.</summary>
    List<ChatEffect> FromViewer(JsonElement line, HostRecord record, DateTime now);
    /// <summary>The conversation before the message a viewer names as its oldest.</summary>
    List<ChatEffect> History(string request, string uuid, string tag);
    /// <summary>The conversations there are to move to.</summary>
    List<ChatEffect> Sessions(string request, string tag);
    /// <summary>Move to another conversation.</summary>
    List<ChatEffect> Resume(string request, string session, string tag);
    /// <summary>Read the agent's commands and skills from disk again, where the agent can; refused to the asking viewer where it cannot.</summary>
    List<ChatEffect> Commands(string request, string tag);
    /// <summary>Name the conversation, without a turn: the agent is asked where it keeps names, and the name is kept here where it does not.</summary>
    List<ChatEffect> Rename(string request, string name, string tag, HostRecord record);
    string CatalogFrame();
    string SettingsFrame();
}
