using System.Text.Json;

namespace SessionDeck;

/// <summary>
/// Whether the page has an accessibility tree and what asked for one, as lines for the performance log. Nothing
/// here wakes by itself: the page's state is read when the page reports a slow switch or an unexplained stall, and
/// a request is noted when its window message arrives.
/// </summary>
internal sealed class AxWatch
{
    /// <summary>
    /// The browser's own counts: each accessibility mode it has switched on (0 native APIs, 1 web contents,
    /// 2 inline text boxes, 3 extended properties, 4 HTML, 11 screen reader), and each call made into its
    /// accessibility objects by kind (Chromium's <c>UMA_API_*</c> numbers).
    /// </summary>
    internal const string ModesHistogram = "Accessibility.ModeFlag", ApisHistogram = "Accessibility.WinAPIs";

    internal const int AskEveryMs = 5000, CallsEveryMs = 60000, ApisNamed = 8;
    internal const int RequestsAtFirst = 10, RequestEveryMs = 60000;

    bool _asked;
    long _askedAt;
    string? _modesSaid;
    long _callsSaid, _saidAt;
    bool _unavailableSaid;
    int _requests;
    long _requestSaidAt;

    /// <summary>
    /// Whether a report from the page is one to ask the browser about: a switch slow enough to be taken apart, or
    /// a stall in which none of the page's own handlers ran.
    /// </summary>
    internal static bool Prompts(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return false;
        return t.GetString() switch
        {
            "switch-parts" => true,
            "stall" => !root.TryGetProperty("ran", out var ran) || ran.ValueKind != JsonValueKind.String || ran.GetString() is not { Length: > 0 },
            _ => false,
        };
    }

    /// <summary>Whether to ask the browser now: the reports that prompt it can come twenty a minute.</summary>
    internal bool ShouldAsk(long nowMs)
    {
        if (_asked && nowMs - _askedAt < AskEveryMs) return false;
        _asked = true;
        _askedAt = nowMs;
        return true;
    }

    /// <summary>What <c>Browser.getHistograms</c> is asked: only histograms with this in their name come back.</summary>
    internal static string Question(string histogram) => JsonSerializer.Serialize(new { query = histogram });

    /// <summary>
    /// The line for what the browser answered (<c>Browser.getHistograms</c>, asked once for each of the two), or
    /// null when it says nothing new: a line when the modes switched on differ from the last line, and when
    /// accessibility calls have gone on being made, at most once a minute.
    /// </summary>
    internal string? PageLine(string modesAnswer, string apisAnswer, long nowMs)
    {
        if (Buckets(modesAnswer, ModesHistogram) is not { } modes || Buckets(apisAnswer, ApisHistogram) is not { } apis)
            return Unavailable("no histograms in the answer");

        string modesNow = modes.Count == 0 ? "-" : string.Join(",", modes.Select(m => m.Bucket).Order());
        long calls = apis.Sum(a => a.Count);
        bool changed = modesNow != _modesSaid;
        if (!changed && !(calls > _callsSaid && nowMs - _saidAt >= CallsEveryMs)) return null;
        _modesSaid = modesNow;
        _callsSaid = calls;
        _saidAt = nowMs;
        string named = apis.Count == 0 ? "-" : string.Join(" ", apis.OrderByDescending(a => a.Count).ThenBy(a => a.Bucket).Take(ApisNamed).Select(a => $"{a.Bucket}:{a.Count}"));
        return $"page-ax modes={modesNow} apis={named} calls={calls}";
    }

    /// <summary>The line for the browser not answering the question at all, said once.</summary>
    internal string? Unavailable(string error)
    {
        if (_unavailableSaid) return null;
        _unavailableSaid = true;
        return "page-ax unavailable=" + new string(error.Select(c => char.IsControl(c) ? ' ' : c).Take(160).ToArray());
    }

    /// <summary>
    /// Counts one request for an accessibility object and says whether it is worth a line: the first ten of a
    /// run, then one a minute. The count so far is handed back for the line.
    /// </summary>
    internal bool Request(long nowMs, out int count)
    {
        count = ++_requests;
        if (count > RequestsAtFirst && nowMs - _requestSaidAt < RequestEveryMs) return false;
        _requestSaidAt = nowMs;
        return true;
    }

    /// <summary>The line for one request: which object was asked for, and whose window had the foreground then.</summary>
    internal static string RequestLine(long objectId, string foreground, int count) =>
        $"ax-request objid={ObjectName(objectId)} foreground={Word(foreground)} n={count}";

    /// <summary>
    /// The object ids of <c>WM_GETOBJECT</c> that say which interface is wanted: UI Automation asks for its root
    /// object, the older interface (MSAA) for the client area or the window.
    /// </summary>
    internal static string ObjectName(long objectId) => unchecked((int)objectId) switch
    {
        -25 => "UiaRootObjectId",
        -4 => "OBJID_CLIENT",
        0 => "OBJID_WINDOW",
        var other => other.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The buckets of one histogram that hold anything; none when the browser has no such histogram yet; null when the answer is not one.</summary>
    static List<(long Bucket, long Count)>? Buckets(string answer, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("histograms", out var all) || all.ValueKind != JsonValueKind.Array) return null;
            var found = new List<(long, long)>();
            foreach (var h in all.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object || !h.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || n.GetString() != name) continue;
                if (!h.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in buckets.EnumerateArray())
                    if (b.ValueKind == JsonValueKind.Object && Number(b, "count") is > 0 and var count) found.Add((Number(b, "low"), count));
            }
            return found;
        }
        catch (JsonException) { return null; }
    }

    static long Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    static string Word(string s) => s.Length == 0 ? "-" : new string(s.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).Take(40).ToArray());
}
