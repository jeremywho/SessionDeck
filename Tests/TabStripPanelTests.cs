using Xunit;

namespace SessionDeck.Tests;

public class TabStripPanelTests
{
    [Fact]
    public void Tabs_that_fit_get_their_own_width_so_the_plus_follows_the_last_tab() =>
        Assert.Equal(300, TabStripPanel.TabsWidth(available: 800, tabsWanted: 300, plusWidth: 36));

    [Fact]
    public void Tabs_that_overflow_leave_room_for_the_plus_at_the_right_edge() =>
        Assert.Equal(764, TabStripPanel.TabsWidth(available: 800, tabsWanted: 1500, plusWidth: 36));

    [Fact]
    public void A_strip_narrower_than_the_plus_gives_the_tabs_nothing() =>
        Assert.Equal(0, TabStripPanel.TabsWidth(available: 20, tabsWanted: 300, plusWidth: 36));
}
