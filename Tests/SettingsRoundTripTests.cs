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

    /// <summary>
    /// The in-place update swap runs an older build against a newer file whenever an update rolls
    /// back, and that build saves on launch. Nested keys it does not know used to be dropped there.
    /// </summary>
    [Fact]
    public void Nested_keys_this_build_does_not_know_are_written_back_unchanged()
    {
        string json = "{\"Deck\":{\"Focused\":1,\"Split\":\"rows\",\"Columns\":[{\"Hosts\":[\"a\"],\"Pinned\":[true]}]}," +
                      "\"SessionGroups\":[{\"Name\":\"bz\",\"Color\":\"#f00\",\"Members\":[\"s\"]}]}";
        var s = JsonSerializer.Deserialize<Settings>(json)!;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(s));
        var root = doc.RootElement;
        Assert.Equal("rows", root.GetProperty("Deck").GetProperty("Split").GetString());
        Assert.Equal("[true]", root.GetProperty("Deck").GetProperty("Columns")[0].GetProperty("Pinned").GetRawText());
        Assert.Equal("#f00", root.GetProperty("SessionGroups")[0].GetProperty("Color").GetString());
    }

    [Fact]
    public void Deck_layout_window_state_and_groups_survive_a_save_and_load()
    {
        var s = new Settings { WindowMaximized = true, WindowLeft = 10, WindowTop = 20, WindowWidth = 3413, WindowHeight = 1392, Zoom = 1.2 };
        s.Deck = new DeckLayout
        {
            Focused = 2,
            Columns = { new DeckColumn { Hosts = { "a", "b" }, Sessions = { "s-a", "s-b" }, Active = "b", Fraction = 0.25 } },
            ClosedHosts = { "c" },
            ClosedSessions = { "s-c" },
        };
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
        Assert.True(back.WindowMaximized);
        Assert.Equal((10.0, 20.0, 3413.0, 1392.0, 1.2), (back.WindowLeft!.Value, back.WindowTop!.Value, back.WindowWidth, back.WindowHeight, back.Zoom));
        var col = Assert.Single(back.Deck.Columns);
        Assert.Equal(new[] { "a", "b" }, col.Hosts);
        Assert.Equal(new[] { "s-a", "s-b" }, col.Sessions);
        Assert.Equal(("b", 0.25), (col.Active, col.Fraction));
        Assert.Equal(2, back.Deck.Focused);
        Assert.Equal(new[] { "c" }, back.Deck.ClosedHosts);
        Assert.Equal(new[] { "s-c" }, back.Deck.ClosedSessions);
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
