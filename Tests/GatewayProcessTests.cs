using System.Diagnostics;
using System.IO;
using SessionDeck.Gateway;
using Xunit;

namespace SessionDeck.Tests;

public class GatewayProcessTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-gwp-" + Guid.NewGuid().ToString("N"));
    public GatewayProcessTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void A_newer_deck_replaces_an_older_gateway()
    {
        var me = Process.GetCurrentProcess();
        var r = new GatewayRecord(me.Id, me.StartTime.ToUniversalTime().Ticks, 7420, @"C:\old\SessionDeck.exe", "t");
        Assert.True(GatewayProcess.IsAlive(r));
        Assert.True(GatewayProcess.ShouldReplace(r, @"C:\new\SessionDeck.exe"));
        Assert.False(GatewayProcess.ShouldReplace(r, @"c:\OLD\SessionDeck.exe"));
        Assert.False(GatewayProcess.IsAlive(r with { StartTicks = r.StartTicks - TimeSpan.TicksPerMinute }));
        Assert.False(GatewayProcess.IsAlive(r with { Pid = 0 }));
    }

    [Fact]
    public void The_deck_starts_a_gateway_when_none_runs_keeps_a_current_one_and_replaces_an_old_one()
    {
        var spawned = new List<string>();
        var quitAsked = new List<int>();
        var r = new GatewayRecord(42, 1, 7420, @"C:\old\SessionDeck.exe", "t");

        Assert.Equal(GatewayProcess.Outcome.Started, GatewayProcess.EnsureRunning(null, @"C:\new\SessionDeck.exe", _ => false, q => quitAsked.Add(q.Pid), spawned.Add, TimeSpan.Zero));
        Assert.Equal(new[] { @"C:\new\SessionDeck.exe" }, spawned);
        Assert.Empty(quitAsked);

        Assert.Equal(GatewayProcess.Outcome.Started, GatewayProcess.EnsureRunning(r, @"C:\new\SessionDeck.exe", _ => false, q => quitAsked.Add(q.Pid), spawned.Add, TimeSpan.Zero));
        Assert.Equal(2, spawned.Count);

        Assert.Equal(GatewayProcess.Outcome.Kept, GatewayProcess.EnsureRunning(r, @"C:\old\SessionDeck.exe", _ => true, q => quitAsked.Add(q.Pid), spawned.Add, TimeSpan.Zero));
        Assert.Equal(2, spawned.Count);
        Assert.Empty(quitAsked);

        bool alive = true;
        Assert.Equal(GatewayProcess.Outcome.Replaced, GatewayProcess.EnsureRunning(r, @"C:\new\SessionDeck.exe", _ => alive, q => { quitAsked.Add(q.Pid); alive = false; }, spawned.Add, TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { 42 }, quitAsked);
        Assert.Equal(3, spawned.Count);
    }

    [Fact]
    public void A_damaged_or_missing_record_reads_as_none()
    {
        string path = Path.Combine(_dir, "gateway.json");
        Assert.Null(GatewayProcess.ReadRecord(path));
        File.WriteAllText(path, "{ nope");
        Assert.Null(GatewayProcess.ReadRecord(path));
        File.WriteAllText(path, "{\"Pid\":5,\"StartTicks\":1,\"Port\":7420,\"Exe\":\"x\",\"AdminToken\":\"a\"}");
        Assert.Equal(5, GatewayProcess.ReadRecord(path)!.Pid);
    }
}
