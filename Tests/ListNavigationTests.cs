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
    public void The_nearest_unread_in_that_direction_is_taken_and_the_current_row_is_skipped()
    {
        var unread = new[] { false, true, false, false, true, false };
        Assert.Equal(4, ListNavigation.NextUnread(unread, 1, 1));
        Assert.Equal(1, ListNavigation.NextUnread(unread, 4, -1));
        Assert.Equal(4, ListNavigation.NextUnread(unread, 2, 1));
        Assert.Equal(1, ListNavigation.NextUnread(unread, 2, -1));
    }

    [Fact]
    public void With_no_unread_that_way_the_end_row_is_taken_and_an_end_already_current_means_no_move()
    {
        var unread = new[] { false, true, false, false, true, false };
        Assert.Equal(5, ListNavigation.NextUnread(unread, 4, 1));
        Assert.Equal(0, ListNavigation.NextUnread(unread, 1, -1));
        Assert.Null(ListNavigation.NextUnread(unread, 5, 1));
        Assert.Null(ListNavigation.NextUnread(unread, 0, -1));
        Assert.Equal(2, ListNavigation.NextUnread(new[] { false, false, false }, 0, 1));
        Assert.Equal(0, ListNavigation.NextUnread(new[] { false, false, false }, 2, -1));
        Assert.Null(ListNavigation.NextUnread(System.Array.Empty<bool>(), null, 1));
    }

    [Fact]
    public void With_no_current_row_the_scan_starts_from_the_far_end()
    {
        Assert.Equal(1, ListNavigation.NextUnread(new[] { false, true, false, true }, null, 1));
        Assert.Equal(3, ListNavigation.NextUnread(new[] { false, true, false, true }, null, -1));
        Assert.Equal(3, ListNavigation.NextUnread(new[] { false, false, false, false }, null, 1));
        Assert.Equal(0, ListNavigation.NextUnread(new[] { false, false, false, false }, null, -1));
    }
}
