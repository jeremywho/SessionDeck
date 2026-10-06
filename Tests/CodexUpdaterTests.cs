using Xunit;

namespace SessionDeck.Tests;

public class CodexUpdaterTests
{
    [Fact]
    public void The_latest_release_is_read_from_codex_version_file()
    {
        Assert.Equal("0.160.1", CodexUpdater.ParseLatest("{\"latest_version\":\"0.160.1\",\"last_checked_at\":\"2026-10-06T18:34:58Z\",\"dismissed_version\":\"0.156.0\"}"));
        Assert.Equal("", CodexUpdater.ParseLatest("{\"last_checked_at\":\"2026-10-06T18:34:58Z\"}"));
        Assert.Equal("", CodexUpdater.ParseLatest("not json"));
    }

    [Theory]
    [InlineData("0.160.1", "codex-cli 0.160.0", true)]
    [InlineData("0.160.1", "codex-cli 0.160.1", false)]
    [InlineData("0.160.0", "codex-cli 0.160.1", false)]
    [InlineData("", "codex-cli 0.160.1", false)]
    [InlineData("0.160.1", "", false)]
    public void An_update_is_needed_only_when_the_binary_is_older_than_the_newest_release(string latest, string installed, bool expected)
        => Assert.Equal(expected, CodexUpdater.NeedsUpdate(latest, installed));
}
