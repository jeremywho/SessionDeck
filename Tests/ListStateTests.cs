using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>What Jeremy arranged in the session list (groups and where each row sits in its group) across host replacements.</summary>
public class ListStateTests
{
    static readonly DateTime Day = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Local);
    static DateTime At(int h, int m, int s = 0) => Day.AddHours(h).AddMinutes(m).AddSeconds(s);

    static HostRecord Host(string id, string session, DateTime startedLocal, string lastEvent = "") =>
        new() { Id = id, SessionId = session, StartedAt = startedLocal.ToUniversalTime(), LastEvent = lastEvent };

    static SessionInfo Idle(string session, DateTime statusAt) =>
        new() { SessionId = session, Status = "idle", StatusUpdatedAt = statusAt, UpdatedAt = statusAt, Kind = "interactive", Name = session };

    static SessionRow Row(HostRecord host, SessionInfo info) => new(host, info);

    /// <summary>The grid's own sort: group, then state, then most recently changed, then name.</summary>
    static List<string> Order(IEnumerable<SessionRow> rows) => rows
        .OrderBy(r => r.GroupOrder).ThenBy(r => r.SortPriority).ThenByDescending(r => r.LastChanged).ThenBy(r => r.Name)
        .Select(r => r.LiveSessionId).ToList();

    static List<SessionGroup> Groups(params (string Name, string[] Members)[] groups) =>
        groups.Select(g => new SessionGroup { Name = g.Name, Members = g.Members.ToList() }).ToList();

    [Fact]
    public void A_restart_keeps_each_row_in_its_place_within_its_group()
    {
        var groups = Groups(("bz", new[] { "s1", "s2", "s3" }));
        var r1 = Row(Host("h1", "s1", At(9, 0)), Idle("s1", At(16, 40)));
        var r2 = Row(Host("h2", "s2", At(9, 0)), Idle("s2", At(16, 30)));
        var r3 = Row(Host("h3", "s3", At(9, 0)), Idle("s3", At(16, 20)));
        SessionGroup.Apply(groups, new[] { r1, r2, r3 });
        Assert.Equal(new[] { "s1", "s2", "s3" }, Order(new[] { r1, r2, r3 }));

        // 16:59:35: the CLI updated; s3 then s1 come back in new hosts and report their startup as a status change.
        var n3 = Row(Host("h3b", "s3", At(16, 59, 36), "SessionStart"), Idle("s3", At(16, 59, 40)));
        var n1 = Row(Host("h1b", "s1", At(16, 59, 36), "SessionStart"), Idle("s1", At(16, 59, 45)));
        n3.Inherit(r3.GroupedAs, r3.LastChanged);
        n1.Inherit(r1.GroupedAs, r1.LastChanged);
        SessionGroup.Apply(groups, new[] { n1, r2, n3 });

        Assert.Equal(new[] { "s1", "s2", "s3" }, Order(new[] { n3, r2, n1 }));
        Assert.All(new[] { n1, r2, n3 }, r => Assert.Equal("bz", r.GroupName));
    }

    [Fact]
    public void A_restarted_row_takes_its_own_time_once_it_takes_a_turn()
    {
        var old = Row(Host("h1", "s1", At(9, 0)), Idle("s1", At(16, 40)));
        var row = Row(Host("h1b", "s1", At(16, 59, 36), "SessionStart"), Idle("s1", At(16, 59, 45)));
        row.Inherit(old.GroupedAs, old.LastChanged);
        Assert.Equal(At(16, 40), row.LastChanged);

        row.Update(Idle("s1", At(17, 0)), Host("h1b", "s1", At(16, 59, 36), "Notification"));
        Assert.Equal(At(16, 40), row.LastChanged);

        row.Update(Idle("s1", At(17, 5)), Host("h1b", "s1", At(16, 59, 36), "Stop"));
        Assert.Equal(At(17, 5), row.LastChanged);
    }

    [Fact]
    public void A_host_that_has_worked_since_it_started_is_not_held_back()
    {
        var turned = Row(Host("h", "s", At(9, 0), "Stop"), Idle("s", At(16, 50)));
        turned.Hold(At(8, 0));
        Assert.Equal(At(16, 50), turned.LastChanged);

        var sinceStart = Row(Host("h", "s", At(9, 0), "SessionStart"), Idle("s", At(16, 50)));
        sinceStart.Hold(At(10, 0));
        Assert.Equal(At(16, 50), sinceStart.LastChanged);
    }

    /// <summary>
    /// The row used to keep the host record it was created with, so its group key never followed a
    /// /resume and the membership stayed under the old id; the next launch then showed it ungrouped.
    /// </summary>
    [Fact]
    public void A_resume_inside_a_session_moves_its_membership_to_the_new_conversation()
    {
        var groups = Groups(("side", new[] { "fresh" }));
        var row = Row(Host("h1", "fresh", At(9, 0)), Idle("fresh", At(9, 1)));
        SessionGroup.Apply(groups, new[] { row });
        Assert.Equal("side", row.GroupName);

        row.Update(Idle("resumed", At(9, 5)), Host("h1", "resumed", At(9, 0), "SessionStart"));
        Assert.True(SessionGroup.Apply(groups, new[] { row }));
        Assert.Equal(new[] { "resumed" }, groups[0].Members);
        Assert.Equal("side", row.GroupName);

        var afterRelaunch = Row(Host("h1", "resumed", At(9, 0)), Idle("resumed", At(9, 5)));
        SessionGroup.Apply(groups, new[] { afterRelaunch });
        Assert.Equal("side", afterRelaunch.GroupName);
    }

    [Fact]
    public void A_host_record_that_learns_its_session_updates_the_row()
    {
        var row = Row(Host("h1", "", At(9, 0)), SessionRow.Placeholder(Host("h1", "", At(9, 0))));
        Assert.Equal("h1", row.GroupKey);
        var learned = Host("h1", "thread-1", At(9, 0), "SessionStart");
        row.Update(SessionRow.Placeholder(learned), learned);
        Assert.Equal("thread-1", row.GroupKey);
        Assert.Equal("thread-1", row.Host.SessionId);
    }

    /// <summary>A CLI started by hand in a pane: the registry knows its conversation, the host record does not. A restart resumes the registry's.</summary>
    [Fact]
    public void A_row_is_grouped_by_the_conversation_its_restart_will_resume()
    {
        var row = Row(Host("h1", "stale", At(9, 0)), Idle("live", At(9, 5)));
        Assert.Equal("live", row.LiveSessionId);
        Assert.Equal(row.LiveSessionId, row.GroupKey);
    }

    /// <summary>Builds before this one grouped such a row under the host record's id; the first launch of this build must not drop it from its group.</summary>
    [Fact]
    public void A_row_grouped_under_its_host_records_id_keeps_its_group_after_the_upgrade()
    {
        var groups = Groups(("side", new[] { "stale" }));
        var row = Row(Host("h1", "stale", At(9, 0)), Idle("live", At(9, 5)));
        SessionGroup.Apply(groups, new[] { row });
        Assert.Equal("side", row.GroupName);
        Assert.Equal(new[] { "live" }, groups[0].Members);
    }

    [Fact]
    public void A_restarted_session_that_comes_back_under_another_id_keeps_its_group()
    {
        var groups = Groups(("tr", new[] { "s1" }));
        var old = Row(Host("h1", "s1", At(9, 0)), Idle("s1", At(16, 40)));
        SessionGroup.Apply(groups, new[] { old });

        var next = Row(Host("h1b", "s1-forked", At(16, 59, 36), "SessionStart"), Idle("s1-forked", At(16, 59, 40)));
        next.Inherit(old.GroupedAs, old.LastChanged);
        SessionGroup.Apply(groups, new[] { next });
        Assert.Equal("tr", next.GroupName);
        Assert.Equal(new[] { "s1-forked" }, groups[0].Members);
    }

    [Fact]
    public void A_resumed_or_adopted_session_rejoins_its_group_by_session_id()
    {
        var groups = Groups(("bz", new[] { "s1" }), ("tr", new[] { "s2" }));
        var resumed = Row(Host("new-host", "s2", At(17, 0), "SessionStart"), Idle("s2", At(17, 0, 5)));
        SessionGroup.Apply(groups, new[] { resumed });
        Assert.Equal("tr", resumed.GroupName);
        Assert.Equal(2, resumed.GroupOrder);
    }

    [Fact]
    public void Applying_groups_never_reorders_renames_or_expands_them()
    {
        var groups = Groups(("bz", new[] { "s1" }), ("side", new[] { "s2" }), ("spacer", new[] { "s3" }), ("tr", new[] { "s4" }));
        groups[2].Collapsed = true;
        var rows = new[]
        {
            Row(Host("h4b", "s4", At(17, 0), "SessionStart"), Idle("s4", At(17, 0, 3))),
            Row(Host("h1", "s1", At(9, 0)), Idle("s1", At(9, 1))),
            Row(Host("h3b", "s3", At(17, 0), "SessionStart"), Idle("s3", At(17, 0, 4))),
        };
        rows[0].Inherit("s4", At(12, 0));
        rows[2].Inherit("s3", At(12, 0));
        SessionGroup.Apply(groups, rows);
        Assert.Equal(new[] { "bz", "side", "spacer", "tr" }, groups.Select(g => g.Name));
        Assert.Equal(new[] { false, false, true, false }, groups.Select(g => g.Collapsed));
        Assert.Equal(new[] { 4, 1, 3 }, rows.Select(r => r.GroupOrder));
    }
}
