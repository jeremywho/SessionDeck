using Xunit;

namespace SessionDeck.Tests;

public class ListNavigationTests
{
    [Fact]
    public void A_step_moves_one_row_and_stops_at_the_ends()
    {
        Assert.Equal(2, ListNavigation.Step(5, 1, 1));
        Assert.Equal(0, ListNavigation.Step(5, 1, -1));
        Assert.Equal(0, ListNavigation.Step(5, 0, -1));
        Assert.Equal(4, ListNavigation.Step(5, 4, 1));
    }

    [Fact]
    public void With_no_current_row_a_step_starts_from_the_nearer_end()
    {
        Assert.Equal(0, ListNavigation.Step(5, null, 1));
        Assert.Equal(4, ListNavigation.Step(5, null, -1));
        Assert.Equal(0, ListNavigation.Step(5, 99, 1));
        Assert.Null(ListNavigation.Step(0, null, 1));
        Assert.Null(ListNavigation.Step(5, 2, 0));
    }

    [Fact]
    public void The_next_unread_is_found_in_either_direction_and_wraps()
    {
        var unread = new[] { false, true, false, false, true, false };
        Assert.Equal(4, ListNavigation.NextUnread(unread, 1, 1));
        Assert.Equal(1, ListNavigation.NextUnread(unread, 4, 1));
        Assert.Equal(1, ListNavigation.NextUnread(unread, 4, -1));
        Assert.Equal(4, ListNavigation.NextUnread(unread, 1, -1));
        Assert.Equal(4, ListNavigation.NextUnread(unread, 2, 1));
        Assert.Equal(1, ListNavigation.NextUnread(unread, 2, -1));
        Assert.Equal(1, ListNavigation.NextUnread(unread, 5, 1));
        Assert.Equal(4, ListNavigation.NextUnread(unread, 0, -1));
    }

    [Fact]
    public void An_unread_current_row_is_skipped_and_none_unread_means_no_move()
    {
        Assert.Null(ListNavigation.NextUnread(new[] { false, true, false }, 1, 1));
        Assert.Null(ListNavigation.NextUnread(new[] { false, false }, 0, 1));
        Assert.Null(ListNavigation.NextUnread(System.Array.Empty<bool>(), null, 1));
        Assert.Equal(1, ListNavigation.NextUnread(new[] { false, true, false, true }, null, 1));
        Assert.Equal(3, ListNavigation.NextUnread(new[] { false, true, false, true }, null, -1));
    }
}
