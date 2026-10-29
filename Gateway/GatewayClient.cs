using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SessionDeck.Gateway;

internal sealed record DeviceSummary(string Id, string Name, DateTime PairedAtUtc, DateTime LastSeenUtc);

/// <summary>What goes in the pairing QR: the address a phone on the LAN dials and the one-time code.</summary>
internal static class QrPayload
{
    public static string For(string host, int port, string code) => $"sessiondeck://pair?host={host}&port={port}&code={code}";

    /// <summary>This machine's address on the LAN: the first IPv4 of an interface that is up and not loopback or virtual-switch-like; empty when none.</summary>
    public static string LanAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && a.PrefixOrigin != PrefixOrigin.WellKnown) return a.Address.ToString();
            }
        }
        catch (Exception ex) { App.LogError(ex); }
        return "";
    }
}

/// <summary>The deck's own way to ask the running gateway for a code, the device list, or a revoke: loopback, admin token from gateway.json.</summary>
internal sealed class GatewayClient
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly GatewayRecord _record;
    readonly HttpClient _http;

    public GatewayClient(GatewayRecord record)
    {
        _record = record;
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{record.Port}/"), Timeout = TimeSpan.FromSeconds(3) };
        _http.DefaultRequestHeaders.Add("X-Admin-Token", record.AdminToken);
    }

    public static GatewayClient? Running() =>
        GatewayProcess.ReadRecord() is { } r && GatewayProcess.IsAlive(r) ? new GatewayClient(r) : null;

    public int Port => _record.Port;

    public async Task<(string Code, DateTime ExpiresUtc)?> IssueCodeAsync()
    {
        using var res = await _http.PostAsync("admin/pair-code", new StringContent(""));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("code").GetString() ?? "", doc.RootElement.GetProperty("expiresAtUtc").GetDateTime());
    }

    public async Task<List<DeviceSummary>> DevicesAsync()
    {
        using var res = await _http.GetAsync("devices");
        if (!res.IsSuccessStatusCode) return new();
        return JsonSerializer.Deserialize<List<DeviceSummary>>(await res.Content.ReadAsStringAsync(), Json) ?? new();
    }

    public async Task<bool> RevokeAsync(string deviceId)
    {
        using var res = await _http.DeleteAsync("devices/" + deviceId);
        return res.IsSuccessStatusCode;
    }
}
