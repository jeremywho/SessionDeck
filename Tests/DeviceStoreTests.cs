using System.IO;
using SessionDeck.Gateway;
using Xunit;

namespace SessionDeck.Tests;

public class DeviceStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-devices-" + Guid.NewGuid().ToString("N"));
    readonly string _path;
    static readonly DateTime T0 = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    public DeviceStoreTests() { Directory.CreateDirectory(_dir); _path = Path.Combine(_dir, "devices.json"); }
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void A_code_pairs_once_within_five_minutes_and_the_token_authenticates_until_revoked()
    {
        var store = new DeviceStore(_path);
        string code = store.IssuePairingCode(T0);
        Assert.Equal(8, code.Length);
        Assert.All(code, c => Assert.Contains(c, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"));
        Assert.Equal(T0.AddMinutes(5), store.CodeExpiresUtc);

        var paired = store.Redeem(code.ToLowerInvariant(), "Jeremy's iPhone", T0.AddMinutes(1));
        Assert.NotNull(paired);
        Assert.Equal(64, paired!.Value.Token.Length);
        Assert.Equal("Jeremy's iPhone", paired.Value.Device.Name);
        Assert.Null(store.Redeem(code, "again", T0.AddMinutes(2)));
        Assert.Null(store.CodeExpiresUtc);

        Assert.Equal(paired.Value.Device.Id, store.Authenticate(paired.Value.Token, T0.AddMinutes(3))!.Id);
        Assert.Null(store.Authenticate("nope", T0));
        Assert.Null(store.Authenticate("", T0));
        Assert.Null(store.Authenticate(null, T0));

        Assert.True(store.Revoke(paired.Value.Device.Id));
        Assert.False(store.Revoke(paired.Value.Device.Id));
        Assert.Null(store.Authenticate(paired.Value.Token, T0.AddMinutes(4)));
        Assert.Empty(store.Devices);
    }

    [Fact]
    public void An_expired_code_is_refused_and_a_new_code_replaces_the_old_one()
    {
        var store = new DeviceStore(_path);
        string code = store.IssuePairingCode(T0);
        Assert.Null(store.Redeem(code, "late", T0.AddMinutes(6)));
        string first = store.IssuePairingCode(T0);
        string second = store.IssuePairingCode(T0);
        Assert.Null(store.Redeem(first, "stale", T0.AddSeconds(1)));
        Assert.NotNull(store.Redeem(second, "fresh", T0.AddSeconds(1)));
    }

    [Fact]
    public void The_store_survives_a_reload_holds_hashes_not_tokens_and_updates_last_seen_sparingly()
    {
        var store = new DeviceStore(_path);
        var paired = store.Redeem(store.IssuePairingCode(T0), "Phone", T0)!.Value;
        string file = File.ReadAllText(_path);
        Assert.DoesNotContain(paired.Token, file);
        Assert.Contains(paired.Device.Id, file);

        var reloaded = new DeviceStore(_path);
        Assert.Equal(paired.Device.Id, reloaded.Authenticate(paired.Token, T0.AddSeconds(30))!.Id);
        Assert.Equal(T0, reloaded.Devices.Single().LastSeenUtc);
        reloaded.Authenticate(paired.Token, T0.AddMinutes(2));
        Assert.Equal(T0.AddMinutes(2), reloaded.Devices.Single().LastSeenUtc);
        Assert.Equal(T0.AddMinutes(2), new DeviceStore(_path).Devices.Single().LastSeenUtc);
    }

    [Fact]
    public void A_damaged_file_starts_the_store_empty_rather_than_failing()
    {
        File.WriteAllText(_path, "{ not json");
        var store = new DeviceStore(_path);
        Assert.Empty(store.Devices);
        Assert.NotNull(store.Redeem(store.IssuePairingCode(T0), "Phone", T0));
        Assert.Single(new DeviceStore(_path).Devices);
    }
}
