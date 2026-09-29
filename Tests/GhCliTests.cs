using Xunit;

namespace SessionDeck.Tests;

public class GhCliTests
{
    [Fact]
    public void Gh_is_found_in_a_path_value_read_after_launch()
    {
        const string path = @"C:\a;;%SystemRoot%\x; C:\Program Files\GitHub CLI\ ";
        var found = GhCli.FindOnPath(path, p => p.Equals(@"C:\Program Files\GitHub CLI\gh.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\Program Files\GitHub CLI\gh.exe", found);
    }

    [Fact]
    public void A_path_without_gh_finds_nothing() => Assert.Null(GhCli.FindOnPath(@"C:\a;C:\b", _ => false));
}
