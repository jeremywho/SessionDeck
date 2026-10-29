using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SessionDeck.Host;

namespace SessionDeck.Gateway;

/// <summary>What the running gateway wrote about itself: <c>gateway.json</c> under the data dir.</summary>
internal sealed record GatewayRecord(int Pid, long StartTicks, int Port, string Exe, string AdminToken);

/// <summary>
/// The gateway runs as <c>SessionDeck.exe --gateway</c> from a stamped copy, like a host, so an app
/// update never finds its exe in use. The deck starts it when none is alive and replaces one that
/// runs an older copy; the gateway itself outlives the deck window.
/// </summary>
internal static class GatewayProcess
{
    public static string RecordPath => Path.Combine(Settings.DataDir, "gateway.json");
    public static string LogPath => Path.Combine(Path.GetTempPath(), "sessiondeck-gateway.log");
    public static string MutexName => Settings.InstanceMutexName + "_Gateway";

    public static GatewayRecord? ReadRecord() => ReadRecord(RecordPath);

    internal static GatewayRecord? ReadRecord(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<GatewayRecord>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    public static void WriteRecord(GatewayRecord r) => AtomicFile.Write(RecordPath, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));

    public static bool IsAlive(GatewayRecord r)
    {
        Process p;
        try { p = Process.GetProcessById(r.Pid); }
        catch { return false; }
        using (p)
        {
            try { if (p.HasExited) return false; } catch { }
            long? start = null;
            try { start = p.StartTime.ToUniversalTime().Ticks; } catch { }
            string name = "";
            try { name = p.ProcessName; } catch { }
            return HostLiveness.Alive(r.StartTicks, start, name, HostLiveness.BootTicksUtc());
        }
    }

    /// <summary>A live gateway from another copy of the exe is one an update left behind.</summary>
    public static bool ShouldReplace(GatewayRecord r, string currentExe) =>
        !string.Equals(Path.GetFullPath(r.Exe), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase);

    public static string Spawnable() => HostManager.HostExe();

    /// <summary>Called by the deck at startup: start a gateway when none runs; replace one from an older exe.</summary>
    public static void EnsureRunning()
    {
        try
        {
            string exe = Spawnable();
            var record = ReadRecord();
            if (record != null && IsAlive(record))
            {
                if (!ShouldReplace(record, exe)) return;
                AskToQuit(record);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && IsAlive(record)) Thread.Sleep(100);
            }
            Spawn(exe);
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    public static void AskToQuit(GatewayRecord r)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{r.Port}/admin/quit") { Content = new StringContent("") };
            req.Headers.Add("X-Admin-Token", r.AdminToken);
            http.Send(req);
        }
        catch { }
    }

    public static Process Spawn(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        psi.ArgumentList.Add("--gateway");
        SessionLauncher.StripFromChild(psi, HostManager.InheritedSessionMarkers(psi.Environment.Keys));
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start the gateway");
    }

    public static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:O} [{Environment.ProcessId}] {line}{Environment.NewLine}"); } catch { }
    }

    /// <summary>The <c>--gateway</c> entry point: serve until asked to quit. Returns the exit code.</summary>
    public static int Run()
    {
        using var mutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew) { Log("another gateway holds the mutex; exiting"); return 0; }
        var settings = Settings.Peek();
        if (!settings.Gateway.Enabled) { Log("gateway disabled in settings; exiting"); return 0; }

        var devices = new DeviceStore(Path.Combine(Settings.DataDir, "devices.json"));
        string adminToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var catalog = new SessionCatalog();
        var hosts = new Dictionary<string, HostRecord>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        List<CatalogEntry> current = new();

        GatewayServer? server = null;
        var quit = new TaskCompletionSource();
        var loop = new CatalogLoop(
            () =>
            {
                CodexScanner.Probe();
                var pairs = SessionPipeline.Pair(SessionPipeline.Scan(prune: false));
                var built = catalog.Build(pairs, Settings.Peek(), DateTime.UtcNow);
                lock (gate)
                {
                    hosts.Clear();
                    foreach (var (h, _) in pairs) hosts[h.Id] = h;
                    current = built;
                }
                return built;
            },
            _ => server?.CatalogChanged(),
            () => server?.Subscribers ?? 0);

        server = new GatewayServer(IPAddress.Any, settings.Gateway.Port, devices,
            () => { lock (gate) return current; },
            id => { lock (gate) return hosts.GetValueOrDefault(id); },
            name => typeof(GatewayServer).Assembly.GetManifestResourceStream("www/" + name),
            adminToken);
        server.QuitRequested += () => quit.TrySetResult();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };

        try
        {
            server.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"listen failed on port {settings.Gateway.Port}: {ex.Message}");
            return 1;
        }
        var self = Process.GetCurrentProcess();
        WriteRecord(new GatewayRecord(self.Id, self.StartTime.ToUniversalTime().Ticks, server.Port, Environment.ProcessPath ?? "", adminToken));
        Log($"listening on port {server.Port}");

        using var cts = new CancellationTokenSource();
        var running = Task.Run(async () =>
        {
            SessionPipeline.WarmUp();
            await loop.RunAsync(cts.Token);
        });
        quit.Task.GetAwaiter().GetResult();
        Log("quit requested");
        cts.Cancel();
        try { running.Wait(TimeSpan.FromSeconds(5)); } catch { }
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { if (ReadRecord()?.Pid == self.Id) File.Delete(RecordPath); } catch { }
        return 0;
    }
}
