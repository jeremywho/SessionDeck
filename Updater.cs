using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace SessionDeck;

/// <summary>
/// gh-based self-update. Checks the latest GitHub Release (private repo — auth via the installed `gh`
/// CLI, so no token in the app), downloads + stages the new self-contained exe, and swaps it in with a
/// rename-and-relaunch. A boot-counter marker rolls back to the previous exe if a freshly-updated build
/// crash-loops on startup. Entirely dormant unless we're the installed instance.
/// </summary>
internal static class Updater
{
    const string Repo = "jeremywho/SessionDeck";

    static string StagingDir => Path.Combine(Installer.InstallDir, "staging");
    static string MarkerPath => Path.Combine(Installer.InstallDir, "update.pending.json");

    /// <summary>Fired (off the UI thread) once a newer release has been downloaded and staged.</summary>
    public static event Action? UpdateStaged;
    public static string? StagedTag { get; private set; }
    static string? _stagedExe;

    public static Version Current =>
        Norm(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    static Version Norm(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>
    /// Shortest gap between real checks, whoever asks. The periodic timer alone can't get near this,
    /// but showing the window also triggers a check — and without a floor, opening and closing the
    /// window repeatedly would spawn a pair of `gh` processes every time.
    /// </summary>
    public static readonly TimeSpan MinCheckGap = TimeSpan.FromMinutes(5);

    static DateTime _lastCheck = DateTime.MinValue;

    /// <summary>Whether enough time has passed since the last check. Pure, so the debounce is testable
    /// without spawning anything.</summary>
    public static bool ShouldCheckNow(DateTime lastCheck, DateTime now, TimeSpan gap) =>
        lastCheck == DateTime.MinValue || now - lastCheck >= gap;

    /// <summary>Ask GitHub (via gh) for the latest release; if it's newer, download + stage it. Idempotent.</summary>
    public static async Task CheckAsync()
    {
        if (!Installer.IsInstalledInstance()) return;   // dormant in dev / uninstalled runs
        if (!ShouldCheckNow(_lastCheck, DateTime.UtcNow, MinCheckGap)) return;
        // Stamped before the work, not after: a slow or failing `gh` shouldn't let a second trigger
        // pile another pair of processes on top of the one already running.
        _lastCheck = DateTime.UtcNow;
        try
        {
            string? tag = (await Gh($"api repos/{Repo}/releases/latest --jq .tag_name"))?.Trim();
            if (string.IsNullOrEmpty(tag) || !TryParseTag(tag, out var v)) return;   // no release / unparsable
            if (v <= Current || StagedTag == tag) return;                            // up to date / already staged

            string asset = $"SessionDeck-{tag}.exe";
            Directory.CreateDirectory(StagingDir);
            string dest = Path.Combine(StagingDir, asset);
            await Gh($"release download {tag} --repo {Repo} --pattern \"{asset}\" --dir \"{StagingDir}\" --clobber");
            if (!File.Exists(dest) || new FileInfo(dest).Length < 1_000_000) return;   // sanity: a real exe

            _stagedExe = dest;
            StagedTag = tag;
            UpdateStaged?.Invoke();
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    /// <summary>Swap the staged exe into place and relaunch it. The caller shuts the app down right after.</summary>
    public static bool Apply()
    {
        if (_stagedExe == null || !File.Exists(_stagedExe)) return false;
        try
        {
            string cur = Installer.InstalledExe;
            string old = cur + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(cur, old);            // Windows allows renaming a running exe (delete would fail)
            File.Copy(_stagedExe, cur);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new Marker { Tag = StagedTag ?? "", Old = old }));
            try { File.Delete(_stagedExe); } catch { }
            Process.Start(new ProcessStartInfo(cur, "--updated") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { App.LogError(ex); return false; }
    }

    /// <summary>On startup: if a prior post-update boot never confirmed success, revert to the .old exe.</summary>
    public static void CheckRollbackOnStartup()
    {
        Marker? m;
        try
        {
            if (!File.Exists(MarkerPath)) return;
            m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath));
        }
        catch { TryDelete(MarkerPath); return; }
        if (m == null) { TryDelete(MarkerPath); return; }

        m.Boots++;
        if (m.Boots < 2)   // first post-update boot: give it a chance — ConfirmStartupOk() clears this on success
        {
            try { File.WriteAllText(MarkerPath, JsonSerializer.Serialize(m)); } catch { }
            return;
        }

        // second boot with an uncleared marker => the updated build failed to start => revert
        try
        {
            string cur = Installer.InstalledExe;
            if (File.Exists(m.Old))
            {
                string bad = cur + ".bad";
                TryDelete(bad);
                File.Move(cur, bad);      // rename the running (bad) exe out of the way
                File.Move(m.Old, cur);    // restore the last-good exe
                TryDelete(MarkerPath);
                Process.Start(new ProcessStartInfo(cur) { UseShellExecute = true });
                Environment.Exit(0);
            }
            TryDelete(MarkerPath);
        }
        catch (Exception ex) { App.LogError(ex); TryDelete(MarkerPath); }
    }

    /// <summary>Call once the app is confirmably up: clears the rollback marker and drops the .old / .bad exes.</summary>
    public static void ConfirmStartupOk()
    {
        try
        {
            string cur = Installer.InstalledExe;
            if (File.Exists(MarkerPath))
            {
                Marker? m = null;
                try { m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath)); } catch { }
                TryDelete(MarkerPath);
                if (m != null) TryDelete(m.Old);
            }
            TryDelete(cur + ".bad");
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    static bool TryParseTag(string tag, out Version v)
    {
        v = new Version(0, 0, 0);
        string s = tag.TrimStart('v', 'V');
        int dash = s.IndexOf('-'); if (dash >= 0) s = s[..dash];   // drop any -prerelease suffix
        if (!Version.TryParse(s, out var parsed)) return false;
        v = Norm(parsed);
        return true;
    }

    static async Task<string?> Gh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("gh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            // Both pipes are redirected, so both must be drained concurrently: reading stdout to
            // completion first deadlocks if gh fills the (~4KB) stderr pipe buffer while we're
            // not reading it — gh blocks writing stderr, we block waiting for stdout EOF.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdout, stderr, p.WaitForExitAsync());
            return p.ExitCode == 0 ? stdout.Result : null;
        }
        catch { return null; }   // gh not installed / not on PATH
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    sealed class Marker
    {
        public string Tag { get; set; } = "";
        public string Old { get; set; } = "";
        public int Boots { get; set; }
    }
}
