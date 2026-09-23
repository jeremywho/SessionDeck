using Xunit;

namespace SessionDeck.Tests;

public class InstalledVersionsTests
{
    [Theory]
    [InlineData("2.0.1", "2.0.2", true)]
    [InlineData("2.0.2", "2.0.2", false)]
    [InlineData("2.0.3", "2.0.2", false)]
    [InlineData("1.9.9 (Claude Code)", "2.0.0", true)]
    [InlineData("codex-cli 0.156.0", "0.157.0", true)]
    [InlineData("", "2.0.2", false)]
    [InlineData("2.0.1", "", false)]
    [InlineData("garbage", "2.0.2", false)]
    public void Behind_only_when_running_is_older_than_installed(string running, string installed, bool expected)
    {
        Assert.Equal(expected, InstalledVersions.Compare(running, installed));
    }
}
