using System.IO;
using SessionDeck.Gateway;
using Xunit;

namespace SessionDeck.Tests;

public class SessionStarterTests
{
    static readonly Settings Settings = new() { ResumeFlags = "--dangerously-skip-permissions", CodexFlags = "--dangerously-bypass-approvals-and-sandbox", RecentFolders = new() { @"C:\x", @"C:\y" } };
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Each_provider_gets_the_command_the_deck_itself_would_run()
    {
        var claude = SessionStarter.Plan(new StartRequest("claude", Home, "my work", "hello"), Settings)!;
        Assert.Equal("Claude", claude.Provider);
        Assert.Equal(36, claude.SessionId.Length);
        Assert.StartsWith($"claude --session-id {claude.SessionId} --name \"my work\" --dangerously-skip-permissions", claude.Command);
        Assert.Equal("my work", claude.Title);
        Assert.Equal("hello", claude.Prompt);

        var codex = SessionStarter.Plan(new StartRequest("Codex", Home), Settings)!;
        Assert.Equal("Codex", codex.Provider);
        Assert.Equal("", codex.SessionId);
        Assert.StartsWith("codex --dangerously-bypass-approvals-and-sandbox", codex.Command);

        var agy = SessionStarter.Plan(new StartRequest("agy", Home), Settings)!;
        Assert.Equal("Agy", agy.Provider);
        Assert.StartsWith("agy --dangerously-skip-permissions --add-dir", agy.Command);
        Assert.Equal("AGY", agy.Title);

        var shell = SessionStarter.Plan(new StartRequest("shell", Home), Settings)!;
        Assert.Equal("Shell", shell.Provider);
        Assert.Equal("", shell.Command);
        Assert.Equal("Terminal", shell.Title);
    }

    [Fact]
    public void An_unknown_provider_or_a_missing_folder_is_refused_and_an_empty_folder_means_home()
    {
        Assert.Null(SessionStarter.Plan(new StartRequest("vim", Home), Settings));
        Assert.Null(SessionStarter.Plan(new StartRequest("claude", Path.Combine(Path.GetTempPath(), "sd-no-such-folder-" + Guid.NewGuid().ToString("N"))), Settings));
        Assert.Equal(Home, SessionStarter.Plan(new StartRequest("claude", ""), Settings)!.Cwd);
    }

    [Fact]
    public void Folders_offered_to_a_phone_start_with_home_and_skip_duplicates()
    {
        var settings = new Settings { RecentFolders = new() { @"C:\x", Home.ToUpperInvariant(), @"C:\x" } };
        Assert.Equal(new[] { Home, @"C:\x" }, SessionStarter.Folders(settings));
    }

    [Fact]
    public void The_machine_load_sampler_reports_memory_at_once_and_cpu_after_two_samples()
    {
        var sampler = new MachineLoadSampler();
        var first = sampler.Sample();
        Assert.True(first.MemoryTotalGb > 1);
        Assert.InRange(first.MemoryPercent, 0, 100);
        Assert.Equal(-1, first.CpuPercent);
        Thread.Sleep(200);
        var second = sampler.Sample();
        Assert.InRange(second.CpuPercent, 0, 100);
        Assert.InRange(second.GpuPercent, -1, 100);
    }
}
