using Xunit;

namespace SessionDeck.Tests;

public class TopLevelShellsTests
{
    [Fact]
    public void Counts_each_shell_chain_once_and_ignores_non_shells()
    {
        var table = new Dictionary<int, (int Parent, string Name)>
        {
            [100] = (1, "claude.exe"),
            [200] = (100, "bash.exe"),      // background shell: bash -> bash -> conhost
            [201] = (200, "bash.exe"),
            [202] = (201, "conhost.exe"),
            [300] = (100, "pwsh.exe"),      // a second chain
            [301] = (300, "cmd.exe"),
            [400] = (100, "node.exe"),      // not a shell
            [500] = (2, "bash.exe"),        // someone else's shell
        };
        Assert.Equal(2, Native.TopLevelShells(100, table));
        Assert.Equal(0, Native.TopLevelShells(400, table));
        Assert.Equal(0, Native.TopLevelShells(999, table));
    }
}
