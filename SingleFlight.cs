using System.Threading.Tasks;

namespace ClaudeSessionMonitor;

/// <summary>
/// Runs an async operation with at most one run in flight and at most one run queued behind it.
///
/// <para>The usage poll now has three callers — startup, the 15-minute timer, and an account switch —
/// and they can collide (switching accounts a few times in a row, or a switch landing on a tick).
/// Firing a second HTTP call on top of a running one buys nothing: the later result would win a race
/// with the earlier one, and both ask the same question. So overlapping requests collapse into a
/// single re-run once the current one finishes, which still guarantees the last request is answered
/// by a call that started after it.</para>
/// </summary>
internal sealed class SingleFlight
{
    readonly Func<Task> _work;
    readonly object _gate = new();
    bool _running;
    bool _queued;

    public SingleFlight(Func<Task> work) => _work = work;

    /// <summary>
    /// Requests a run. The returned task completes when the run this call started finishes; a request
    /// that merely queued behind a running one completes immediately, since it has no run of its own.
    /// </summary>
    public Task RunAsync()
    {
        lock (_gate)
        {
            if (_running) { _queued = true; return Task.CompletedTask; }
            _running = true;
        }
        return Loop();
    }

    async Task Loop()
    {
        while (true)
        {
            try { await _work(); }
            catch { }   // the work reports its own failures; an escape must not wedge the gate shut

            lock (_gate)
            {
                // Cleared inside the same lock that releases the gate, so a request arriving here is
                // either seen by this loop or starts a fresh one — never dropped between the two.
                if (!_queued) { _running = false; return; }
                _queued = false;
            }
        }
    }
}
