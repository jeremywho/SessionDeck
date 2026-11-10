using System.IO;
using System.Text.Json;

namespace SessionDeck.Gateway;

/// <summary>
/// When a phone last looked at each session, by session id (unix ms on this machine's clock). The
/// gateway is the only writer; the deck reads the file and folds it into its own read ledger, so a
/// session opened on the phone stops being unread on the desktop. The deck's ledger stays the deck's
/// to write: two processes never write one file.
/// </summary>
internal sealed class PhoneReadStore
{
    public const string FileName = "phone-read.json";
    static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    readonly string _path;
    readonly object _gate = new();
    readonly Dictionary<string, long> _readAt;

    public PhoneReadStore(string path)
    {
        _path = path;
        _readAt = Read(path);
    }

    /// <summary>A phone has the session in view now. Returns whether the record moved forward.</summary>
    public bool Mark(string sessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        long ms = ReadLedger.Ms(nowUtc);
        lock (_gate)
        {
            if (_readAt.TryGetValue(sessionId, out var seen) && seen >= ms) return false;
            _readAt[sessionId] = ms;
            long cutoff = ReadLedger.Ms(nowUtc - Keep);
            foreach (var stale in _readAt.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) _readAt.Remove(stale);
            AtomicFile.Write(_path, JsonSerializer.Serialize(_readAt));
            return true;
        }
    }

    public IReadOnlyDictionary<string, long> Snapshot()
    {
        lock (_gate) return new Dictionary<string, long>(_readAt, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A plain shared read for the deck: empty when there is no file or the gateway is mid-write.</summary>
    public static Dictionary<string, long> Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = JsonSerializer.Deserialize<Dictionary<string, long>>(fs);
            return read == null ? new(StringComparer.OrdinalIgnoreCase) : new(read, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }
}
