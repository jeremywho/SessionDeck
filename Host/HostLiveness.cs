namespace SessionDeck.Host;

/// <summary>
/// Whether a host record still names a running host. A pid outlives its process: after a reboot
/// the same number belongs to whatever started next, often a service whose start time this user
/// cannot read, so "start time unreadable" alone must never mean alive.
/// </summary>
internal static class HostLiveness
{
    static readonly long Tolerance = TimeSpan.TicksPerSecond * 2;
    static readonly long BootSlack = TimeSpan.TicksPerSecond * 5;

    /// <param name="recordStartTicks">The host's own start, UTC ticks, as written in its record.</param>
    /// <param name="processStartTicks">The start of the process at that pid, UTC ticks; null when it could not be read.</param>
    /// <param name="processName">The name of the process at that pid.</param>
    /// <param name="bootTicks">When this machine last booted, UTC ticks.</param>
    public static bool Alive(long recordStartTicks, long? processStartTicks, string processName, long bootTicks)
    {
        if (recordStartTicks < bootTicks - BootSlack) return false;
        if (processStartTicks is long start) return Math.Abs(start - recordStartTicks) < Tolerance;
        return string.Equals(processName, "SessionDeck", StringComparison.OrdinalIgnoreCase);
    }

    public static long BootTicksUtc() => (DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).Ticks;
}
