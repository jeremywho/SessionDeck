using System.Text.Json;

namespace SessionDeck;

/// <summary>What the deck's page reports about its own slowness, as lines for the performance log.</summary>
internal sealed class PageWatch
{
    /// <summary>A layout that changed nothing on show is worth a line once it took this long from the app asking to the page having drawn it.</summary>
    internal const int SlowLayoutMs = 300;

    // Panes whose socket to their host is down. A pane whose host is gone asks again for as long as it is open;
    // one line says so, and one more when it is back.
    readonly HashSet<string> _lost = new();

    /// <summary>How many panes are waiting for their host now.</summary>
    internal int Lost => _lost.Count;

    /// <summary>The line for one report from the page at <paramref name="nowMs"/> (Unix ms), or null when it is not worth one.</summary>
    internal string? Line(JsonElement root, long nowMs)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string hidden = Flag(root, "hidden") ? " hidden" : "";
        switch (Text(root, "type"))
        {
            case "switched":
            {
                // `at` is when the app posted the layout; the page hands it back with what it measured on its side.
                // A change of what is shown is logged however quick it was: a pane that looked stuck beside a line
                // saying the page was prompt puts the stall past the page, and no line would say nothing at all.
                long at = Number(root, "at");
                bool changed = Flag(root, "changed");
                if (at <= 0 || (!changed && nowMs - at < SlowLayoutMs)) return null;
                return $"{(changed ? "tab-switch" : "page-layout")} total={nowMs - at}ms queued={Number(root, "queued")}ms handler={Number(root, "handler")}ms frame={Number(root, "frame")}ms"
                    + $" pane={Word(Text(root, "pane"))} kind={Word(Text(root, "kind"))}{hidden}";
            }
            case "stall":
                return $"page-stall {Number(root, "ms")}ms{hidden} ran={Rest(Text(root, "ran"))}";
            case "visibility":
                return "page-visibility " + (hidden.Length > 0 ? "hidden" : "visible");
            case "disconnected":
            {
                string id = Text(root, "id");
                if (_lost.Count >= 500) _lost.Clear();
                if (id.Length == 0 || !_lost.Add(id)) return null;
                return $"pane-socket lost host={Word(id)} retry={Number(root, "retryMs")}ms";
            }
            case "connected":
                return _lost.Remove(Text(root, "id")) ? $"pane-socket back host={Word(Text(root, "id"))}" : null;
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
