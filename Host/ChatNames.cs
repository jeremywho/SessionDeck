using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SessionDeck.Host;

/// <summary>
/// The names given to conversations whose agent has no way to keep one (the Agent Client Protocol
/// has no rename). Kept by conversation, not by host: a name stays with its conversation through a
/// restart, a reboot and a move to another conversation and back.
/// </summary>
internal interface IChatNames
{
    /// <summary>The name given to a conversation, or empty.</summary>
    string Of(string session);
    /// <summary>False when the name could not be kept.</summary>
    bool Set(string session, string name);
}

/// <summary>Names kept for as long as the process lives: what a translator has when it is given nowhere to keep them.</summary>
internal sealed class MemoryChatNames : IChatNames
{
    readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    public string Of(string session) => _names.GetValueOrDefault(session, "");
    public bool Set(string session, string name) { _names[session] = name; return true; }
}

/// <summary>One small file a conversation under <c>chat-names</c> in the data folder, named by a hash of the agent and the conversation's id (an id is the agent's to choose, and is not trusted as a file name).</summary>
internal sealed class ChatNames : IChatNames
{
    /// <summary>As long as a name gets, the limit the Claude CLI puts on its own.</summary>
    public const int Longest = 200;

    readonly string _dir, _provider;

    public ChatNames(string dataDir, string provider)
    {
        _dir = Path.Combine(dataDir, "chat-names");
        _provider = provider;
    }

    internal string PathOf(string session) =>
        Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_provider + "\n" + session)))[..32].ToLowerInvariant() + ".txt");

    public string Of(string session)
    {
        if (session.Length == 0) return "";
        try
        {
            string path = PathOf(session);
            return File.Exists(path) ? Clean(File.ReadAllText(path)) : "";
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    public bool Set(string session, string name)
    {
        if (session.Length == 0) return false;
        try
        {
            Directory.CreateDirectory(_dir);
            string path = PathOf(session), temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, name, new UTF8Encoding(false));
            File.Move(temp, path, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>A name as it is kept and shown: one line, runs of white space as one space, no longer than <see cref="Longest"/>.</summary>
    public static string Clean(string name)
    {
        var sb = new StringBuilder(Math.Min(name.Length, Longest));
        bool space = false;
        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
            // Half of a pair would be all that fits: leave it out.
            if (sb.Length + (space ? 1 : 0) + (char.IsHighSurrogate(c) ? 2 : 1) > Longest) break;
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
