using System.Text.Json;
using Xunit;

namespace SessionDeck.Tests;

public class ReadLedgerTests
{
    static readonly DateTime T0 = new(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
    static DateTime At(int minutes) => T0.AddMinutes(minutes);

    static SessionRow Completed(string id, DateTime finishedUtc) =>
        new(new SessionInfo { SessionId = id, Status = "idle", StatusUpdatedAt = finishedUtc.ToLocalTime(), UpdatedAt = finishedUtc.ToLocalTime(), Kind = "interactive", Name = id });

    [Fact]
    public void A_session_that_finished_after_it_was_last_seen_is_unread_and_viewing_it_clears_that()
    {
        var ledger = new ReadLedger(new());
        Assert.True(ledger.IsUnread("s1", At(10)));
        Assert.True(ledger.MarkRead("s1", At(5)));
        Assert.True(ledger.IsUnread("s1", At(10)));
        Assert.True(ledger.MarkRead("s1", At(10)));
        Assert.False(ledger.IsUnread("s1", At(10)));
        Assert.False(ledger.MarkRead("s1", At(10)));
        Assert.False(ledger.IsUnread("s1", At(3)));
    }

    [Fact]
    public void A_row_badges_when_it_completed_after_the_read_mark_and_not_otherwise()
    {
        var row = Completed("s1", At(10));
        Assert.False(row.IsUnread);
        Assert.True(row.SetReadAt(DateTime.MinValue));
        Assert.True(row.IsUnread);
        Assert.Equal("Unread", row.StateTooltip);

        Assert.True(row.SetReadAt(At(12)));
        Assert.False(row.IsUnread);
        Assert.Equal("Completed", row.StateTooltip);

        Assert.True(row.SetReadAt(At(8)));
        Assert.True(row.IsUnread);
    }

    [Fact]
    public void Working_awaiting_and_ended_rows_never_badge()
    {
        var working = new SessionRow(new SessionInfo { SessionId = "w", Status = "busy", StatusUpdatedAt = At(10), Kind = "interactive" });
        Assert.False(working.IsUnread);
        var waiting = new SessionRow(new SessionInfo { SessionId = "a", Status = "waiting", StatusUpdatedAt = At(10), Kind = "interactive" });
        Assert.False(waiting.IsUnread);
        var ended = new SessionRow(new SessionInfo { SessionId = "e", Status = "exited", StatusUpdatedAt = At(10), Kind = "interactive" });
        Assert.False(ended.IsUnread);
    }

    [Fact]
    public void A_viewed_session_that_works_again_and_finishes_out_of_sight_is_unread_again()
    {
        var row = Completed("s1", At(10));
        row.SetReadAt(At(11));
        Assert.False(row.IsUnread);
        row.Update(new SessionInfo { SessionId = "s1", Status = "busy", StatusUpdatedAt = At(20).ToLocalTime(), UpdatedAt = At(20).ToLocalTime(), Kind = "interactive", Name = "s1" });
        Assert.False(row.IsUnread);
        row.Update(new SessionInfo { SessionId = "s1", Status = "idle", StatusUpdatedAt = At(25).ToLocalTime(), UpdatedAt = At(25).ToLocalTime(), Kind = "interactive", Name = "s1" });
        Assert.True(row.IsUnread);
    }

    [Fact]
    public void The_idle_notification_a_minute_after_a_turn_does_not_make_a_viewed_session_unread()
    {
        var row = Completed("s1", At(10));
        row.SetReadAt(At(11));
        Assert.False(row.IsUnread);
        row.Update(new SessionInfo { SessionId = "s1", Status = "idle", StatusUpdatedAt = At(12).ToLocalTime(), UpdatedAt = At(12).ToLocalTime(), Kind = "interactive", Name = "s1" });
        Assert.False(row.IsUnread);
        Assert.Equal(At(10), row.StateSince.ToUniversalTime());
    }

    [Fact]
    public void A_held_restart_time_is_the_time_the_session_finished()
    {
        var host = new SessionDeck.Host.HostRecord { Id = "h1", SessionId = "s1", StartedAt = At(9), LastEvent = "SessionStart" };
        var row = new SessionRow(host, new SessionInfo { SessionId = "s1", Status = "idle", StatusUpdatedAt = At(10).ToLocalTime(), UpdatedAt = At(10).ToLocalTime(), Kind = "interactive", Name = "s1" });
        row.Hold(At(3));
        Assert.Equal(At(3), row.StateSince.ToUniversalTime());
        row.SetReadAt(At(5));
        Assert.False(row.IsUnread);
    }

    [Fact]
    public void Pruning_forgets_only_sessions_gone_for_a_month()
    {
        var map = new Dictionary<string, long>
        {
            ["old-gone"] = ReadLedger.Ms(At(0)),
            ["old-live"] = ReadLedger.Ms(At(0)),
            ["new-gone"] = ReadLedger.Ms(At(0).AddDays(29)),
        };
        var ledger = new ReadLedger(map);
        Assert.Equal(1, ledger.Prune(new[] { "old-live" }, At(0).AddDays(31), TimeSpan.FromDays(30)));
        Assert.Equal(new[] { "new-gone", "old-live" }, map.Keys.OrderBy(k => k));
    }

    [Fact]
    public void The_ledger_survives_a_save_and_load_and_is_absent_until_seeded()
    {
        Assert.Null(new Settings().ReadAt);
        var s = new Settings { ReadAt = new() { ["s1"] = 123 } };
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
        Assert.Equal(123, back.ReadAt!["s1"]);
    }
}
