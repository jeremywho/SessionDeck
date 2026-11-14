using System.IO;
using System.Text.Json;
using SessionDeck.Host;

namespace SessionDeck.Gateway;

/// <summary>
/// The sessions a phone ended, by host id: the gateway ends the host itself and writes the request
/// here, and the deck finishes the job on its next refresh (forgets the session so a reboot does not
/// bring it back, closes its tab, removes its row). The gateway is the only writer; the deck only
/// reads, so two processes never write one file.
/// </summary>
internal sealed class PhoneEndStore
{
    public const string FileName = "phone-ended.json";
    static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    public sealed record Ended(string SessionId, long AtMs);

    readonly string _path;
    readonly object _gate = new();
    readonly Dictionary<string, Ended> _ended;

    public PhoneEndStore(string path)
    {
        _path = path;
        _ended = Read(path);
    }

    public void Mark(string hostId, string sessionId, DateTime nowUtc)
    {
        long ms = ReadLedger.Ms(nowUtc);
        lock (_gate)
        {
            _ended[hostId] = new Ended(sessionId, ms);
            long cutoff = ReadLedger.Ms(nowUtc - Keep);
            foreach (var stale in _ended.Where(kv => kv.Value.AtMs < cutoff).Select(kv => kv.Key).ToList()) _ended.Remove(stale);
            AtomicFile.Write(_path, JsonSerializer.Serialize(_ended));
        }
    }

    /// <summary>A plain shared read for the deck: empty when there is no file or the gateway is mid-write.</summary>
    public static Dictionary<string, Ended> Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = JsonSerializer.Deserialize<Dictionary<string, Ended>>(fs);
            return read == null ? new(StringComparer.OrdinalIgnoreCase) : new(read, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>What the deck still has to do for one ended host.</summary>
    public sealed record Step(HostRecord Host, string SessionId, bool Kill, bool Remove);

    /// <summary>
    /// The deck's share of each end a phone asked for, against the hosts it has now. A host still
    /// running is ended again (the gateway's request may not have reached it); one that has stopped is
    /// removed. Its session is forgotten on every pass until then, since the deck remembers a session
    /// again for as long as it sees it running. Ends already carried out, and hosts the deck never
    /// had, are skipped.
    /// </summary>
    public static List<Step> Plan(IReadOnlyDictionary<string, Ended> ended, IEnumerable<HostRecord> hosts, ISet<string> done)
    {
        var steps = new List<Step>();
        foreach (var host in hosts)
        {
            if (done.Contains(host.Id) || !ended.TryGetValue(host.Id, out var end)) continue;
            string session = host.SessionId.Length > 0 ? host.SessionId : end.SessionId;
            steps.Add(new Step(host, session, Kill: !host.HasExited, Remove: host.HasExited));
        }
        return steps;
    }
}
