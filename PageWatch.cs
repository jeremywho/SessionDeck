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

    /// <summary>How long a layout may go unanswered before a line says so, and how long before a second one does.</summary>
    internal static readonly int[] SilentAfterMs = { 3000, 15000 };

    // The newest layout the app posted and has not seen drawn: when, whether the page has said it took it, how
    // many times its silence has been said, and what the post itself threw.
    long _waitAt;
    bool _taken;
    int _said;
    string _postError = "";

    // How long the app's own handler ran around the layout posted at `_appAt`.
    long _appAt, _appMs;

    /// <summary>The app posted a layout stamped <paramref name="at"/>; <paramref name="error"/> is what the post threw, or why it was held.</summary>
    internal void Posted(long at, string error = "")
    {
        _waitAt = at; _taken = false; _said = 0; _postError = error;
    }

    /// <summary>The app's handler around the layout stamped <paramref name="at"/> ran for <paramref name="ms"/>.</summary>
    internal void AppWorked(long at, long ms)
    {
        _appAt = at; _appMs = ms;
    }

    /// <summary>
    /// A line once the page has left the newest layout unanswered for 3 s, and one more at 15 s; otherwise null.
    /// A page that is hung or has stopped drawing answers nothing, so its own reports cannot say so: this is the
    /// app's side of it. <c>waiting-for=page</c> means the page never took the message (its thread is held, or
    /// it is gone); <c>waiting-for=frame</c> means it took it and no frame came after.
    /// </summary>
    internal string? Silent(long nowMs, bool pageReady)
    {
        if (_waitAt <= 0 || _said >= SilentAfterMs.Length || nowMs - _waitAt < SilentAfterMs[_said]) return null;
        _said++;
        return $"page-silent {nowMs - _waitAt}ms waiting-for={(_taken ? "frame" : "page")} ready={(pageReady ? "yes" : "no")} post={(_postError.Length > 0 ? Word(_postError) : "ok")}";
    }

    /// <summary>One of the browser's processes for a <c>page-silent</c> line: whether it is there, and how busy over the second it was watched.</summary>
    internal static string ProcessState(string kind, int pid, double? cpuPercent, long workingSetMb) =>
        cpuPercent is { } cpu ? $"{Word(kind)}:{pid}=alive cpu={Math.Round(cpu)}% ws={workingSetMb}MB" : $"{Word(kind)}:{pid}=gone";

    /// <summary>The line for one of the browser's processes failing.</summary>
    internal static string Failed(string kind, string reason, int exitCode, string description, bool reloading) =>
        $"page-process failed kind={Word(kind)} reason={Word(reason)} exit={exitCode} process={Word(description)}{(reloading ? " reloading" : "")}";

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
                if (at > 0 && at == _waitAt) _waitAt = 0;
                if (at <= 0 || (!changed && nowMs - at < SlowLayoutMs)) return null;
                // `app` is the app's own handler around the post. What is left of `total` after `app`, `queued`
                // and `frame` is the answer waiting for the app's thread.
                string app = at == _appAt ? $" app={_appMs}ms" : "";
                return $"{(changed ? "tab-switch" : "page-layout")} total={nowMs - at}ms{app} queued={Number(root, "queued")}ms handler={Number(root, "handler")}ms frame={Number(root, "frame")}ms"
                    + $" pane={Word(Text(root, "pane"))} kind={Word(Text(root, "kind"))}{hidden}";
            }
            case "took":
                // The page's thread has the layout; whether a frame follows is the next thing waited for.
                if (Number(root, "at") is > 0 and var took && took == _waitAt) _taken = true;
                return null;
            case "frame":
                return $"page-frame {Number(root, "ms")}ms work={Number(root, "work")}ms raf={Number(root, "raf")}ms layout={Number(root, "layout")}ms blocked={Number(root, "blocked")}ms{hidden} ran={Rest(Text(root, "ran"))} scripts={Rest(Text(root, "scripts"))}";
            case "key":
                return $"page-key {Number(root, "ms")}ms ran={Rest(Text(root, "ran"))} pane={Word(Text(root, "pane"))} kind={Word(Text(root, "kind"))}";
            case "pace":
                return $"page-pace median={Number(root, "median")}ms max={Number(root, "max")}ms pane={Word(Text(root, "pane"))}";
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
