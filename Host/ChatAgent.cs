using System.Text.Json;

namespace SessionDeck.Host;

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
    string CatalogFrame();
    string SettingsFrame();
}
