namespace SessionDeck.Gateway;

/// <summary>
/// Rebuilds the catalog on the deck's own cadence while a phone is listening and slowly otherwise,
/// and announces only a list that differs from the one last announced.
/// </summary>
internal sealed class CatalogLoop
{
    readonly Func<List<CatalogEntry>> _build;
    readonly Action<List<CatalogEntry>> _changed;
    readonly Func<int> _subscribers;
    readonly TimeSpan _busy;
    readonly TimeSpan _idle;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    string _lastJson = "";

    public CatalogLoop(Func<List<CatalogEntry>> build, Action<List<CatalogEntry>> changed, Func<int> subscribers, TimeSpan? busy = null, TimeSpan? idle = null,
                       Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _build = build;
        _changed = changed;
        _subscribers = subscribers;
        _busy = busy ?? TimeSpan.FromSeconds(5);
        _idle = idle ?? TimeSpan.FromSeconds(30);
        _delay = delay ?? Task.Delay;
    }

    public List<CatalogEntry> Current { get; private set; } = new();

    /// <summary>One rebuild now; true when the list changed.</summary>
    public bool Tick()
    {
        var entries = _build();
        Current = entries;
        string json = SessionCatalog.ToJson(entries);
        if (json == _lastJson) return false;
        _lastJson = json;
        _changed(entries);
        return true;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { App.LogError(ex); }
            try { await _delay(_subscribers() > 0 ? _busy : _idle, ct); }
            catch (OperationCanceledException) { }
        }
    }
}
