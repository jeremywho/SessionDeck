using System.IO;
using Xunit;

namespace SessionDeck.Tests;

public class SessionRegistryTests
{
    static readonly string DataDir;

    static SessionRegistryTests()
    {
        // Redirect the registry BEFORE anything touches SessionRegistry — its paths are resolved
        // in a static initializer. Keeps the tests away from the real %APPDATA% file, which the
        // installed instance may be rewriting at that very moment.
        DataDir = Path.Combine(Path.GetTempPath(), "csm-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SD_DATA_DIR", DataDir);
    }

    static string RegistryFile => Path.Combine(DataDir, "active-sessions.json");

    static SessionInfo Session(string id) => new() { SessionId = id, Cwd = @"C:\x", Name = id };

    // Regression for the graceful-shutdown wipe: Windows Update restarted the box, the claude
    // processes died first, and the still-pumping app snapshotted the empty live set over
    // active-sessions.json — so the post-reboot launch had nothing to offer for restore.
    [Fact]
    public void Frozen_snapshot_preserves_the_last_live_set()
    {
        // Seed two live sessions. This first write also proves the SD_DATA_DIR redirect works —
        // everything below would be meaningless (and dangerous) if writes hit the real %APPDATA%.
        SessionRegistry.Snapshot(new[] { Session("aaa"), Session("bbb") });
        Assert.True(File.Exists(RegistryFile), "registry file was not written to the redirected dir");
        var seeded = File.ReadAllText(RegistryFile);
        Assert.Contains("aaa", seeded);
        Assert.Contains("bbb", seeded);

        SessionRegistry.Frozen = true;
        try
        {
            // The shutdown scenario: every session gone in one scan. The file must survive.
            SessionRegistry.Snapshot(Array.Empty<SessionInfo>());
            Assert.Equal(seeded, File.ReadAllText(RegistryFile));

            // Any other frozen write is ignored too.
            SessionRegistry.Snapshot(new[] { Session("ccc") });
            Assert.Equal(seeded, File.ReadAllText(RegistryFile));
        }
        finally
        {
            SessionRegistry.Frozen = false;
        }

        // Unfrozen behavior is unchanged: a genuine graceful close still empties the file
        // (and the frozen calls above must not have corrupted the change-detection set).
        SessionRegistry.Snapshot(Array.Empty<SessionInfo>());
        var after = File.ReadAllText(RegistryFile);
        Assert.DoesNotContain("aaa", after);
        Assert.DoesNotContain("ccc", after);
    }

    // The snapshot used to be keyed on the id-SET alone, so a rename (or cwd/model change) with an
    // unchanged set was never persisted — and a set that stayed identical past the 7-day restore
    // cutoff aged itself out because LastSeen never advanced (hence the periodic checkpoint too,
    // which is time-based and pinned only by its existence here).
    [Fact]
    public void A_rename_with_an_unchanged_id_set_is_persisted()
    {
        SessionRegistry.ResetForTests();
        var s = Session("rename-me");
        SessionRegistry.Snapshot(new[] { s });

        var renamed = Session("rename-me");
        renamed.Name = "better name";
        SessionRegistry.Snapshot(new[] { renamed });

        Assert.Contains("better name", File.ReadAllText(RegistryFile));
    }

    static DateTime At(int h, int m) => new DateTime(2026, 9, 25, h, m, 0, DateTimeKind.Local);

    [Fact]
    public void Settled_times_are_saved_and_read_back_by_the_next_launch()
    {
        SessionRegistry.ResetForTests();
        var a = Session("settled-a");
        var b = Session("settled-b");
        SessionRegistry.Snapshot(new[] { a, b }, s => s == a ? At(16, 40) : At(16, 30));

        SessionRegistry.ResetForTests();
        Assert.Equal(At(16, 40), SessionRegistry.SettledAt("settled-a"));
        Assert.Equal(At(16, 30), SessionRegistry.SettledAt("settled-b"));
    }

    [Fact]
    public void A_working_session_keeps_the_time_it_last_settled()
    {
        SessionRegistry.ResetForTests();
        var a = Session("busy-a");
        SessionRegistry.Snapshot(new[] { a }, _ => At(16, 40));
        SessionRegistry.Snapshot(new[] { a }, _ => null);
        Assert.Equal(At(16, 40), SessionRegistry.SettledAt("busy-a"));
    }

    [Fact]
    public void A_session_restarting_out_of_the_live_set_keeps_its_settled_time()
    {
        SessionRegistry.ResetForTests();
        var a = Session("restarting-a");
        var b = Session("restarting-b");
        SessionRegistry.Snapshot(new[] { a, b }, _ => At(16, 40));
        SessionRegistry.Snapshot(new[] { b }, _ => At(16, 40));
        Assert.Equal(At(16, 40), SessionRegistry.SettledAt("restarting-a"));
    }

    /// <summary>After a reboot every session is resumed in a new host at about the same moment; their order in a group must not become the resume order.</summary>
    [Fact]
    public void After_a_reboot_resumed_rows_keep_their_order_within_their_group()
    {
        SessionRegistry.ResetForTests();
        var ids = new[] { "reboot-1", "reboot-2", "reboot-3" };
        var settled = new[] { At(16, 40), At(16, 30), At(16, 20) };
        var infos = ids.Select(Session).ToArray();
        SessionRegistry.Snapshot(infos, s => settled[Array.IndexOf(infos, s)]);
        SessionRegistry.ResetForTests();

        var boot = new DateTime(2026, 9, 25, 18, 0, 0, DateTimeKind.Local);
        var rows = ids.Select((id, i) =>
        {
            var host = new Host.HostRecord { Id = "h-" + id, SessionId = id, StartedAt = boot.ToUniversalTime(), LastEvent = "SessionStart" };
            var row = new SessionRow(host, new SessionInfo { SessionId = id, Status = "idle", StatusUpdatedAt = boot.AddSeconds(5 + i), Name = id });
            row.Hold(SessionRegistry.SettledAt(row.LiveSessionId));
            return row;
        }).ToList();
        Assert.Equal(ids.Reverse(), rows.OrderByDescending(r => r.Info.StatusUpdatedAt).Select(r => r.LiveSessionId));
        Assert.Equal(ids, rows.OrderByDescending(r => r.LastChanged).Select(r => r.LiveSessionId));
    }

    [Fact]
    public void Saved_sessions_keep_keys_this_build_does_not_know()
    {
        string json = "[{\"Id\":\"x\",\"LastChanged\":5,\"FutureField\":{\"k\":1}}]";
        var list = System.Text.Json.JsonSerializer.Deserialize<List<SavedSession>>(json)!;
        Assert.Equal(5, list[0].LastChanged);
        Assert.Contains("\"FutureField\":{\"k\":1}", System.Text.Json.JsonSerializer.Serialize(list));
    }

    /// <summary>SD_DATA_DIR must move every file and lock the instance owns, or a test instance shares state with the installed deck.</summary>
    [Fact]
    public void An_isolated_instance_keeps_its_settings_hosts_and_lock_apart()
    {
        Assert.Equal(DataDir, Settings.DataDir);
        Assert.Equal(Path.Combine(DataDir, "hosts"), HostManager.HostsDir);
        Assert.StartsWith("SessionDeck_SingleInstance_", Settings.InstanceMutexName);
    }

    [Fact]
    public void An_identical_set_within_the_checkpoint_window_is_not_rewritten()
    {
        SessionRegistry.ResetForTests();
        SessionRegistry.Snapshot(new[] { Session("same") });
        var first = File.GetLastWriteTimeUtc(RegistryFile);

        SessionRegistry.Snapshot(new[] { Session("same") });   // identical, seconds later
        Assert.Equal(first, File.GetLastWriteTimeUtc(RegistryFile));
    }
}
