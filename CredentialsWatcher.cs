using System.IO;
using System.Threading;

namespace SessionDeck;

/// <summary>
/// Watches Claude Code's credentials file and reports, once per burst, that it has been rewritten —
/// which in practice means the machine's signed-in account just changed.
///
/// <para>Why this exists: the usage bar polls every 15 minutes, so switching accounts left the pills
/// showing the *previous* account's numbers for up to a quarter of an hour while the address beside
/// them had already updated (that one is re-read off <c>~/.claude.json</c> on the 2s tick). Rather
/// than shorten the poll for everyone, the switch itself is the trigger.</para>
///
/// <para>Two details drive the shape of this class. The file is <b>replaced</b>, not edited — the
/// account switcher writes a temp file beside it and renames over the top — so the interesting event
/// can arrive as Renamed, Created, Changed or Deleted depending on who is doing the writing and how;
/// all four are treated the same. And the switcher patches <c>~/.claude.json</c> (account identity,
/// usage cache) a few milliseconds *after* the credentials file, so firing on the first event would
/// re-read the old identity — hence the debounce, which is a settling delay as much as a coalescer.
/// </para>
///
/// <para>The file itself is never written, only watched: see UsageApi.ReadAccessToken.</para>
/// </summary>
internal sealed class CredentialsWatcher : IDisposable
{
    /// <summary>
    /// How long to let the writes settle before reporting. Long enough for the sibling
    /// <c>~/.claude.json</c> patch to land, short enough to feel immediate.
    /// </summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1);

    readonly string _dir;
    readonly string _name;
    readonly TimeSpan _debounce;
    readonly Timer _timer;
    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    bool _pending;
    bool _disposed;

    /// <summary>
    /// Raised on a threadpool thread, once per burst. Subscribers touching UI must marshal — see
    /// SessionsWindow, which hands it straight to the dispatcher like the sessions-dir watcher does.
    /// </summary>
    public event Action? Changed;

    public CredentialsWatcher(string path, TimeSpan? debounce = null)
    {
        _dir = Path.GetDirectoryName(path) ?? "";
        _name = Path.GetFileName(path);
        _debounce = debounce ?? DefaultDebounce;
        _timer = new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Starts watching. Returns false if there is nothing to watch (no <c>~/.claude</c> yet, or the
    /// OS refused the handle) — a miss here costs the immediate refresh, nothing else, since the
    /// 15-minute poll is untouched.
    /// </summary>
    public bool Start()
    {
        try
        {
            if (_dir.Length == 0 || _name.Length == 0 || !Directory.Exists(_dir)) return false;

            // Filtered to the one filename: ~/.claude also holds settings.json and friends, and this
            // watcher must not react to those.
            _watcher = new FileSystemWatcher(_dir, _name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Poke();
            _watcher.Created += (_, _) => Poke();
            _watcher.Renamed += (_, _) => Poke();
            _watcher.Deleted += (_, _) => Poke();   // a sign-out is worth reflecting too
            return true;
        }
        catch { return false; }   // best-effort, exactly like the sessions-dir watcher
    }

    /// <summary>
    /// Records a change. The first one opens a window; everything arriving inside it is dropped,
    /// because the handler re-reads current state rather than replaying events — so N notifications
    /// and one notification call for the same work.
    /// </summary>
    public void Poke()
    {
        lock (_gate)
        {
            if (_disposed || _pending) return;
            _pending = true;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    void Fire()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = false;   // cleared first: a change during the handler opens the next window
        }
        try { Changed?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
