using Xunit;

namespace ClaudeSessionMonitor.Tests;

/// <summary>
/// The update check is now triggered from two places — a periodic timer and showing the window — so
/// something has to stop the second one re-running `gh` every time the window is opened.
/// </summary>
public class UpdateCheckCadenceTests
{
    static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_first_check_always_runs()
        => Assert.True(Updater.ShouldCheckNow(DateTime.MinValue, Now, TimeSpan.FromMinutes(5)));

    [Fact]
    public void A_check_just_made_is_not_repeated()
        => Assert.False(Updater.ShouldCheckNow(Now.AddSeconds(-30), Now, TimeSpan.FromMinutes(5)));

    [Fact]
    public void A_check_older_than_the_gap_runs_again()
        => Assert.True(Updater.ShouldCheckNow(Now.AddMinutes(-6), Now, TimeSpan.FromMinutes(5)));

    [Fact]
    public void Exactly_the_gap_counts_as_due()
        => Assert.True(Updater.ShouldCheckNow(Now.AddMinutes(-5), Now, TimeSpan.FromMinutes(5)));

    // Opening and closing the window in a burst is the case this exists for: one check, not six.
    [Fact]
    public void A_burst_of_window_opens_yields_a_single_check()
    {
        var last = DateTime.MinValue;
        int checks = 0;
        for (int i = 0; i < 6; i++)
        {
            var t = Now.AddSeconds(i * 2);
            if (!Updater.ShouldCheckNow(last, t, Updater.MinCheckGap)) continue;
            checks++;
            last = t;
        }
        Assert.Equal(1, checks);
    }

    // The gap has to be shorter than the periodic timer, or the timer's own ticks would be swallowed.
    [Fact]
    public void The_debounce_gap_is_shorter_than_the_periodic_interval()
        => Assert.True(Updater.MinCheckGap < TimeSpan.FromMinutes(30));
}
