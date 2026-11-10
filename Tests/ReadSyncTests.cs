using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using SessionDeck.Gateway;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>Read marks travel both ways: the phone's reach the deck through the gateway's file, the deck's reach the phone in the session list.</summary>
public class ReadSyncTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-readsync-" + Guid.NewGuid().ToString("N"));
    static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    static DateTime At(int minutes) => T0.AddMinutes(minutes);
    string StorePath => Path.Combine(_dir, PhoneReadStore.FileName);

    public ReadSyncTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void A_phones_mark_only_moves_forward_and_survives_a_restart()
    {
        var store = new PhoneReadStore(StorePath);
        Assert.True(store.Mark("s1", At(5)));
        Assert.False(store.Mark("s1", At(3)));
        Assert.False(store.Mark("", At(9)));
        Assert.True(store.Mark("s2", At(6)));
        Assert.Equal(ReadLedger.Ms(At(5)), store.Snapshot()["s1"]);

        var reopened = new PhoneReadStore(StorePath);
        Assert.Equal(ReadLedger.Ms(At(5)), reopened.Snapshot()["S1"]);
        Assert.Equal(ReadLedger.Ms(At(6)), PhoneReadStore.Read(StorePath)["s2"]);
    }

    [Fact]
    public void Marks_older_than_a_month_are_dropped_and_a_missing_or_broken_file_reads_as_empty()
    {
        var store = new PhoneReadStore(StorePath);
        store.Mark("old", At(0));
        store.Mark("new", At(0).AddDays(31));
        Assert.Equal(new[] { "new" }, store.Snapshot().Keys);

        Assert.Empty(PhoneReadStore.Read(Path.Combine(_dir, "nowhere.json")));
        File.WriteAllText(StorePath, "{ not json");
        Assert.Empty(PhoneReadStore.Read(StorePath));
        Assert.Empty(new PhoneReadStore(StorePath).Snapshot());
    }

    [Fact]
    public void The_deck_folds_a_phones_marks_into_its_ledger_keeping_the_later_one()
    {
        var mine = new Dictionary<string, long> { ["s1"] = ReadLedger.Ms(At(10)), ["s2"] = ReadLedger.Ms(At(1)) };
        var ledger = new ReadLedger(mine);
        bool changed = ledger.Merge(new Dictionary<string, long> { ["s1"] = ReadLedger.Ms(At(4)), ["s2"] = ReadLedger.Ms(At(7)), ["s3"] = ReadLedger.Ms(At(2)), [""] = 5 });
        Assert.True(changed);
        Assert.Equal(ReadLedger.Ms(At(10)), mine["s1"]);
        Assert.Equal(ReadLedger.Ms(At(7)), mine["s2"]);
        Assert.Equal(ReadLedger.Ms(At(2)), mine["s3"]);
        Assert.False(mine.ContainsKey(""));
        Assert.False(ledger.IsUnread("s2", At(6)));
        Assert.True(ledger.IsUnread("s2", At(8)));
        Assert.False(ledger.Merge(new Dictionary<string, long> { ["s2"] = ReadLedger.Ms(At(7)) }));
    }

    [Fact]
    public void The_session_list_carries_the_later_of_the_decks_and_the_phones_marks()
    {
        var settings = new Settings { ReadAt = new() { ["s1"] = 500, ["s2"] = 100 } };
        var phone = new Dictionary<string, long> { ["s2"] = 900, ["s3"] = 300 };
        Assert.Equal(500, SessionCatalog.ReadAtMs("s1", settings, phone));
        Assert.Equal(900, SessionCatalog.ReadAtMs("s2", settings, phone));
        Assert.Equal(300, SessionCatalog.ReadAtMs("s3", settings, phone));
        Assert.Equal(0, SessionCatalog.ReadAtMs("s4", settings, phone));
        Assert.Equal(0, SessionCatalog.ReadAtMs("", settings, phone));
        Assert.Equal(500, SessionCatalog.ReadAtMs("s1", settings, null));
        Assert.Equal(0, SessionCatalog.ReadAtMs("s1", new Settings(), null));

        var host = new HostRecord { Id = "h1", SessionId = "s2", Provider = "Claude", AgentStatus = "idle", LastEvent = "Stop", StatusAt = At(0), Protocol = 4, Title = "alpha", StartedAt = At(-60) };
        var info = new SessionInfo { SessionId = "s2", Status = "idle", Kind = "interactive", Name = "alpha", Provider = SessionProvider.Claude, StatusUpdatedAt = At(0).ToLocalTime(), UpdatedAt = At(0).ToLocalTime() };
        var entry = new SessionCatalog().Build(new() { (host, info) }, settings, At(1), phone).Single();
        Assert.Equal(900, entry.ReadAtMs);
        Assert.Contains("\"readAtMs\":900", SessionCatalog.ToJson(new[] { entry }));
    }

    [Fact]
    public async Task A_paired_phone_reports_a_session_read_and_nobody_else_can()
    {
        var devices = new DeviceStore(Path.Combine(_dir, "devices.json"));
        await using var server = new GatewayServer(IPAddress.Loopback, 0, devices, () => new List<CatalogEntry>(), _ => null,
            name => typeof(GatewayServer).Assembly.GetManifestResourceStream("www/" + name), "ADMINSECRET", "relay-key-for-tests");
        await server.StartAsync();
        HttpClient Client(string? token)
        {
            var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"), Timeout = TimeSpan.FromSeconds(10) };
            if (token != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }
        StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

        using var phone = Client("relay-key-for-tests");
        Assert.Equal(HttpStatusCode.NotImplemented, (await phone.PostAsync("/read", Json("{\"sessionId\":\"s1\"}"))).StatusCode);

        var marked = new List<string>();
        server.MarkRead = id => { marked.Add(id); return true; };
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync("/read", Json("{\"sessionId\":\"s1\"}"))).StatusCode);
        Assert.Equal(new[] { "s1" }, marked);

        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsync("/read", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsync("/read", Json("not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsync("/read", Json("{\"sessionId\":\"" + new string('x', 200) + "\"}"))).StatusCode);
        using var stranger = Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsync("/read", Json("{\"sessionId\":\"s9\"}"))).StatusCode);
        Assert.Equal(new[] { "s1" }, marked);
    }
}
