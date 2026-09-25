using System.Collections.ObjectModel;
using SessionDeck.Host;

namespace SessionDeck;

/// <summary>A column: its tabs, the one it shows, and its share of the width.</summary>
internal sealed class DeckGroup<TTab> where TTab : class
{
    public ObservableCollection<TTab> Tabs { get; } = new();
    public TTab? Active { get; set; }
    public double Fraction { get; set; } = 1;
}

/// <summary>
/// The deck's columns with no view attached: which tab sits in which column at which index, which
/// tab each column shows, which column has focus, and which hosts have had their tab closed.
/// <see cref="DeckPane"/> draws it.
/// </summary>
internal sealed class DeckModel<TTab> where TTab : class
{
    sealed record ClosedSlot(HostRecord Host, DeckGroup<TTab>? Group, int Index);

    readonly Func<TTab, HostRecord> _host;
    readonly Dictionary<string, ClosedSlot> _closed = new();

    public DeckModel(Func<TTab, HostRecord> host) { _host = host; }

    public List<DeckGroup<TTab>> Groups { get; } = new();
    public int Focused { get; set; }

    public DeckGroup<TTab>? FocusedGroup => Groups.Count == 0 ? null : Groups[Math.Clamp(Focused, 0, Groups.Count - 1)];
    public DeckGroup<TTab>? GroupOf(TTab tab) => Groups.FirstOrDefault(g => g.Tabs.Contains(tab));
    public IEnumerable<TTab> AllTabs => Groups.SelectMany(g => g.Tabs);
    public TTab? FindByHost(string hostId) => AllTabs.FirstOrDefault(t => _host(t).Id == hostId);
    public bool IsClosed(string hostId) => _closed.ContainsKey(hostId);

    /// <summary>A tab closed earlier goes back to its column and index while that column exists; anything else goes into the focused column.</summary>
    public void Add(TTab tab, bool activate)
    {
        DeckGroup<TTab> group;
        if (_closed.Remove(_host(tab).Id, out var slot) && slot.Group != null && Groups.Contains(slot.Group))
        {
            group = slot.Group;
            group.Tabs.Insert(Math.Clamp(slot.Index, 0, group.Tabs.Count), tab);
        }
        else
        {
            if (Groups.Count == 0) Groups.Add(new DeckGroup<TTab>());
            group = FocusedGroup!;
            group.Tabs.Add(tab);
        }
        if (activate || group.Active == null) { group.Active = tab; Focused = Groups.IndexOf(group); }
    }

    public bool Activate(TTab tab)
    {
        var group = GroupOf(tab);
        if (group == null) return false;
        group.Active = tab;
        Focused = Groups.IndexOf(group);
        return true;
    }

    /// <summary>
    /// Move the tab into a new column inserted at <paramref name="insertAt"/>, taking half the width of
    /// the column at <paramref name="takeFrom"/>. A tab that was alone in its column just gets focus.
    /// </summary>
    public bool SplitAt(TTab tab, int insertAt, int takeFrom)
    {
        var from = GroupOf(tab);
        if (from == null) return false;
        var donor = takeFrom >= 0 && takeFrom < Groups.Count ? Groups[takeFrom] : from;
        if (from.Tabs.Count == 1 && donor == from) return Activate(tab);
        var to = new DeckGroup<TTab>();
        Groups.Insert(Math.Clamp(insertAt, 0, Groups.Count), to);
        Detach(tab, from);
        if (Groups.Contains(donor) && donor != from) { to.Fraction = donor.Fraction / 2; donor.Fraction /= 2; }
        else if (Groups.Contains(from)) { to.Fraction = from.Fraction / 2; from.Fraction /= 2; }
        else to.Fraction = from.Fraction;
        to.Tabs.Add(tab);
        to.Active = tab;
        Focused = Groups.IndexOf(to);
        return true;
    }

    public bool MoveTo(TTab tab, DeckGroup<TTab> to, int index)
    {
        var from = GroupOf(tab);
        if (from == null) return false;
        if (from == to)
        {
            int cur = to.Tabs.IndexOf(tab);
            index = Math.Clamp(index, 0, to.Tabs.Count - 1);
            if (cur != index) to.Tabs.Move(cur, index);
        }
        else
        {
            Detach(tab, from);
            to.Tabs.Insert(Math.Clamp(index, 0, to.Tabs.Count), tab);
        }
        to.Active = tab;
        Focused = Groups.IndexOf(to);
        return true;
    }

    /// <summary>Take the tab out and remember where it was, so reopening it puts it back.</summary>
    public bool Close(TTab tab)
    {
        var group = GroupOf(tab);
        if (group == null) return false;
        _closed[_host(tab).Id] = new ClosedSlot(_host(tab), group, group.Tabs.IndexOf(tab));
        Detach(tab, group);
        return true;
    }

    /// <summary>A column left empty goes away and its width joins a neighbour.</summary>
    void Detach(TTab tab, DeckGroup<TTab> from)
    {
        from.Tabs.Remove(tab);
        if (from.Active == tab) from.Active = from.Tabs.LastOrDefault();
        if (from.Tabs.Count == 0 && Groups.Count > 1)
        {
            int i = Groups.IndexOf(from);
            Groups.RemoveAt(i);
            Groups[Math.Max(0, i - 1)].Fraction += from.Fraction;
            if (Focused >= Groups.Count) Focused = Groups.Count - 1;
        }
    }

    /// <summary>
    /// The host <paramref name="oldHostId"/> was replaced by <paramref name="host"/> (a restart). Its tab
    /// is swapped in place: same column, same index, same shown tab, same focus. A host whose tab was
    /// closed stays closed, keeping its way back. Returns the swapped tabs, or null when no tab moved.
    /// </summary>
    public (TTab Old, TTab New)? ReplaceHost(string oldHostId, HostRecord host, Func<HostRecord, TTab> tabFor)
    {
        if (FindByHost(oldHostId) is not { } old)
        {
            if (_closed.Remove(oldHostId, out var slot)) _closed[host.Id] = slot with { Host = host };
            return null;
        }
        var group = GroupOf(old)!;
        var tab = tabFor(host);
        group.Tabs[group.Tabs.IndexOf(old)] = tab;
        if (group.Active == old) group.Active = tab;
        return (old, tab);
    }

    public TTab? CycleTarget(int delta)
    {
        var g = FocusedGroup;
        if (g == null || g.Tabs.Count == 0) return null;
        int idx = g.Active == null ? 0 : g.Tabs.IndexOf(g.Active);
        return g.Tabs[((idx + delta) % g.Tabs.Count + g.Tabs.Count) % g.Tabs.Count];
    }

    /// <summary>
    /// The columns for saving. Every slot, and every closed tab of a host still alive, also names its
    /// session, so a host that comes back under a new id (restart, reboot resume) finds its place.
    /// </summary>
    public DeckLayout Snapshot(Func<HostRecord, string> sessionOf, Func<HostRecord, bool> isLive)
    {
        var closed = _closed.Values.Select(c => c.Host).Where(isLive).ToList();
        return new DeckLayout
        {
            Focused = Focused,
            Columns = Groups.Select(g => new DeckColumn
            {
                Hosts = g.Tabs.Select(t => _host(t).Id).ToList(),
                Sessions = g.Tabs.Select(t => sessionOf(_host(t))).ToList(),
                Active = g.Active is { } a ? _host(a).Id : "",
                Fraction = g.Fraction,
            }).ToList(),
            ClosedHosts = closed.Select(h => h.Id).ToList(),
            ClosedSessions = closed.Select(sessionOf).ToList(),
        };
    }

    /// <summary>
    /// Rebuild the columns from a saved layout for the hosts alive now. A slot takes the host it names,
    /// or else a live host running the slot's session under another id. A host whose tab was closed
    /// stays closed. Hosts nothing claims go into the last column; columns left empty are dropped.
    /// </summary>
    public void Restore(DeckLayout layout, IReadOnlyCollection<HostRecord> hosts, Func<HostRecord, TTab> tabFor)
    {
        var byId = hosts.ToDictionary(h => h.Id);
        var named = new HashSet<string>(layout.Columns.SelectMany(c => c.Hosts).Concat(layout.ClosedHosts).Where(byId.ContainsKey));
        var bySession = hosts
            .Where(h => !named.Contains(h.Id) && h.SessionId.Length > 0)
            .GroupBy(h => h.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new Queue<HostRecord>(g), StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>();
        DeckGroup<TTab>? focused = null;
        Groups.Clear();
        _closed.Clear();
        for (int c = 0; c < layout.Columns.Count; c++)
        {
            var col = layout.Columns[c];
            var g = new DeckGroup<TTab> { Fraction = col.Fraction > 0 ? col.Fraction : 1 };
            for (int i = 0; i < col.Hosts.Count; i++)
            {
                var host = byId.GetValueOrDefault(col.Hosts[i]) ?? BySession(At(col.Sessions, i));
                if (host == null || !placed.Add(host.Id)) continue;
                var tab = tabFor(host);
                g.Tabs.Add(tab);
                if (col.Hosts[i] == col.Active) g.Active = tab;
            }
            if (g.Tabs.Count == 0) continue;
            g.Active ??= g.Tabs[^1];
            Groups.Add(g);
            if (c == layout.Focused) focused = g;
        }
        for (int i = 0; i < layout.ClosedHosts.Count; i++)
        {
            var host = byId.GetValueOrDefault(layout.ClosedHosts[i]) ?? BySession(At(layout.ClosedSessions, i));
            if (host != null && placed.Add(host.Id)) _closed[host.Id] = new ClosedSlot(host, null, 0);
        }
        var rest = hosts.Where(h => !placed.Contains(h.Id)).ToList();
        if (rest.Count > 0)
        {
            if (Groups.Count == 0) Groups.Add(new DeckGroup<TTab>());
            var last = Groups[^1];
            foreach (var host in rest) last.Tabs.Add(tabFor(host));
            last.Active ??= last.Tabs[^1];
        }
        Focused = focused != null ? Groups.IndexOf(focused) : Math.Clamp(layout.Focused, 0, Math.Max(0, Groups.Count - 1));

        HostRecord? BySession(string sessionId) =>
            sessionId.Length > 0 && bySession.TryGetValue(sessionId, out var q) && q.Count > 0 ? q.Dequeue() : null;
    }

    static string At(List<string> list, int i) => i < list.Count ? list[i] ?? "" : "";
}
