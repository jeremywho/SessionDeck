using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionDeck.Host;

/// <summary>
/// When the messages of a replayed Antigravity conversation were said. The replay has no times; the
/// transcript on disk has one per step. The two are the same conversation in the same order, told
/// differently (measured, 1.5.13): what the person said is a <c>USER_INPUT</c> step that wraps the
/// words in a request and appends the agent's own context; what the agent said is the
/// <c>content</c> of a <c>PLANNER_RESPONSE</c>; and the replay runs together what the agent said
/// in steps that had only a system message between them, which can be minutes apart (a timer's
/// reply after the message that set it).
/// <para>A wrong time is worse than none, so a line gets a time only when its words are found at
/// the next place they can be, the search never goes back, and nothing found leaves the line as it
/// was.</para>
/// </summary>
internal static class AgyTimes
{
    /// <summary>How many of the agent's later messages are looked at for one that the transcript skipped ahead of.</summary>
    const int Ahead = 6;
    /// <summary>The shortest words that are looked for inside longer ones, or further ahead: shorter ones are found everywhere.</summary>
    const int Telling = 16;

    public static List<byte[]> Stamp(IReadOnlyList<byte[]> lines, IReadOnlyList<AgyStep> steps)
    {
        var stamped = new List<byte[]>(lines.Count);
        int at = 0;
        var last = DateTime.MinValue;
        byte[] Timed(byte[] line, DateTime? when)
        {
            if (when is not { } time) return line;
            if (time < last) time = last;
            last = time;
            return ChatLines.Stamped(line, new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)));
        }
        foreach (var line in lines)
        {
            var (kind, words) = Said(line);
            string told = Plain(words);
            if (steps.Count == 0 || told.Length == 0) { stamped.Add(line); continue; }
            switch (kind)
            {
                case "user":
                {
                    int found = Asked(steps, at, told);
                    if (found < 0) { stamped.Add(line); break; }
                    stamped.Add(Timed(line, steps[found].At));
                    at = found + 1;
                    break;
                }
                case "thinking":
                {
                    int found = Thought(steps, at, told);
                    stamped.Add(found < 0 ? line : Timed(line, steps[found].At));
                    if (found >= 0) at = found;
                    break;
                }
                case "text":
                {
                    var parts = Spoken(steps, at, told, out bool whole);
                    if (parts.Count == 0) { stamped.Add(line); break; }
                    at = parts[^1] + 1;
                    var pieces = whole ? Apart(line, words, steps, parts) : null;
                    if (pieces == null) stamped.Add(Timed(line, steps[parts[0]].At));
                    else foreach (var (piece, step) in pieces) stamped.Add(Timed(piece, steps[step].At));
                    break;
                }
                default: stamped.Add(line); break;
            }
        }
        return stamped;
    }

    /// <summary>The step that holds what the person said: the next one that does, however many the replay left out.</summary>
    static int Asked(IReadOnlyList<AgyStep> steps, int from, string told)
    {
        for (int i = from; i < steps.Count; i++)
        {
            if (steps[i].Type != AgyStep.UserInput) continue;
            string asked = Plain(Request(steps[i].Content));
            if (asked == told || asked.StartsWith(told, StringComparison.Ordinal) || (told.Length >= Telling && asked.Contains(told, StringComparison.Ordinal))) return i;
        }
        return -1;
    }

    /// <summary>The words between the marks Antigravity puts around what the person typed; all of it when there are none.</summary>
    static string Request(string content)
    {
        const string open = "<USER_REQUEST>", close = "</USER_REQUEST>";
        int start = content.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return content;
        start += open.Length;
        int end = content.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? content[start..] : content[start..end];
    }

    /// <summary>The agent's step in this turn that thought these words.</summary>
    static int Thought(IReadOnlyList<AgyStep> steps, int from, string told)
    {
        for (int i = from; i < steps.Count && steps[i].Type != AgyStep.UserInput; i++)
        {
            string thought = Plain(steps[i].Thinking);
            if (thought.Length == 0) continue;
            if (thought == told || (Math.Min(thought.Length, told.Length) >= Telling && (thought.StartsWith(told, StringComparison.Ordinal) || told.StartsWith(thought, StringComparison.Ordinal)))) return i;
        }
        return -1;
    }

    /// <summary>
    /// The agent's steps in this turn whose words, one after the other, are what was said.
    /// <paramref name="whole"/> when they are all of it: only then is it known where one step's
    /// words end and the next one's begin.
    /// </summary>
    static List<int> Spoken(IReadOnlyList<AgyStep> steps, int from, string told, out bool whole)
    {
        var parts = new List<int>();
        whole = false;
        string rest = told;
        int looked = 0;
        for (int i = from; i < steps.Count && steps[i].Type != AgyStep.UserInput; i++)
        {
            if (steps[i].Type != AgyStep.Planner) continue;
            string said = Plain(steps[i].Content);
            if (said.Length == 0) continue;
            if (rest.StartsWith(said, StringComparison.Ordinal))
            {
                parts.Add(i);
                rest = rest[said.Length..].TrimStart();
                if (rest.Length == 0) { whole = true; break; }
                continue;
            }
            // What comes after the first words has to follow at once. Before them, a message the replay left out
            // may stand in the way, and is passed over only for words long enough to tell apart.
            if (parts.Count > 0 || told.Length < Telling || ++looked >= Ahead) break;
        }
        return parts;
    }

    /// <summary>
    /// The message as the separate ones it was, when something came between the steps that said it:
    /// null when it was one message, or when the places to cut cannot be found in its own text.
    /// </summary>
    static List<(byte[] Line, int Step)>? Apart(byte[] line, string words, IReadOnlyList<AgyStep> steps, List<int> parts)
    {
        var groups = new List<List<int>> { new() { parts[0] } };
        for (int i = 1; i < parts.Count; i++)
        {
            bool between = false;
            for (int s = parts[i - 1] + 1; s < parts[i]; s++) between |= steps[s].Type != AgyStep.Planner;
            if (between) groups.Add(new());
            groups[^1].Add(parts[i]);
        }
        if (groups.Count < 2) return null;
        var cuts = new List<int> { 0 };
        int search = 0;
        for (int g = 0; g < groups.Count; g++)
        {
            foreach (int step in groups[g])
            {
                string said = steps[step].Content.Trim();
                int found = words.IndexOf(said, search, StringComparison.Ordinal);
                if (found < 0) return null;
                if (g > 0 && step == groups[g][0]) cuts.Add(found);
                search = found + said.Length;
            }
        }
        JsonObject? whole;
        try { whole = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (whole == null || whole["message"]?["content"] is not JsonArray { Count: 1 } || whole["uuid"]?.GetValue<string>() is not { Length: > 0 } id) return null;
        var pieces = new List<(byte[], int)>();
        for (int g = 0; g < groups.Count; g++)
        {
            string text = words[cuts[g]..(g + 1 < cuts.Count ? cuts[g + 1] : words.Length)];
            var piece = (JsonObject)whole.DeepClone();
            piece["message"]!["content"]![0]!["text"] = g == 0 ? text.TrimEnd() : text.Trim();
            if (g > 0) piece["uuid"] = id + "." + g;
            pieces.Add((Encoding.UTF8.GetBytes(piece.ToJsonString()), groups[g][0]));
        }
        return pieces;
    }

    /// <summary>Who said a line and what: the person, or the agent thinking or speaking. Empty for a tool call, a result, or anything else.</summary>
    static (string Kind, string Words) Said(byte[] line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return ("", "");
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content)) return ("", "");
            bool user = type.GetString() == "user";
            if (!user && type.GetString() != "assistant") return ("", "");
            if (content.ValueKind == JsonValueKind.String) return user ? ("user", content.GetString() ?? "") : ("", "");
            if (content.ValueKind != JsonValueKind.Array) return ("", "");
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String) continue;
                string named = kind.GetString() ?? "";
                if (named == "text" && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) return (user ? "user" : "text", text.GetString() ?? "");
                if (!user && named == "thinking" && block.TryGetProperty("thinking", out var thought) && thought.ValueKind == JsonValueKind.String) return ("thinking", thought.GetString() ?? "");
            }
            return ("", "");
        }
        catch (JsonException) { return ("", ""); }
    }

    /// <summary>The words with every run of white space as one space: the two tellings break lines differently.</summary>
    static string Plain(string text)
    {
        var plain = new StringBuilder(text.Length);
        bool gap = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { gap = plain.Length > 0; continue; }
            if (gap) plain.Append(' ');
            gap = false;
            plain.Append(c);
        }
        return plain.ToString();
    }
}
