using Xunit;

namespace SessionDeck.Tests;

public class OutsideLinkTests
{
    static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\work\out\diagram.png", @"C:\work\out\clip.MP4", @"C:\work\out\walk through.html", @"C:\work\out\notes.md",
        @"C:\work\out\setup.exe", @"C:\work\out\run.ps1", @"C:\work\out\go.lnk", @"C:\work\out\data.bin", @"C:\work\src\a.cs", @"C:\work\out\odd#name.txt",
        @"C:\work\src\build.js", @"C:\work\src\tool.py", @"C:\work\src\run.bat", @"C:\work\src\setup.sh", @"C:\work\src\old.vbs",
    };
    static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase) { @"C:\work", @"C:\work\out", @"C:\work\src" };

    static (OutsideLink.Kind Kind, string Target) Decide(string? link) => OutsideLink.Decide(link, Files.Contains, Folders.Contains);

    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("HTTP://example.com", "HTTP://example.com")]
    [InlineData("  https://example.com  ", "https://example.com")]
    public void A_web_address_goes_to_the_browser(string link, string target) => Assert.Equal((OutsideLink.Kind.Web, target), Decide(link));

    [Theory]
    [InlineData("file:///C:/work/out/diagram.png", @"C:\work\out\diagram.png")]
    [InlineData("file:///C:/work/out/clip.MP4", @"C:\work\out\clip.MP4")]
    [InlineData("file:///C:/work/out/walk%20through.html", @"C:\work\out\walk through.html")]
    [InlineData("file:///C:/work/out/walk%20through.html#step-2", @"C:\work\out\walk through.html")]
    [InlineData("C:/work/out/notes.md", @"C:\work\out\notes.md")]
    [InlineData(@"C:\work\out\notes.md", @"C:\work\out\notes.md")]
    [InlineData("C:/work/out/../out/notes.md", @"C:\work\out\notes.md")]
    [InlineData("C:/work/out/odd#name.txt", @"C:\work\out\odd#name.txt")]
    [InlineData("C:/work/src/a.cs", @"C:\work\src\a.cs")]
    [InlineData("C:/work/src/a.cs#L10", @"C:\work\src\a.cs")]
    public void A_file_that_is_looked_at_or_edited_is_opened(string link, string target) => Assert.Equal((OutsideLink.Kind.Open, target), Decide(link));

    [Theory]
    [InlineData("file:///C:/work/out/setup.exe", @"C:\work\out\setup.exe")]
    [InlineData("file:///C:/work/out/run.ps1", @"C:\work\out\run.ps1")]
    [InlineData("file:///C:/work/out/go.lnk", @"C:\work\out\go.lnk")]
    [InlineData("file:///C:/work/out/data.bin", @"C:\work\out\data.bin")]
    [InlineData("C:/work/src/build.js", @"C:\work\src\build.js")]
    [InlineData("C:/work/src/tool.py", @"C:\work\src\tool.py")]
    [InlineData("C:/work/src/run.bat", @"C:\work\src\run.bat")]
    [InlineData("C:/work/src/setup.sh", @"C:\work\src\setup.sh")]
    [InlineData("C:/work/src/old.vbs", @"C:\work\src\old.vbs")]
    public void Anything_that_could_run_or_is_unknown_is_shown_in_its_folder_and_never_opened(string link, string target) => Assert.Equal((OutsideLink.Kind.Show, target), Decide(link));

    [Theory]
    [InlineData("file:///C:/work/out", @"C:\work\out")]
    [InlineData("file:///C:/work/out/", @"C:\work\out")]
    [InlineData("file:///C:/work/out/not-written-yet.png", @"C:\work\out")]
    public void A_folder_is_opened_and_a_file_that_is_not_there_opens_where_it_would_be(string link, string target) => Assert.Equal((OutsideLink.Kind.Folder, target), Decide(link));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file://fileserver/share/diagram.png")]
    [InlineData(@"\\fileserver\share\diagram.png")]
    [InlineData("//fileserver/share/diagram.png")]
    [InlineData(@"\\?\C:\work\out\diagram.png")]
    [InlineData("file:///C:/nowhere/at/all.png")]
    [InlineData("out/diagram.png")]
    [InlineData("./diagram.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("C:diagram.png")]
    [InlineData("file:///etc/passwd")]
    public void Another_machines_share_a_relative_path_and_every_other_kind_of_link_do_nothing(string? link) => Assert.Equal((OutsideLink.Kind.None, ""), Decide(link));

    [Theory]
    [InlineData(@"\\fileserver\share\a.cs")]
    [InlineData(@"\\fileserver\share\a.cs#L10")]
    [InlineData("//fileserver/share/a.cs#L10")]
    [InlineData("file://fileserver/share/diagram.png")]
    [InlineData("file:////fileserver/share/diagram.png")]
    [InlineData("file://localhost/C:/work/out/diagram.png")]
    [InlineData(@"\\?\UNC\fileserver\share\a.cs#L10")]
    public void A_share_on_another_machine_is_never_asked_about_not_even_whether_the_file_is_there(string link)
    {
        // Asking is the leak: Windows offers this account's credentials to the machine named.
        var asked = new List<string>();
        var decided = OutsideLink.Decide(link, p => { asked.Add(p); return true; }, p => { asked.Add(p); return true; });
        Assert.Equal((OutsideLink.Kind.None, ""), decided);
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData("out/diagram.png")]
    [InlineData("./diagram.png")]
    [InlineData("diagram.png")]
    [InlineData("C:diagram.png")]
    [InlineData("/etc/passwd")]
    [InlineData("file:///etc/passwd")]
    [InlineData("C:/work/out/nul")]
    [InlineData("file:///C:/work/out/nul")]
    [InlineData("C:/work/out/setup.exe:diagram.png")]
    [InlineData("file:///C:/work/out/setup.exe:diagram.png")]
    public void Nothing_is_opened_for_a_path_that_is_not_a_plain_file_on_a_drive_even_where_such_a_file_exists(string link) =>
        // Every file and folder "exists" here: what is refused is refused for what the link says, not for what is on the disk.
        Assert.Equal((OutsideLink.Kind.None, ""), OutsideLink.Decide(link, _ => true, _ => true));
}
