using System.IO;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class HostRecordWriteTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-record-" + Guid.NewGuid().ToString("N"));

    public HostRecordWriteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void A_record_is_written_only_when_its_content_changed()
    {
        string? last = null;
        Assert.True(RecordGate.Changed("{\"a\":1}", ref last));
        Assert.False(RecordGate.Changed("{\"a\":1}", ref last));
        Assert.False(RecordGate.Changed("{\"a\":1}", ref last));
        Assert.True(RecordGate.Changed("{\"a\":2}", ref last));
        Assert.Equal("{\"a\":2}", last);
    }

    [Theory]
    [InlineData("✳ local chess", "local chess")]
    [InlineData("◐ local chess", "local chess")]
    [InlineData("✻ Claude Code", "Claude Code")]
    [InlineData("◆ codex", "codex")]
    [InlineData("🤖 helper", "helper")]
    [InlineData("local chess", "local chess")]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe", "pwsh")]
    [InlineData("R16 build", "R16 build")]
    public void A_leading_mark_is_not_part_of_the_name(string title, string name)
        => Assert.Equal(name, TitleMarks.Strip(title));

    [Theory]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe", true)]
    [InlineData("C:\\Windows\\system32\\cmd.exe", true)]
    [InlineData("✳ local chess", false)]
    [InlineData("AGY", false)]
    [InlineData("", false)]
    public void A_shells_own_path_is_recognised_as_no_name(string title, bool exePath)
        => Assert.Equal(exePath, TitleMarks.IsExePath(title));

    [Fact]
    public void Spinner_frames_are_the_same_name_and_a_rename_is_not()
    {
        Assert.True(TitleMarks.SameName("✳ local chess", "◑ local chess"));
        Assert.True(TitleMarks.SameName("✳ local chess", "✳ local chess"));
        Assert.False(TitleMarks.SameName("✳ local chess", "✳ chess database"));
        Assert.False(TitleMarks.SameName("✳ local chess", ""));
    }

    [Fact]
    public void The_hook_counter_is_not_part_of_the_file()
    {
        var r = new HostRecord { Id = "h1", HookEvents = 5 };
        Assert.DoesNotContain("HookEvents", System.Text.Json.JsonSerializer.Serialize(r));
    }

    [Fact]
    public void Copying_over_a_file_in_use_waits_for_it_and_gives_up_after_the_deadline()
    {
        string src = Path.Combine(_dir, "new.exe");
        string dst = Path.Combine(_dir, "installed.exe");
        File.WriteAllText(src, "new");
        File.WriteAllText(dst, "old");
        using (var hold = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(Installer.CopyWithRetry(src, dst, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50)));
        }
        Assert.Equal("old", File.ReadAllText(dst));

        var release = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.None);
        var releaser = Task.Run(() => { Thread.Sleep(200); release.Dispose(); });
        Assert.True(Installer.CopyWithRetry(src, dst, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)));
        releaser.Wait();
        Assert.Equal("new", File.ReadAllText(dst));
    }

    [Fact]
    public void The_host_answers_terminal_queries_unless_a_viewer_that_can_is_attached()
    {
        Assert.True(HostProgram.HostAnswersQueries(Array.Empty<bool>()));
        Assert.True(HostProgram.HostAnswersQueries(new[] { true }));
        Assert.True(HostProgram.HostAnswersQueries(new[] { true, true }));
        Assert.False(HostProgram.HostAnswersQueries(new[] { false }));
        Assert.False(HostProgram.HostAnswersQueries(new[] { true, false }));
    }

    [Fact]
    public void A_snapshot_carries_the_pty_size_and_a_resize_announces_the_new_one()
    {
        using var snap = System.Text.Json.JsonDocument.Parse(HostProgram.SnapshotJson(10, 20, "t", 132, 40));
        Assert.Equal("snapshot", snap.RootElement.GetProperty("type").GetString());
        Assert.Equal(10, snap.RootElement.GetProperty("start").GetInt64());
        Assert.Equal(20, snap.RootElement.GetProperty("end").GetInt64());
        Assert.Equal(132, snap.RootElement.GetProperty("cols").GetInt32());
        Assert.Equal(40, snap.RootElement.GetProperty("rows").GetInt32());
        Assert.Equal("{\"type\":\"size\",\"cols\":80,\"rows\":24}", HostProgram.SizeJson(80, 24));
    }
}
