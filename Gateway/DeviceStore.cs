using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SessionDeck.Gateway;

internal sealed record PairedDevice(string Id, string Name, string TokenHash, DateTime PairedAtUtc, DateTime LastSeenUtc);

/// <summary>
/// The phones allowed to talk to the gateway. A device is let in with a short code shown on the
/// desktop, good for five minutes and for as many devices as are paired in that time (an iPad and a
/// phone from one code), and from then on by a token only it holds; the file keeps the token's hash,
/// so reading the file does not give the token away.
/// </summary>
internal sealed class DeviceStore
{
    const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(1);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly string _path;
    readonly object _gate = new();
    List<PairedDevice> _devices;
    string? _code;
    DateTime _codeIssuedUtc;

    public DeviceStore(string path)
    {
        _path = path;
        _devices = Load(path);
    }

    public IReadOnlyList<PairedDevice> Devices { get { lock (_gate) return _devices.ToList(); } }

    /// <summary>A fresh code, replacing any outstanding one; good for five minutes, for every device paired in that time.</summary>
    public string IssuePairingCode(DateTime nowUtc)
    {
        var chars = new char[8];
        var bytes = RandomNumberGenerator.GetBytes(chars.Length);
        for (int i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        lock (_gate)
        {
            _code = new string(chars);
            _codeIssuedUtc = nowUtc;
            return _code;
        }
    }

    public DateTime? CodeExpiresUtc { get { lock (_gate) return _code == null ? null : _codeIssuedUtc + CodeLifetime; } }

    public (string Token, PairedDevice Device)? Redeem(string code, string deviceName, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_code == null || nowUtc - _codeIssuedUtc > CodeLifetime || !string.Equals(code.Trim(), _code, StringComparison.OrdinalIgnoreCase)) return null;
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var device = new PairedDevice(Guid.NewGuid().ToString("N")[..12], string.IsNullOrWhiteSpace(deviceName) ? "Phone" : deviceName.Trim(), Hash(token), nowUtc, nowUtc);
            _devices.Add(device);
            Save();
            return (token, device);
        }
    }

    public PairedDevice? Authenticate(string? token, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(token)) return null;
        string hash = Hash(token);
        lock (_gate)
        {
            int i = _devices.FindIndex(d => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.TokenHash), Encoding.ASCII.GetBytes(hash)));
            if (i < 0) return null;
            var device = _devices[i];
            if (nowUtc - device.LastSeenUtc >= LastSeenWriteInterval)
            {
                device = device with { LastSeenUtc = nowUtc };
                _devices[i] = device;
                Save();
            }
            return device;
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (_gate)
        {
            int removed = _devices.RemoveAll(d => d.Id == deviceId);
            if (removed > 0) Save();
            return removed > 0;
        }
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    void Save() => AtomicFile.Write(_path, JsonSerializer.Serialize(_devices, Json));

    static List<PairedDevice> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(path)) is { } list) return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { App.LogError(ex); }
        return new List<PairedDevice>();
    }
}
