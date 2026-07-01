using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClaudeSessionMonitor;

/// <summary>
/// Per-user install to %LOCALAPPDATA%\Programs\ClaudeSessionMonitor (like Claude Code / VS Code) — the
/// app can rewrite its own exe with no admin, so auto-update is seamless. On first run from anywhere
/// else (a download), it copies itself there, drops a Start Menu shortcut, and relaunches from there.
/// Dormant for dev builds (run from a \bin\ folder), when --no-install is passed, or CSM_NO_INSTALL=1.
/// </summary>
internal static class Installer
{
    public static string InstallDir =>
        Environment.GetEnvironmentVariable("CSM_INSTALL_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "Programs", "ClaudeSessionMonitor");

    public static string InstalledExe => Path.Combine(InstallDir, "ClaudeSessionMonitor.exe");

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
            if (Environment.GetEnvironmentVariable("CSM_NO_INSTALL") == "1") return false;
            if (exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) return false; // dev / IDE build

            Directory.CreateDirectory(InstallDir);
            File.Copy(exe, InstalledExe, overwrite: true);
            TryCreateStartMenuShortcut();
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { App.LogError(ex); return false; }   // install failed -> just run in place
    }

    static void TryCreateStartMenuShortcut()
    {
        try
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string lnk = Path.Combine(programs, "Claude Sessions.lnk");
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            var sc = shell.CreateShortcut(lnk);
            sc.TargetPath = InstalledExe;
            sc.WorkingDirectory = InstallDir;
            sc.IconLocation = InstalledExe + ",0";
            sc.Description = "Claude Sessions — live Claude Code session monitor";
            sc.Save();
        }
        catch (Exception ex) { App.LogError(ex); }
    }
}
