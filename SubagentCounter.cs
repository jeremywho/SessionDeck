using System.IO;

namespace SessionDeck;

/// <summary>
/// Incremental count/activity cache for Claude's append-only subagent transcripts. A full directory
/// enumeration discovers new/deleted agents; between enumerations only recently active files are
/// stat'd, while cached timestamps are enough to let inactive badges age out.
/// </summary>
internal sealed class SubagentCounter
{
    internal static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan HotGrace = TimeSpan.FromSeconds(10);

    sealed class Entry
    {
        public long DirectoryMtime;
        public DateTime ReconcileAt;
        public Dictionary<string, DateTime> Files = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public (int Total, int Active) Count(string directory, DateTime nowUtc)
    {
        var dir = new DirectoryInfo(directory);
        if (!dir.Exists) { _entries.Remove(directory); return (0, 0); }

        long directoryMtime;
        try { directoryMtime = dir.LastWriteTimeUtc.Ticks; }
        catch { return Cached(directory, nowUtc); }

        if (!_entries.TryGetValue(directory, out var entry) ||
            entry.DirectoryMtime != directoryMtime || nowUtc >= entry.ReconcileAt)
        {
            entry = Reconcile(dir, directoryMtime, nowUtc, entry);
            _entries[directory] = entry;
        }
        else
        {
            // Active writers are the only files whose timestamp can affect today's result. Keep
            // probing them for a short grace period after they age out; settled history is untouched.
            foreach (var path in entry.Files.Keys.ToArray())
            {
                if (nowUtc - entry.Files[path] > ActiveWindow + HotGrace) continue;
                try
                {
                    var file = new FileInfo(path);
                    if (file.Exists) entry.Files[path] = file.LastWriteTimeUtc;
                }
                catch { }
            }
        }

        return Result(entry, nowUtc);
    }

    public void Retain(IReadOnlySet<string> liveDirectories)
    {
        foreach (var directory in _entries.Keys.ToArray())
            if (!liveDirectories.Contains(directory)) _entries.Remove(directory);
    }

    Entry Reconcile(DirectoryInfo dir, long directoryMtime, DateTime nowUtc, Entry? previous)
    {
        var files = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // DirectoryInfo supplies the find-data metadata from the enumeration, avoiding the old
            // pattern of a separate File.GetLastWriteTimeUtc call for every historical file.
            foreach (var file in dir.EnumerateFiles("agent-*.jsonl"))
                try { files[file.FullName] = file.LastWriteTimeUtc; } catch { }
        }
        catch
        {
            if (previous != null) return previous;
        }

        return new Entry
        {
            DirectoryMtime = directoryMtime,
            ReconcileAt = nowUtc + ReconcileInterval,
            Files = files,
        };
    }

    (int Total, int Active) Cached(string directory, DateTime nowUtc) =>
        _entries.TryGetValue(directory, out var entry) ? Result(entry, nowUtc) : (0, 0);

    static (int Total, int Active) Result(Entry entry, DateTime nowUtc)
    {
        int active = 0;
        foreach (var written in entry.Files.Values)
            if (nowUtc - written < ActiveWindow) active++;
        return (entry.Files.Count, active);
    }
}
