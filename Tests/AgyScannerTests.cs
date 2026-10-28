using System.IO;
using Xunit;

namespace SessionDeck.Tests;

public class AgyScannerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-agy-" + Guid.NewGuid().ToString("N"));

    public AgyScannerTests() => Directory.CreateDirectory(Path.Combine(_dir, "log"));
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Each_process_log_maps_its_pid_to_the_conversation_it_opened()
    {
        File.WriteAllText(Path.Combine(_dir, "log", "cli-20261028_160142.log"),
            "I1028 16:01:42.413186      98 server.go:1847] Starting language server process with pid 98032\n" +
            "I1028 16:01:42.415677      98 server.go:1903] Language server version: 1.4.9\n" +
            "I1028 16:01:47.204690     883 server.go:1225] Created conversation b34a47d3-a8cf-4e41-98cc-7eca5276e0f5 (backend=BACKEND_TYPE_UNSPECIFIED)\n" +
            "I1028 16:01:47.204690     883 server.go:3268] GetConversationDetail: found conversation b34a47d3-a8cf-4e41-98cc-7eca5276e0f5 (active=true)\n");
        File.WriteAllText(Path.Combine(_dir, "log", "cli-20261028_113957.log"),
            "I1028 11:39:57.000000      98 server.go:1847] Starting language server process with pid 11664\n" +
            "I1028 11:41:00.000000     883 server.go:3268] GetConversationDetail: found conversation cd6eb9bd-12ad-4d1c-a32e-90958d14b685 (active=true)\n");
        string old = Path.Combine(_dir, "log", "cli-20261001_000000.log");
        File.WriteAllText(old, "I1001 00:00:00.000000      98 server.go:1847] Starting language server process with pid 5\nCreated conversation 00000000-0000-0000-0000-000000000000\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(10));

        var map = AgyScanner.ConversationsByPid(Path.Combine(_dir, "log"), DateTime.UtcNow);
        Assert.Equal("b34a47d3-a8cf-4e41-98cc-7eca5276e0f5", map[98032]);
        Assert.Equal("cd6eb9bd-12ad-4d1c-a32e-90958d14b685", map[11664]);
        Assert.False(map.ContainsKey(5));
    }

    [Fact]
    public void The_latest_rename_in_the_history_names_the_conversation()
    {
        string history = Path.Combine(_dir, "history.jsonl");
        File.WriteAllText(history,
            "{\"display\":\"I guess it was a model usage limit\",\"timestamp\":\"1793221684438\",\"workspace\":\"C:\\\\Users\\\\Jeremy\",\"conversationId\":\"cd6eb9bd-12ad-4d1c-a32e-90958d14b685\"}\n" +
            "{\"display\":\"/rename Oura v1\",\"timestamp\":\"1793221736641\",\"workspace\":\"C:\\\\Users\\\\Jeremy\",\"conversationId\":\"b34a47d3-a8cf-4e41-98cc-7eca5276e0f5\",\"type\":\"slash_command\"}\n" +
            "not json\n" +
            "{\"display\":\"/rename Oura\",\"timestamp\":\"1793221736999\",\"workspace\":\"C:\\\\Users\\\\Jeremy\",\"conversationId\":\"b34a47d3-a8cf-4e41-98cc-7eca5276e0f5\",\"type\":\"slash_command\"}\n");
        var names = AgyScanner.NamesFromHistory(history);
        Assert.Equal("Oura", names["b34a47d3-a8cf-4e41-98cc-7eca5276e0f5"]);
        Assert.False(names.ContainsKey("cd6eb9bd-12ad-4d1c-a32e-90958d14b685"));
        Assert.Empty(AgyScanner.NamesFromHistory(Path.Combine(_dir, "missing.jsonl")));
    }

    [Fact]
    public void The_agy_process_under_a_hosts_shell_is_found_and_another_panes_is_not()
    {
        var table = new Dictionary<int, (int Parent, string Name)>
        {
            [100] = (1, "SessionDeck.exe"), [200] = (100, "pwsh.exe"), [300] = (200, "agy.exe"), [310] = (300, "agy.exe"),
            [400] = (100, "pwsh.exe"), [500] = (400, "claude.exe"),
        };
        Assert.Equal(300, AgyScanner.AgyPidUnder(200, table));
        Assert.Equal(0, AgyScanner.AgyPidUnder(400, table));
        Assert.Equal(0, AgyScanner.AgyPidUnder(999, table));
    }
}
