namespace SessionDeck.Host;

/// <summary>What a subagent has written since it was last looked at: the steps, and what its file says of it now.</summary>
internal sealed record AgySubagentNews(AgySubagent Subagent, List<AgyStep> Steps);

/// <summary>What the subagents of a conversation have done since the last look.</summary>
internal sealed record AgyNews(string Conversation, List<AgySubagentNews> Subagents);

/// <summary>
/// Watches what the subagents of the open Antigravity conversation write to disk, a look at a time.
/// The protocol says a subagent was started and nothing after that; the agent writes each of the
/// subagent's steps to the subagent's own transcript as it happens (measured, 1.5.13: a step is on
/// disk within a second of being made, whole, and never rewritten).
/// <para>A subagent's file says <c>ALIVE</c> long after its process is gone, so only the ones that
/// appear while this process is watching are followed: those already there at the first look at a
/// conversation belong to an earlier process and are left alone.</para>
/// </summary>
internal sealed class AgyWatch
{
    readonly AgyDisk _disk;
    string _conversation = "";
    readonly HashSet<string> _before = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _read = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _state = new(StringComparer.Ordinal);

    public AgyWatch(AgyDisk disk) => _disk = disk;

    /// <summary>What is new since the last look at this conversation; null when nothing is.</summary>
    public AgyNews? Look(string conversation)
    {
        if (!AgyDisk.IsConversation(conversation)) return null;
        var subagents = _disk.Subagents(conversation);
        if (conversation != _conversation)
        {
            _conversation = conversation;
            _before.Clear();
            _read.Clear();
            _state.Clear();
            foreach (var old in subagents) _before.Add(old.Id);
            return null;
        }
        var news = new List<AgySubagentNews>();
        foreach (var subagent in subagents)
        {
            if (_before.Contains(subagent.Id)) continue;
            bool known = _read.TryGetValue(subagent.Id, out long at);
            var steps = _disk.Since(subagent.Id, ref at);
            _read[subagent.Id] = at;
            bool changed = !_state.TryGetValue(subagent.Id, out var state) || state != subagent.State;
            _state[subagent.Id] = subagent.State;
            if (!known || changed || steps.Count > 0) news.Add(new AgySubagentNews(subagent, steps));
        }
        return news.Count > 0 ? new AgyNews(conversation, news) : null;
    }
}
