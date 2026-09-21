using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>
/// Switching the machine's Claude account rewrites <c>~/.claude/.credentials.json</c> and then, a few
/// milliseconds later, <c>~/.claude.json</c>. These pin the three pieces that turn that into an
/// immediate footer refresh: noticing the replacement, settling before acting, and not letting the
/// resulting polls pile up.
/// </summary>
public class AccountSwitchTests
{
    // Short enough to keep the suite quick, long enough that a burst genuinely lands inside it.
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    /// <summary>Waits for a counter to reach <paramref name="want"/>, then returns what it settled on.</summary>
    static int Settle(Func<int> count, int want, int timeoutMs = 5000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end && count() < want) Thread.Sleep(15);
        Thread.Sleep(Debounce.Milliseconds * 3);   // give a wrongly-uncoalesced extra call time to show
        return count();
    }

    // The point of the debounce: the switcher's two writes (credentials, then the config) and any
    // Created/Changed/Renamed spam Windows emits for one replacement are ONE account switch.
    [Fact]
    public void A_burst_of_changes_reports_a_single_switch()
    {
        using var w = new CredentialsWatcher(Path.Combine(Path.GetTempPath(), ".credentials.json"), Debounce);
        int fired = 0;
        w.Changed += () => Interlocked.Increment(ref fired);

        for (int i = 0; i < 20; i++) w.Poke();

        Assert.Equal(1, Settle(() => Volatile.Read(ref fired), 1));
    }

    // ...but a later switch is a new event, not swallowed by the first one's window.
    [Fact]
    public void A_later_change_reports_again()
    {
        using var w = new CredentialsWatcher(Path.Combine(Path.GetTempPath(), ".credentials.json"), Debounce);
        int fired = 0;
        w.Changed += () => Interlocked.Increment(ref fired);

        w.Poke();
        Assert.Equal(1, Settle(() => Volatile.Read(ref fired), 1));

        w.Poke();
        Assert.Equal(2, Settle(() => Volatile.Read(ref fired), 2));
    }

    // Nothing edits this file in place — it is written beside and renamed over the top (the account
    // switcher uses Move-Item -Force). So the watcher has to survive the target being a different
    // file object each time, which is why it watches the directory rather than holding the file.
    [Fact]
    public void Replacing_the_file_by_rename_reports_a_switch()
    {
        var dir = Directory.CreateTempSubdirectory("csm-creds").FullName;
        try
        {
            var creds = Path.Combine(dir, ".credentials.json");
            File.WriteAllText(creds, """{ "claudeAiOauth": { "accessToken": "old" } }""");

            using var w = new CredentialsWatcher(creds, Debounce);
            int fired = 0;
            w.Changed += () => Interlocked.Increment(ref fired);
            Assert.True(w.Start());

            var tmp = Path.Combine(dir, ".credentials.json.tmp");
            File.WriteAllText(tmp, """{ "claudeAiOauth": { "accessToken": "new" } }""");
            File.Move(tmp, creds, overwrite: true);

            Assert.Equal(1, Settle(() => Volatile.Read(ref fired), 1));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ~/.claude also holds settings.json and other churn. Reacting to those would mean an API call
    // every time Claude Code saves anything.
    [Fact]
    public void A_write_to_a_neighbouring_file_is_ignored()
    {
        var dir = Directory.CreateTempSubdirectory("csm-creds").FullName;
        try
        {
            var creds = Path.Combine(dir, ".credentials.json");
            File.WriteAllText(creds, "{}");

            using var w = new CredentialsWatcher(creds, Debounce);
            int fired = 0;
            w.Changed += () => Interlocked.Increment(ref fired);
            Assert.True(w.Start());

            File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "theme": "dark" }""");

            Assert.Equal(0, Settle(() => Volatile.Read(ref fired), 1, 1200));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // A machine with no ~/.claude yet must not take the app down; it just loses the fast path.
    [Fact]
    public void A_missing_directory_declines_instead_of_throwing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "csm-no-such-dir-" + Guid.NewGuid(), ".credentials.json");
        using var w = new CredentialsWatcher(missing, Debounce);
        Assert.False(w.Start());
    }

    // Disposing mid-window must not fire afterwards — the window is closing, and the handler touches UI.
    [Fact]
    public void Disposing_cancels_a_pending_report()
    {
        var w = new CredentialsWatcher(Path.Combine(Path.GetTempPath(), ".credentials.json"), Debounce);
        int fired = 0;
        w.Changed += () => Interlocked.Increment(ref fired);

        w.Poke();
        w.Dispose();

        Thread.Sleep(Debounce.Milliseconds * 4);
        Assert.Equal(0, Volatile.Read(ref fired));
    }

    // --- poll coalescing -----------------------------------------------------------------------

    // Three callers can now ask for a poll (startup, the 15-minute timer, an account switch), and a
    // switch is exactly when they collide. Requests arriving during a call collapse into one re-run:
    // the last request still gets a call that started after it, without N HTTP calls in flight.
    [Fact]
    public async Task Requests_during_a_run_collapse_into_one_rerun()
    {
        var gate = new TaskCompletionSource();
        int runs = 0;
        var flight = new SingleFlight(async () => { Interlocked.Increment(ref runs); await gate.Task; });

        var first = flight.RunAsync();
        for (int i = 0; i < 5; i++) await flight.RunAsync();   // all queue behind the running one

        Assert.Equal(1, Volatile.Read(ref runs));
        gate.SetResult();
        await first;

        Assert.Equal(2, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Sequential_requests_each_get_their_own_run()
    {
        int runs = 0;
        var flight = new SingleFlight(() => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        await flight.RunAsync();
        await flight.RunAsync();
        await flight.RunAsync();

        Assert.Equal(3, Volatile.Read(ref runs));
    }

    // A throwing poll must release the gate, or every later switch would be silently ignored.
    [Fact]
    public async Task A_failing_run_does_not_wedge_the_gate()
    {
        int runs = 0;
        var flight = new SingleFlight(() =>
        {
            Interlocked.Increment(ref runs);
            throw new InvalidOperationException("poll blew up");
        });

        await flight.RunAsync();
        await flight.RunAsync();

        Assert.Equal(2, Volatile.Read(ref runs));
    }

    // --- account identity ----------------------------------------------------------------------

    // The address is what the user reads, but the uuid is what the app compares: addresses repeat
    // across logins and vanish when signed out.
    [Fact]
    public void The_account_uuid_is_read_alongside_the_address()
    {
        var info = AccountScanner.Parse("""
        { "oauthAccount": { "emailAddress": "someone@example.com",
                            "accountUuid": "11111111-2222-3333-4444-555555555555" } }
        """);

        Assert.NotNull(info);
        Assert.Equal("11111111-2222-3333-4444-555555555555", info!.AccountUuid);
    }

    [Fact]
    public void A_missing_account_uuid_reads_as_empty_rather_than_null()
        => Assert.Equal("", AccountScanner.Parse("""{ "oauthAccount": {} }""")!.AccountUuid);

    // The first reading is not a switch: treating it as one would throw away the poll made at startup.
    [Fact]
    public void The_first_reading_is_not_a_switch()
        => Assert.False(AccountScanner.AccountChanged(null, "abc"));

    [Fact]
    public void The_same_account_read_again_is_not_a_switch()
        => Assert.False(AccountScanner.AccountChanged("abc", "abc"));

    [Fact]
    public void A_different_account_is_a_switch()
        => Assert.True(AccountScanner.AccountChanged("abc", "def"));

    // Signing out counts too — the numbers on screen belong to an account nobody is signed into.
    [Fact]
    public void Signing_out_is_a_switch()
        => Assert.True(AccountScanner.AccountChanged("abc", ""));
}
