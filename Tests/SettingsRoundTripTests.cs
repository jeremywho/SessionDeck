using System.Text.Json;
using Xunit;

namespace SessionDeck.Tests;

public class SettingsRoundTripTests
{
    [Fact]
    public void Session_groups_survive_a_save_and_load()
    {
        var s = new Settings();
        s.SessionGroups.Add(new SessionGroup { Name = "bz", Collapsed = true, Members = { "sid-1", "sid-2" } });
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
        var g = Assert.Single(back.SessionGroups);
        Assert.Equal("bz", g.Name);
        Assert.True(g.Collapsed);
        Assert.Equal(new[] { "sid-1", "sid-2" }, g.Members);
    }

    [Fact]
    public void Keys_this_build_does_not_know_are_written_back_unchanged()
    {
        string json = "{\"TerminalOpacity\": 90, \"FutureThing\": {\"a\": [1, 2]}, \"FutureFlag\": true}";
        var s = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(90, s.TerminalOpacity);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(s));
        Assert.Equal("[1,2]", doc.RootElement.GetProperty("FutureThing").GetProperty("a").GetRawText());
        Assert.True(doc.RootElement.GetProperty("FutureFlag").GetBoolean());
    }
}
