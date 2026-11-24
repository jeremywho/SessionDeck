using Xunit;

namespace SessionDeck.Tests;

public class DeckBrowserArgumentsTests
{
    [Fact]
    public void The_browser_is_told_to_refuse_an_accessibility_tree_unless_the_setting_asks_for_one()
    {
        Assert.Contains("--disable-renderer-accessibility", DeckBrowser.BrowserArguments(pageAccessibility: false).Split(' '));
        Assert.DoesNotContain("--disable-renderer-accessibility", DeckBrowser.BrowserArguments(pageAccessibility: true).Split(' '));
    }

    [Fact]
    public void A_deck_with_no_such_setting_saved_refuses_one()
    {
        Assert.False(new Settings().PageAccessibility);
        Assert.Contains("--disable-renderer-accessibility", DeckBrowser.BrowserArguments(new Settings().PageAccessibility));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void What_the_browser_was_started_with_before_is_still_there(bool pageAccessibility) =>
        Assert.Contains("--disable-features=msSmartScreenProtection", DeckBrowser.BrowserArguments(pageAccessibility).Split(' '));
}
