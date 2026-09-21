using System.IO;
using Xunit;

namespace SessionDeck.Tests;

public sealed class SubagentCounterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "csm-subagents-" + Guid.NewGuid().ToString("N"));

    public SubagentCounterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Counts_inventory_and_ages_activity_from_cached_timestamps()
    {
        var now = DateTime.UtcNow;
        WriteAgent("agent-old.jsonl", now - TimeSpan.FromMinutes(2));
        WriteAgent("agent-live.jsonl", now - TimeSpan.FromSeconds(5));
        var counter = new SubagentCounter();

        Assert.Equal((2, 1), counter.Count(_dir, now));
        Assert.Equal((2, 0), counter.Count(_dir, now + TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void Directory_change_discovers_a_new_agent_before_periodic_reconciliation()
    {
        var now = DateTime.UtcNow;
        WriteAgent("agent-one.jsonl", now);
        var counter = new SubagentCounter();
        Assert.Equal((1, 1), counter.Count(_dir, now));

        WriteAgent("agent-two.jsonl", now + TimeSpan.FromSeconds(1));
        // Ensure this assertion is independent of filesystem timestamp resolution.
        Directory.SetLastWriteTimeUtc(_dir, now + TimeSpan.FromSeconds(2));

        Assert.Equal((2, 2), counter.Count(_dir, now + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Periodic_reconciliation_detects_a_resumed_cold_agent()
    {
        var now = DateTime.UtcNow;
        string path = WriteAgent("agent-resumed.jsonl", now - TimeSpan.FromMinutes(2));
        var counter = new SubagentCounter();
        Assert.Equal((1, 0), counter.Count(_dir, now));

        File.SetLastWriteTimeUtc(path, now + TimeSpan.FromSeconds(1));
        Assert.Equal((1, 0), counter.Count(_dir, now + TimeSpan.FromSeconds(2)));
        Assert.Equal((1, 1), counter.Count(_dir, now + SubagentCounter.ReconcileInterval));
    }

    [Fact]
    public void Retain_forgets_closed_session_directories()
    {
        var now = DateTime.UtcNow;
        WriteAgent("agent-one.jsonl", now);
        var counter = new SubagentCounter();
        Assert.Equal((1, 1), counter.Count(_dir, now));

        counter.Retain(new HashSet<string>());
        File.Delete(Path.Combine(_dir, "agent-one.jsonl"));

        Assert.Equal((0, 0), counter.Count(_dir, now + TimeSpan.FromSeconds(1)));
    }

    string WriteAgent(string name, DateTime writtenUtc)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }
}
