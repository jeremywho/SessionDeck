using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SessionDeck;

/// <summary>
/// The CLI versions installed on this machine, as the shell that launches sessions would resolve
/// them. A running session reports its own version in its transcript; when that is older than what
/// is installed, the CLI has updated itself underneath the session and only a restart picks it up.
/// </summary>
internal static class InstalledVersions
{
    static readonly Regex VersionPattern = new(@"\d+\.\d+\.\d+", RegexOptions.Compiled);
    static int _running;

    public static string Claude { get; private set; } = "";
    public static string Codex { get; private set; } = "";
    public static DateTime CheckedAt { get; private set; } = DateTime.MinValue;

    /// <summary>Raised off the UI thread when either version differs from the previous check.</summary>
    public static event Action? Changed;

    public static string For(SessionProvider provider) => provider == SessionProvider.Codex ? Codex : Claude;

    /// <summary>True when the session's reported version is older than the installed one.</summary>
    public static bool IsBehind(SessionProvider provider, string running) => Compare(running, For(provider));

    internal static bool Compare(string running, string installed)
    {
        if (installed.Length == 0 || running.Length == 0) return false;
        var r = VersionPattern.Match(running);
        var i = VersionPattern.Match(installed);
        if (!r.Success || !i.Success) return false;
        return Version.TryParse(r.Value, out var rv) && Version.TryParse(i.Value, out var iv) && rv < iv;
    }

    public static void Refresh()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                string claude = Query("claude");
                string codex = Query("codex");
                bool changed = claude != Claude || codex != Codex;
                if (claude.Length > 0) Claude = claude;
                if (codex.Length > 0) Codex = codex;
                CheckedAt = DateTime.UtcNow;
                PerformanceLog.Write($"installed-versions claude={Claude} codex={Codex}{(changed ? " changed" : "")}");
                if (changed) Changed?.Invoke();
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    /// <summary>Runs <c>&lt;cli&gt; --version</c> under the same PowerShell the hosts use, so the same profile and PATH apply.</summary>
    static string Query(string cli)
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
            foreach (var a in new[] { "-NoLogo", "-NonInteractive", "-Command", cli + " --version" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "";
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return ""; }
            var m = VersionPattern.Match(output);
            return m.Success ? m.Value : "";
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return "";
        }
    }
}
