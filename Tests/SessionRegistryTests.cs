using System.IO;
using Xunit;

namespace ClaudeSessionMonitor.Tests;

public class SessionRegistryTests
{
    static readonly string DataDir;

    static SessionRegistryTests()
    {
        // Redirect the registry BEFORE anything touches SessionRegistry — its paths are resolved
        // in a static initializer. Keeps the tests away from the real %APPDATA% file, which the
        // installed instance may be rewriting at that very moment.
        DataDir = Path.Combine(Path.GetTempPath(), "csm-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CSM_DATA_DIR", DataDir);
    }

    static string RegistryFile => Path.Combine(DataDir, "active-sessions.json");

    static SessionInfo Session(string id) => new() { SessionId = id, Cwd = @"C:\x", Name = id };

    // Regression for the graceful-shutdown wipe: Windows Update restarted the box, the claude
    // processes died first, and the still-pumping app snapshotted the empty live set over
    // active-sessions.json — so the post-reboot launch had nothing to offer for restore.
    [Fact]
    public void Frozen_snapshot_preserves_the_last_live_set()
    {
        // Seed two live sessions. This first write also proves the CSM_DATA_DIR redirect works —
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
}
