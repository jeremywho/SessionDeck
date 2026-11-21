using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SessionDeck.Host;

/// <summary>A tool call a step made: its name and what it was given.</summary>
internal sealed record AgyCall(string Name, IReadOnlyDictionary<string, string> Args);

/// <summary>One step of a conversation as Antigravity writes it to its own transcript.</summary>
internal sealed record AgyStep(int Index, string Type, DateTime? At, string Content, string Thinking, IReadOnlyList<AgyCall> Calls)
{
    public const string UserInput = "USER_INPUT", Planner = "PLANNER_RESPONSE", Outcome = "GENERIC";

    /// <summary>The step a line of the transcript holds; null for anything that is not one.</summary>
    public static AgyStep? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") is not { Length: > 0 } type) return null;
            int index = root.TryGetProperty("step_index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out int n) ? n : -1;
            DateTime? at = DateTime.TryParse(Text(root, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : null;
            var calls = new List<AgyCall>();
            if (root.TryGetProperty("tool_calls", out var made) && made.ValueKind == JsonValueKind.Array)
                foreach (var call in made.EnumerateArray())
                {
                    if (Text(call, "name") is not { Length: > 0 } name) continue;
                    var args = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (call.ValueKind == JsonValueKind.Object && call.TryGetProperty("args", out var given) && given.ValueKind == JsonValueKind.Object)
                        foreach (var arg in given.EnumerateObject()) args[arg.Name] = Plain(arg.Value);
                    calls.Add(new AgyCall(name, args));
                }
            return new AgyStep(index, type, at, Text(root, "content"), Text(root, "thinking"), calls);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Antigravity writes an argument as JSON inside a string (<c>"\"echo hi\""</c>): the value itself is what is meant.</summary>
    static string Plain(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : value.GetRawText();
        string text = value.GetString() ?? "";
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"') return text;
        try { return JsonSerializer.Deserialize<string>(text) ?? text; }
        catch (JsonException) { return text; }
    }

    static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>A subagent a conversation started, as the file Antigravity keeps for it says.</summary>
internal sealed record AgySubagent(string Id, string Role, string TypeName, string State, int SpawnStep);

/// <summary>What an agent keeps on disk about a conversation, for what its protocol does not say.</summary>
internal interface IAgentDisk
{
    /// <summary>Every step of the conversation written so far; none when there is no transcript that can be read.</summary>
    IReadOnlyList<AgyStep> Steps(string conversation);
}

internal sealed class NoAgentDisk : IAgentDisk
{
    public IReadOnlyList<AgyStep> Steps(string conversation) => Array.Empty<AgyStep>();
}

/// <summary>
/// Antigravity's own record of a conversation, read and never written. Over the protocol it replays
/// a conversation with no times and says nothing of a subagent after starting it; on disk it keeps
/// both (measured, 1.5.13): <c>brain/&lt;conversation&gt;/.system_generated/logs/transcript.jsonl</c>
/// is one JSON object per step, appended as the step happens and never rewritten, with
/// <c>created_at</c> to the second; <c>subagents/&lt;id&gt;.json</c> names each subagent started, and
/// the subagent is a conversation with a transcript of its own. The format is the agent's and not a
/// promise: whatever does not read as expected is as if it were not there.
/// </summary>
internal sealed class AgyDisk : IAgentDisk
{
    /// <summary>A transcript longer than this is not read: it is held whole while it is.</summary>
    const long Most = 256L * 1024 * 1024;

    readonly string _root;

    public AgyDisk(string? root = null) =>
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "brain");

    /// <summary>Antigravity names a conversation with a UUID. Anything else is not made into a path.</summary>
    internal static bool IsConversation(string id) => Guid.TryParseExact(id, "D", out _);

    string Kept(string conversation) => Path.Combine(_root, conversation, ".system_generated");

    public IReadOnlyList<AgyStep> Steps(string conversation)
    {
        long from = 0;
        return Since(conversation, ref from);
    }

    /// <summary>
    /// The steps written after byte <paramref name="offset"/>, which moves past the whole lines read:
    /// a line still being written is left for the next look.
    /// </summary>
    public List<AgyStep> Since(string conversation, ref long offset)
    {
        var steps = new List<AgyStep>();
        if (!IsConversation(conversation)) return steps;
        try
        {
            using var file = new FileStream(Path.Combine(Kept(conversation), "logs", "transcript.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = file.Length;
            if (length <= offset || length - offset > Most) return steps;
            file.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[length - offset];
            file.ReadExactly(bytes);
            int start = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n') continue;
                if (AgyStep.Parse(bytes.AsSpan(start, i - start)) is { } step) steps.Add(step);
                start = i + 1;
            }
            offset += start;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return steps;
    }

    /// <summary>The subagents the conversation has started, in the order it started them.</summary>
    public List<AgySubagent> Subagents(string conversation)
    {
        var found = new List<AgySubagent>();
        if (!IsConversation(conversation)) return found;
        try
        {
            string folder = Path.Combine(Kept(conversation), "subagents");
            if (!Directory.Exists(folder)) return found;
            foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (file.Length is 0 or > 1024 * 1024) continue;
                    using var doc = JsonDocument.Parse(file);
                    if (Subagent(doc.RootElement) is { } one) found.Add(one);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        found.Sort((a, b) => a.SpawnStep != b.SpawnStep ? a.SpawnStep.CompareTo(b.SpawnStep) : string.CompareOrdinal(a.Id, b.Id));
        return found;
    }

    internal static AgySubagent? Subagent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("conversationId", out var id) || id.ValueKind != JsonValueKind.String || !IsConversation(id.GetString() ?? "")) return null;
        string Said(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        root.TryGetProperty("subagentDescriptor", out var descriptor);
        int spawn = root.TryGetProperty("spawnStepIndex", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int n) ? n : int.MaxValue;
        return new AgySubagent(id.GetString()!, Said(descriptor, "role"), Said(descriptor, "typeName"), Said(root, "state"), spawn);
    }
}
