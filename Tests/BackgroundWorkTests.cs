using System.IO;
using Xunit;

namespace SessionDeck.Tests;

public class BackgroundWorkTests
{
    [Fact]
    public void Only_task_outputs_written_inside_the_window_count()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sd-bg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(dir, "live.output"), "x");
            File.WriteAllText(Path.Combine(dir, "stale.output"), "x");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "stale.output"), now - TimeSpan.FromMinutes(10));
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
            Assert.Equal(1, SessionScanner.CountRecentOutputs(dir, now, TimeSpan.FromSeconds(90)));
            Assert.Equal(0, SessionScanner.CountRecentOutputs(Path.Combine(dir, "missing"), now, TimeSpan.FromSeconds(90)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_finished_turn_with_a_background_shell_is_completed_and_badged_not_scheduled()
    {
        var shell = new SessionRow(new SessionInfo { SessionId = "s1", Status = "idle", BackgroundWork = 1 });
        Assert.Equal(SessionState.Completed, shell.State);
        Assert.Equal(1, shell.SubagentsActive);
        var quiet = new SessionRow(new SessionInfo { SessionId = "s2", Status = "idle" });
        Assert.Equal(SessionState.Completed, quiet.State);
        var busy = new SessionRow(new SessionInfo { SessionId = "s3", Status = "busy", BackgroundWork = 1 });
        Assert.Equal(SessionState.Working, busy.State);
        var agents = new SessionRow(new SessionInfo { SessionId = "s4", Status = "idle", SubagentsActive = 2 });
        Assert.Equal(SessionState.Scheduled, agents.State);
        var tasks = new SessionRow(new SessionInfo { SessionId = "s5", Status = "idle", BackgroundTasks = 1 });
        Assert.Equal(SessionState.Scheduled, tasks.State);
    }
}
