using System.Text.Json;

namespace SessionDeck;

/// <summary>What the deck's page reports about its own slowness, as lines for the performance log.</summary>
internal static class PageWatch
{
    /// <summary>A change of tab is worth a line once it took this long from the app asking to the page having drawn it.</summary>
    internal const int SlowSwitchMs = 300;

    /// <summary>The line for one report from the page at <paramref name="nowMs"/> (Unix ms), or null when it is not worth one.</summary>
    internal static string? Line(JsonElement root, long nowMs)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string hidden = Flag(root, "hidden") ? " hidden" : "";
        switch (Text(root, "type"))
        {
            case "switched":
            {
                // `at` is when the app posted the layout; the page hands it back with what it measured on its side.
                long at = Number(root, "at");
                if (at <= 0 || nowMs - at < SlowSwitchMs) return null;
                return $"tab-switch total={nowMs - at}ms queued={Number(root, "queued")}ms handler={Number(root, "handler")}ms frame={Number(root, "frame")}ms"
                    + $" pane={Word(Text(root, "pane"))} kind={Word(Text(root, "kind"))}{hidden}";
            }
            case "stall":
                return $"page-stall {Number(root, "ms")}ms{hidden} ran={Rest(Text(root, "ran"))}";
            case "visibility":
                return "page-visibility " + (hidden.Length > 0 ? "hidden" : "visible");
            case "disconnected":
                return $"pane-socket lost host={Word(Text(root, "id"))} retry={Number(root, "retryMs")}ms";
        }
        return null;
    }

    static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && !double.IsNaN(d) && !double.IsInfinity(d)
            ? (long)Math.Round(Math.Clamp(d, long.MinValue / 2, long.MaxValue / 2)) : 0;

    static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>One value of a line: nothing in it may start another value or another line.</summary>
    static string Word(string s) => s.Length == 0 ? "-" : new string(s.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).Take(40).ToArray());

    static string Rest(string s) => s.Length == 0 ? "-" : new string(s.Select(c => char.IsControl(c) ? ' ' : c).Take(300).ToArray());
}
