using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using SessionDeck;
using SessionDeck.Gateway;
using SessionDeck.Tests;

var dataDir = Path.Combine(Path.GetTempPath(), "sd-review-race-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("SD_DATA_DIR", dataDir);
var devices = new DeviceStore(Path.Combine(dataDir, "devices.json"));
var pairing = devices.Redeem(devices.IssuePairingCode(DateTime.UtcNow), "Review phone", DateTime.UtcNow)!.Value;
await using var host = new FakeHost();
var lookupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var lookupReleased = new ManualResetEventSlim();
await using var gateway = new GatewayServer(IPAddress.Loopback, 0, devices,
    () => new List<CatalogEntry>(),
    id => { lookupEntered.TrySetResult(); lookupReleased.Wait(TimeSpan.FromSeconds(10)); return host.Record; },
    _ => null, "review-admin");
await gateway.StartAsync();
using var phone = new ClientWebSocket();
var connecting = phone.ConnectAsync(new Uri($"ws://127.0.0.1:{gateway.Port}/sessions/{host.Record.Id}/attach?token={pairing.Token}"), CancellationToken.None);
await lookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
Console.WriteLine("Revoked during host lookup: " + gateway.Revoke(pairing.Device.Id));
lookupReleased.Set();
await connecting.WaitAsync(TimeSpan.FromSeconds(10));
var buf = new byte[65536];
var snapshot = await phone.ReceiveAsync(buf, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
Console.WriteLine("Frame after revocation: " + snapshot.MessageType);
await phone.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"input\",\"data\":\"after-revoke\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
var deadline = DateTime.UtcNow.AddSeconds(3);
while (host.Received.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
Console.WriteLine("Host received after revocation: " + string.Join(";", host.Received));
await phone.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
