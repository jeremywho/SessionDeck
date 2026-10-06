using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SessionDeck;

/// <summary>
/// Codex does not replace its own binary the way Claude does: the TUI learns of a newer release,
/// records it in <c>~/.codex/version.json</c>, and shows a prompt at startup until someone runs
/// <c>codex update</c>. A session restarted while that gap is open boots straight into the prompt,
/// and taking the update from there exits the TUI. So the update runs here first, in its own
/// process, and sessions are restarted onto the binary it leaves behind.
/// </summary>
internal static class CodexUpdater
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    static string _attemptedFor = "";
    static DateTime _attemptedAt = DateTime.MinValue;

    public static string VersionFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "version.json");

    /// <summary>The newest release the Codex TUI has seen, or empty when it has not recorded one.</summary>
    public static string LatestAvailable()
    {
        try { return File.Exists(VersionFile) ? ParseLatest(File.ReadAllText(VersionFile)) : ""; }
        catch { return ""; }
    }

    internal static string ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("latest_version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>The binary on disk is older than the newest release Codex knows about.</summary>
    public static bool NeedsUpdate(string latest, string installed) => InstalledVersions.Compare(installed, latest);

    public static bool NeedsUpdate() => NeedsUpdate(LatestAvailable(), InstalledVersions.Codex);

    /// <summary>
    /// Bring the installed Codex up to the newest release before anything resumes a session on it.
    /// Returns true when the binary is current afterwards. A failed attempt is not retried for an
    /// hour, so a broken download does not run on every tick.
    /// </summary>
    public static async Task<bool> EnsureCurrentAsync()
    {
        string latest = LatestAvailable();
        if (!NeedsUpdate(latest, InstalledVersions.Codex)) return true;
        await Gate.WaitAsync();
        try
        {
            if (!NeedsUpdate(latest, InstalledVersions.Codex)) return true;
            if (_attemptedFor == latest && DateTime.UtcNow - _attemptedAt < RetryAfter) return false;
            _attemptedFor = latest;
            _attemptedAt = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            var (exit, tail) = await Task.Run(() => RunUpdate());
            InstalledVersions.RequeryNow();
            bool current = !NeedsUpdate(latest, InstalledVersions.Codex);
            PerformanceLog.Write($"codex-update latest={latest} exit={exit} {sw.Elapsed.TotalSeconds:0}s installed-now={InstalledVersions.Codex} current={current}{(current ? "" : " tail=" + tail)}");
            return current;
        }
        finally { Gate.Release(); }
    }

    static (int Exit, string Tail) RunUpdate()
    {
        try
        {
            var psi = new ProcessStartInfo(SessionLauncher.ResolvePowerShell())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoLogo", "-NonInteractive", "-Command", "codex update" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (-1, "no process");
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds)) { try { p.Kill(true); } catch { } return (-2, "timed out"); }
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (p.ExitCode, string.Join(" | ", lines.TakeLast(3)));
        }
        catch (Exception ex) { App.LogError(ex); return (-3, ex.Message); }
    }
}
