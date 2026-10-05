using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SessionDeck;

/// <summary>
/// Per-user install to %LOCALAPPDATA%\Programs\SessionDeck (like Claude Code / VS Code) — the
/// app can rewrite its own exe with no admin, so auto-update is seamless. On first run from anywhere
/// else (a download), it copies itself there, drops a Start Menu shortcut, and relaunches from there.
/// Dormant for dev builds (run from a \bin\ folder), when --no-install is passed, or SD_NO_INSTALL=1.
/// </summary>
internal static class Installer
{
    public static string InstallDir =>
        Environment.GetEnvironmentVariable("SD_INSTALL_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "Programs", "SessionDeck");

    public static string InstalledExe => Path.Combine(InstallDir, "SessionDeck.exe");

    /// <summary>Are we running as the copy that lives in the install dir?</summary>
    public static bool IsInstalledInstance()
    {
        try
        {
            var p = Environment.ProcessPath;
            return p != null &&
                   string.Equals(Path.GetFullPath(p), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Copied ourselves into place and launched that copy? Then the caller should exit.</summary>
    public static bool MaybeSelfInstall(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return false;
            if (IsInstalledInstance()) return false;                                     // already the installed copy
            if (args.Contains("--no-install")) return false;
            if (Environment.GetEnvironmentVariable("SD_NO_INSTALL") == "1") return false;
            if (exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) return false; // dev / IDE build

            Directory.CreateDirectory(InstallDir);
            if (!CopyWithRetry(exe, InstalledExe, InstalledExeBusyWait, TimeSpan.FromMilliseconds(250)))
            {
                // The installed copy is still running (or still exiting). Running this copy in place
                // instead would only exit on the single-instance mutex, silently. Say so.
                PerformanceLog.Write($"self-install: {InstalledExe} is in use after {InstalledExeBusyWait.TotalSeconds:0}s; not installed");
                System.Windows.MessageBox.Show(
                    "Session Deck is still running, so this copy could not be installed over it.\n\nClose the running Session Deck (tray icon → Exit) and launch this file again.",
                    "Session Deck", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return true;
            }
            TryCreateStartMenuShortcut();
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { App.LogError(ex); return false; }   // install failed -> just run in place
    }

    static readonly TimeSpan InstalledExeBusyWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Copy over a file that may still be held by a process on its way out, retrying until
    /// <paramref name="wait"/> has passed. Returns false when the target stayed in use.
    /// </summary>
    internal static bool CopyWithRetry(string from, string to, TimeSpan wait, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try { File.Copy(from, to, overwrite: true); return true; }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(step); }
            catch (IOException) { return false; }
        }
    }

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "SessionDeck";

    /// <summary>
    /// Register (or unregister) the installed exe under HKCU ...\CurrentVersion\Run — per-user,
    /// no admin. Always points at InstalledExe; callers gate on IsInstalledInstance() so dev
    /// builds never touch the key.
    /// </summary>
    public static void SyncRunAtLogin(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled) key.SetValue(RunValueName, $"\"{InstalledExe}\"");
            else if (key.GetValue(RunValueName) != null) key.DeleteValue(RunValueName);
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    static void TryCreateStartMenuShortcut()
    {
        try
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string lnk = Path.Combine(programs, "Session Deck.lnk");
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            var sc = shell.CreateShortcut(lnk);
            sc.TargetPath = InstalledExe;
            sc.WorkingDirectory = InstallDir;
            sc.IconLocation = InstalledExe + ",0";
            sc.Description = "Session Deck — Claude Code and Codex sessions side by side";
            sc.Save();
        }
        catch (Exception ex) { App.LogError(ex); }
    }
}
