using System.Text.Json;
using Xunit;

namespace SessionDeck.Tests;

public class DeckBrowserDispatchTests
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void A_message_naming_a_terminal_goes_to_that_terminal()
    {
        string? hostId = null, pageType = null;
        DeckBrowser.Dispatch(Json("""{"type":"title","id":"h1","title":"x"}"""), (h, _, _) => hostId = h, (t, _) => pageType = t);
        Assert.Equal("h1", hostId);
        Assert.Null(pageType);
    }

    [Theory]
    [InlineData("""{"type":"dropSpacer","index":0,"group":2,"side":"left"}""", "dropSpacer")]
    [InlineData("""{"type":"fractions","id":"","fracs":[0.5,0.5]}""", "fractions")]
    [InlineData("""{"type":"prRefresh"}""", "prRefresh")]
    public void A_message_naming_no_terminal_goes_to_the_page_handler(string json, string type)
    {
        string? hostId = null, pageType = null;
        DeckBrowser.Dispatch(Json(json), (h, _, _) => hostId = h, (t, _) => pageType = t);
        Assert.Null(hostId);
        Assert.Equal(type, pageType);
    }
}
