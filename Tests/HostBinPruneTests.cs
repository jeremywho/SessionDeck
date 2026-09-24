using System.IO;
using Xunit;

namespace SessionDeck.Tests;

public class HostBinPruneTests
{
    [Fact]
    public void A_file_held_open_by_another_handle_reads_as_locked()
    {
        string path = Path.Combine(Path.GetTempPath(), "sd-lock-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        try
        {
            Assert.False(HostManager.IsLocked(path));
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.True(HostManager.IsLocked(path));
            Assert.False(HostManager.IsLocked(path));
        }
        finally { File.Delete(path); }
    }
}
