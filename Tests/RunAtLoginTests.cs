using System.IO;
using Microsoft.Win32;
using Xunit;

namespace SessionDeck.Tests;

public class RunAtLoginTests
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "SessionDeck";

    [Fact]
    public void Sync_writes_quoted_installed_path_and_removes_it()
    {
        // Redirect the install dir so the asserted path is ours, not the real install.
        string dir = Path.Combine(Path.GetTempPath(), "csm-runkey-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SD_INSTALL_DIR", dir);

        // The test drives the REAL HKCU Run value (there is only one name) — preserve whatever
        // the installed instance may have registered and put it back no matter what.
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath)!;
        var original = runKey.GetValue(RunValueName);
        try
        {
            Installer.SyncRunAtLogin(true);
            Assert.Equal($"\"{Path.Combine(dir, "SessionDeck.exe")}\"", runKey.GetValue(RunValueName));

            Installer.SyncRunAtLogin(false);
            Assert.Null(runKey.GetValue(RunValueName));

            Installer.SyncRunAtLogin(false);   // disabling when absent must not throw
            Assert.Null(runKey.GetValue(RunValueName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SD_INSTALL_DIR", null);
            if (original != null) runKey.SetValue(RunValueName, original);
            else if (runKey.GetValue(RunValueName) != null) runKey.DeleteValue(RunValueName);
        }
    }
}
