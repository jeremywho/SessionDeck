using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class DeckModelTests
{
    sealed class Tab
    {
        public HostRecord Host { get; }
        public Tab(HostRecord host) { Host = host; }
    }

    static HostRecord H(string id, string session = "") => new() { Id = id, SessionId = session.Length > 0 ? session : "s-" + id };

    static DeckModel<Tab> NewModel() => new(t => t.Host);

    /// <summary>Three columns as Jeremy arranges them: widths .5/.3/.2, a shown tab in each, focus on the middle one.</summary>
    static DeckLayout ThreeColumns() => new()
    {
        Focused = 1,
        Columns =
        {
            new DeckColumn { Hosts = { "a", "b", "c" }, Sessions = { "s-a", "s-b", "s-c" }, Active = "b", Fraction = 0.5 },
            new DeckColumn { Hosts = { "d", "e" }, Sessions = { "s-d", "s-e" }, Active = "e", Fraction = 0.3 },
            new DeckColumn { Hosts = { "f" }, Sessions = { "s-f" }, Active = "f", Fraction = 0.2 },
        },
    };

    static readonly string[] AllIds = { "a", "b", "c", "d", "e", "f" };

    static DeckModel<Tab> Arranged(out List<HostRecord> hosts)
    {
        hosts = AllIds.Select(id => H(id)).ToList();
        var m = NewModel();
        m.Restore(ThreeColumns(), hosts, h => new Tab(h));
        return m;
    }

    /// <summary>Columns as "a b* c | d e* | f*", then the focused column and the widths.</summary>
    static string Describe(DeckModel<Tab> m) =>
        string.Join(" | ", m.Groups.Select(g => string.Join(" ", g.Tabs.Select(t => t.Host.Id + (g.Active == t ? "*" : "")))))
        + $" @{m.Focused} " + string.Join("/", m.Groups.Select(g => g.Fraction.ToString("0.00")));

    const string Original = "a b* c | d e* | f* @1 0.50/0.30/0.20";

    static string Renamed(string layout, params string[] ids)
    {
        foreach (var id in ids) layout = System.Text.RegularExpressions.Regex.Replace(layout, $@"\b{id}\b", id + "2");
        return layout;
    }

    static void Restart(DeckModel<Tab> m, string id) => m.ReplaceHost(id, H(id + "2", "s-" + id), h => new Tab(h));

    [Fact]
    public void Restore_rebuilds_the_columns_as_saved()
    {
        Assert.Equal(Original, Describe(Arranged(out _)));
    }

    [Fact]
    public void Restarting_a_tab_in_every_column_keeps_each_where_it_was()
    {
        var m = Arranged(out _);
        Restart(m, "b");
        Restart(m, "e");
        Restart(m, "f");
        Assert.Equal(Renamed(Original, "b", "e", "f"), Describe(m));
    }

    [Fact]
    public void Restarting_the_last_tab_of_a_column_keeps_the_column()
    {
        var m = Arranged(out _);
        Restart(m, "f");
        Assert.Equal(Renamed(Original, "f"), Describe(m));
    }

    [Fact]
    public void Restarting_every_tab_of_a_column_keeps_the_column_its_order_and_its_shown_tab()
    {
        var m = Arranged(out _);
        Restart(m, "a");
        Restart(m, "b");
        Restart(m, "c");
        Assert.Equal(Renamed(Original, "a", "b", "c"), Describe(m));
    }

    /// <summary>
    /// Twelve restarts were fired at once on 2026-09-25 and finished in whatever order their hosts
    /// exited. Every completion order of every tab must leave the same layout.
    /// </summary>
    [Fact]
    public void Concurrent_restarts_finishing_in_any_order_leave_every_tab_in_its_slot()
    {
        foreach (var order in Permutations(AllIds))
        {
            var m = Arranged(out _);
            foreach (var id in order) Restart(m, id);
            Assert.Equal(Renamed(Original, AllIds), Describe(m));
        }
    }

    [Fact]
    public void A_restart_finishing_after_its_tab_was_moved_lands_where_the_tab_is_now()
    {
        var m = Arranged(out _);
        var a = m.FindByHost("a")!;
        m.MoveTo(a, m.Groups[2], 0);
        m.Focused = 0;
        string moved = Describe(m);
        Restart(m, "a");
        Restart(m, "d");
        Assert.Equal(Renamed(moved, "a", "d"), Describe(m));
    }

    [Fact]
    public void A_restarted_layout_saves_the_new_host_in_the_old_slot()
    {
        var m = Arranged(out _);
        Restart(m, "e");
        var saved = m.Snapshot(h => h.SessionId, _ => true);
        Assert.Equal(new[] { "d", "e2" }, saved.Columns[1].Hosts);
        Assert.Equal(new[] { "s-d", "s-e" }, saved.Columns[1].Sessions);
        Assert.Equal("e2", saved.Columns[1].Active);
        Assert.Equal(1, saved.Focused);
    }

    [Fact]
    public void Restarting_a_session_whose_tab_is_closed_opens_no_tab_and_keeps_its_way_back()
    {
        var m = Arranged(out _);
        m.Close(m.FindByHost("b")!);
        string closed = Describe(m);
        Assert.Null(m.ReplaceHost("b", H("b2", "s-b"), h => new Tab(h)));
        Assert.Equal(closed, Describe(m));
        Assert.True(m.IsClosed("b2"));
        m.Add(new Tab(H("b2", "s-b")), activate: false);
        Assert.Equal("b2", m.Groups[0].Tabs[1].Host.Id);
    }

    [Fact]
    public void A_closed_tab_reopens_in_its_column_at_its_index()
    {
        var m = Arranged(out _);
        var b = m.FindByHost("b")!;
        m.Close(b);
        m.Focused = 2;
        m.Add(b, activate: true);
        Assert.Equal("a b* c | d e* | f* @0 0.50/0.30/0.20", Describe(m));
    }

    [Fact]
    public void A_tab_whose_column_is_gone_reopens_in_the_focused_column()
    {
        var m = Arranged(out _);
        var f = m.FindByHost("f")!;
        m.Close(f);
        m.Focused = 0;
        m.Add(f, activate: false);
        Assert.Equal("a b* c f | d e* @0 0.50/0.50", Describe(m));
    }

    [Fact]
    public void Closed_tabs_stay_closed_across_an_app_restart()
    {
        var m = Arranged(out var hosts);
        m.Close(m.FindByHost("c")!);
        var saved = m.Snapshot(h => h.SessionId, _ => true);

        var again = NewModel();
        again.Restore(saved, hosts, h => new Tab(h));
        Assert.Equal("a b* | d e* | f* @1 0.50/0.30/0.20", Describe(again));
        Assert.True(again.IsClosed("c"));
    }

    [Fact]
    public void A_closed_tab_of_a_host_that_ended_is_not_saved()
    {
        var m = Arranged(out _);
        m.Close(m.FindByHost("c")!);
        var saved = m.Snapshot(h => h.SessionId, h => h.Id != "c");
        Assert.Empty(saved.ClosedHosts);
    }

    [Fact]
    public void An_app_restart_keeps_columns_order_shown_tabs_focus_and_widths()
    {
        var m = Arranged(out var hosts);
        var again = NewModel();
        again.Restore(m.Snapshot(h => h.SessionId, _ => true), hosts, h => new Tab(h));
        Assert.Equal(Original, Describe(again));
    }

    /// <summary>After a reboot every session comes back in a new host with a new id.</summary>
    [Fact]
    public void A_reboot_resume_puts_every_session_back_in_its_slot()
    {
        var m = Arranged(out _);
        m.Close(m.FindByHost("c")!);
        var saved = m.Snapshot(h => h.SessionId, _ => true);

        var resumed = AllIds.Reverse().Select(id => H(id + "2", "s-" + id)).ToList();
        var after = NewModel();
        after.Restore(saved, resumed, h => new Tab(h));
        Assert.Equal("a2 b2* | d2 e2* | f2* @1 0.50/0.30/0.20", Describe(after));
        Assert.True(after.IsClosed("c2"));
    }

    [Fact]
    public void A_reboot_where_one_session_did_not_come_back_keeps_the_rest_in_place()
    {
        var saved = Arranged(out _).Snapshot(h => h.SessionId, _ => true);
        var resumed = AllIds.Where(id => id != "f").Select(id => H(id + "2", "s-" + id)).ToList();
        var after = NewModel();
        after.Restore(saved, resumed, h => new Tab(h));
        Assert.Equal("a2 b2* c2 | d2 e2* @1 0.50/0.30", Describe(after));
    }

    [Fact]
    public void A_mix_of_surviving_and_resumed_hosts_all_find_their_slots()
    {
        var saved = Arranged(out _).Snapshot(h => h.SessionId, _ => true);
        var hosts = new List<HostRecord> { H("a"), H("b2", "s-b"), H("c"), H("d2", "s-d"), H("e"), H("f2", "s-f"), H("new") };
        var after = NewModel();
        after.Restore(saved, hosts, h => new Tab(h));
        Assert.Equal("a b2* c | d2 e* | f2* new @1 0.50/0.30/0.20", Describe(after));
    }

    [Fact]
    public void A_host_the_layout_names_by_id_is_never_taken_by_another_slot_with_its_session()
    {
        var layout = new DeckLayout
        {
            Columns =
            {
                new DeckColumn { Hosts = { "old" }, Sessions = { "s-x" }, Active = "old" },
                new DeckColumn { Hosts = { "x" }, Sessions = { "s-x" }, Active = "x" },
            },
        };
        var m = NewModel();
        m.Restore(layout, new[] { H("x", "s-x") }, h => new Tab(h));
        Assert.Equal("x* @0 1.00", Describe(m));
    }

    /// <summary>/resume inside a tab changes its session while its host stays; the saved layout must name the new one.</summary>
    [Fact]
    public void A_resume_inside_a_tab_is_saved_so_a_later_reboot_finds_the_slot()
    {
        var m = Arranged(out _);
        var saved = m.Snapshot(h => h.Id == "d" ? "s-resumed" : h.SessionId, _ => true);
        Assert.Equal("s-resumed", saved.Columns[1].Sessions[0]);

        var resumed = AllIds.Select(id => id == "d" ? H("d2", "s-resumed") : H(id + "2", "s-" + id)).ToList();
        var after = NewModel();
        after.Restore(saved, resumed, h => new Tab(h));
        Assert.Equal(Renamed(Original, AllIds), Describe(after));
    }

    [Fact]
    public void A_layout_saved_before_sessions_were_recorded_still_restores_by_host()
    {
        var old = new DeckLayout
        {
            Focused = 1,
            Columns =
            {
                new DeckColumn { Hosts = { "a", "b" }, Active = "a", Fraction = 0.6 },
                new DeckColumn { Hosts = { "c" }, Active = "c", Fraction = 0.4 },
            },
        };
        var m = NewModel();
        m.Restore(old, new[] { H("a"), H("b"), H("c") }, h => new Tab(h));
        Assert.Equal("a* b | c* @1 0.60/0.40", Describe(m));
    }

    static IEnumerable<string[]> Permutations(string[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (int i = 0; i < items.Length; i++)
            foreach (var rest in Permutations(items.Where((_, j) => j != i).ToArray()))
                yield return new[] { items[i] }.Concat(rest).ToArray();
    }
}
