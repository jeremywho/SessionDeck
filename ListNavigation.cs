namespace SessionDeck;

/// <summary>
/// Moving through the session list from keys alone, for a stick or a macro pad: a step up or down
/// in the displayed order, or a jump to the nearest unread row in one direction, wrapping round.
/// </summary>
internal static class ListNavigation
{
    /// <summary>The index of the row <paramref name="delta"/> places from the current one, clamped to the ends;
    /// with no current row, the first row for a step down and the last for a step up. Null when the list is empty.</summary>
    public static int? Step(int count, int? current, int delta)
    {
        if (count == 0 || delta == 0) return null;
        if (current is not { } at || at < 0 || at >= count) return delta > 0 ? 0 : count - 1;
        return Math.Clamp(at + delta, 0, count - 1);
    }

    /// <summary>The index of the nearest unread row below (direction +1) or above (direction -1) the current one;
    /// with none that way, the last row (down) or the first (up). Null when the list is empty or that end is
    /// already the current row. With no current row the scan starts from the far end.</summary>
    public static int? NextUnread(IReadOnlyList<bool> unread, int? current, int direction)
    {
        int count = unread.Count;
        if (count == 0 || direction == 0) return null;
        int step = direction > 0 ? 1 : -1;
        int start = current is { } at && at >= 0 && at < count ? at : (step > 0 ? -1 : count);
        for (int index = start + step; index >= 0 && index < count; index += step)
            if (unread[index]) return index;
        int end = step > 0 ? count - 1 : 0;
        return end == start ? null : end;
    }
}
