using System.IO;
using SessionDeck.Gateway;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class PhoneEndStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-phoneend-" + Guid.NewGuid().ToString("N"));
    string File => Path.Combine(_dir, PhoneEndStore.FileName);

    public PhoneEndStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static readonly DateTime T0 = new(2026, 11, 13, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_end_is_kept_by_host_until_a_day_has_passed_and_the_deck_reads_it_back()
    {
        var store = new PhoneEndStore(File);
        store.Mark("host00000001", "sess-1", T0);
        store.Mark("host00000002", "sess-2", T0.AddHours(2));
        var read = PhoneEndStore.Read(File);
        Assert.Equal(("sess-1", "sess-2"), (read["HOST00000001"].SessionId, read["host00000002"].SessionId));

        new PhoneEndStore(File).Mark("host00000003", "sess-3", T0.AddHours(25));
        Assert.Equal(new[] { "host00000002", "host00000003" }, PhoneEndStore.Read(File).Keys.OrderBy(k => k));
        Assert.Empty(PhoneEndStore.Read(Path.Combine(_dir, "missing.json")));
        System.IO.File.WriteAllText(File, "{ not json");
        Assert.Empty(PhoneEndStore.Read(File));
    }

    [Fact]
    public void The_deck_ends_what_still_runs_removes_what_has_stopped_and_forgets_each_session()
    {
        var ended = new Dictionary<string, PhoneEndStore.Ended>(StringComparer.OrdinalIgnoreCase)
        {
            ["running00001"] = new("sess-run", 1),
            ["stopped00001"] = new("sess-stop", 1),
            ["neversession"] = new("sess-from-phone", 1),
            ["notondeck001"] = new("sess-gone", 1),
            ["donealready1"] = new("sess-done", 1),
        };
        var hosts = new[]
        {
            new HostRecord { Id = "running00001", SessionId = "sess-run" },
            new HostRecord { Id = "stopped00001", SessionId = "sess-stop", ExitCode = 0 },
            new HostRecord { Id = "neversession", SessionId = "" },
            new HostRecord { Id = "donealready1", SessionId = "sess-done", ExitCode = 0 },
            new HostRecord { Id = "untouched001", SessionId = "sess-other" },
        };
        var steps = PhoneEndStore.Plan(ended, hosts, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "donealready1" });
        Assert.Equal(new[]
        {
            ("running00001", "sess-run", true, false),
            ("stopped00001", "sess-stop", false, true),
            ("neversession", "sess-from-phone", true, false),
        }, steps.Select(s => (s.Host.Id, s.SessionId, s.Kill, s.Remove)));
    }
}
