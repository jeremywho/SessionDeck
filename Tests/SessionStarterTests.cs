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
    public void Cpu_and_memory_come_at_once_and_cpu_needs_two_samples()
    {
        var sampler = new MachineLoadSampler();
        var first = sampler.SampleCpuAndMemory(-1);
        Assert.True(first.MemoryTotalGb > 1);
        Assert.InRange(first.MemoryPercent, 0, 100);
        Assert.Equal(-1, first.CpuPercent);
        Assert.Equal(-1, first.GpuPercent);
        Thread.Sleep(300);
        var second = sampler.SampleCpuAndMemory(7);
        Assert.InRange(second.CpuPercent, 0, 100);
        Assert.Equal(7, second.GpuPercent);
    }

    [Fact]
    public void The_monitor_publishes_cpu_and_memory_before_the_gpu_counters_have_opened()
    {
        using var gpuGate = new ManualResetEventSlim(false);
        var monitor = new MachineLoadMonitor(TimeSpan.FromMilliseconds(100), gpu: () => { gpuGate.Wait(); return 42; });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (monitor.Latest == null && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.NotNull(monitor.Latest);
        Assert.True(monitor.Latest!.MemoryTotalGb > 1);
        Assert.Equal(-1, monitor.Latest.GpuPercent);
        Assert.Matches(@"^-?\d+/-?\d+/-1$", monitor.Signature);
        gpuGate.Set();
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (monitor.Latest?.GpuPercent != 42 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.Equal(42, monitor.Latest?.GpuPercent);
    }
}
