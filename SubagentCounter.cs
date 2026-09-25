using System.IO;
using System.Text;
using System.Text.Json;

namespace SessionDeck;

/// <summary>
/// Incremental count/activity cache for Claude's append-only subagent transcripts. A full directory
/// enumeration discovers new/deleted agents; between enumerations only recently active files are
/// stat'd, while cached timestamps are enough to let inactive badges age out.
/// <para>An agent is "active" if its transcript was written within <see cref="ActiveWindow"/>, or if
/// its last entry says the agent is mid-turn: a tool call awaiting its result, a tool result waiting
/// to go back to the model, or a reply still streaming. A transcript is written only between API
/// turns, so an agent inside a long tool call goes quiet for minutes while still running; the
/// timestamp alone would call it finished. <see cref="LongRunCap"/> bounds how long a mid-turn tail
/// keeps an agent active, for transcripts abandoned by a killed process.</para>
/// </summary>
internal sealed class SubagentCounter
{
    internal static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan LongRunCap = TimeSpan.FromHours(2);
    static readonly TimeSpan HotGrace = TimeSpan.FromSeconds(10);
    const int TailBytes = 64 * 1024;

    sealed class FileState
    {
        public DateTime Written;
        public long Length;
        public bool MidTurn;
    }

    sealed class Entry
    {
        public long DirectoryMtime;
        public DateTime ReconcileAt;
        public Dictionary<string, FileState> Files = new(StringComparer.OrdinalIgnoreCase);
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
                var state = entry.Files[path];
                if (!state.MidTurn && nowUtc - state.Written > ActiveWindow + HotGrace) continue;
                try
                {
                    var file = new FileInfo(path);
                    if (file.Exists) Refresh(state, file);
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
        var files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in dir.EnumerateFiles("agent-*.jsonl"))
            {
                try
                {
                    var state = previous != null && previous.Files.TryGetValue(file.FullName, out var known) ? known : new FileState();
                    Refresh(state, file);
                    files[file.FullName] = state;
                }
                catch { }
            }
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

    /// <summary>Re-read the tail only when the file has actually changed; that is the expensive part.</summary>
    static void Refresh(FileState state, FileInfo file)
    {
        var written = file.LastWriteTimeUtc;
        long length = file.Length;
        if (written == state.Written && length == state.Length) return;
        state.Written = written;
        state.Length = length;
        state.MidTurn = IsMidTurn(file.FullName);
    }

    (int Total, int Active) Cached(string directory, DateTime nowUtc) =>
        _entries.TryGetValue(directory, out var entry) ? Result(entry, nowUtc) : (0, 0);

    static (int Total, int Active) Result(Entry entry, DateTime nowUtc)
    {
        int active = 0;
        foreach (var state in entry.Files.Values)
        {
            var age = nowUtc - state.Written;
            if (age < ActiveWindow || (state.MidTurn && age < LongRunCap)) active++;
        }
        return (entry.Files.Count, active);
    }

    /// <summary>
    /// Whether the transcript's last conversation entry leaves the agent mid-turn. Finished means the
    /// last entry is an assistant message that stopped for good (end_turn, max_tokens, stop_sequence).
    /// Anything else that carries a role — a tool result, a tool call, a streaming reply with no stop
    /// reason — means the agent is still going. A tail with no readable entry is treated as finished,
    /// so the timestamp rule alone decides.
    /// </summary>
    internal static bool IsMidTurn(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            int read = 0;
            while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            var lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0 || line[0] != '{') continue;
                if (i == 0 && start > 0) break;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    string type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                    if (type == "user") return true;
                    if (type != "assistant") continue;
                    string stop = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                                  && m.TryGetProperty("stop_reason", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                    return stop is not ("end_turn" or "max_tokens" or "stop_sequence" or "refusal");
                }
            }
        }
        catch { }
        return false;
    }
}
