using System.IO;
using System.Text.Json;
using Xunit;

namespace SessionDeck.Tests;

public class ClaudeProfilesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sd-profiles-" + Guid.NewGuid().ToString("N"));

    public ClaudeProfilesTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    string Slot(string name, bool withScript = true)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (withScript) File.WriteAllText(Path.Combine(dir, $"claude-{name}.ps1"), "param([string[]]$ClaudeArgs) & claude @ClaudeArgs");
        return dir;
    }

    [Fact]
    public void Every_account_folder_with_a_launcher_is_a_profile_sorted_by_name()
    {
        Slot("shiranami");
        Slot("work");
        Slot("broken", withScript: false);
        var found = ClaudeProfiles.Discover(_root);
        Assert.Equal(new[] { "shiranami", "work" }, found.Select(p => p.Name));
        Assert.Equal(Path.Combine(_root, "shiranami", "claude-shiranami.ps1"), found[0].ScriptPath);
    }

    [Fact]
    public void A_missing_root_yields_no_profiles()
    {
        Assert.Empty(ClaudeProfiles.Discover(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void The_launcher_stands_in_for_claude_and_the_flags_and_hooks_follow()
    {
        string dir = Slot("shiranami");
        string launcher = ClaudeProfiles.CommandFor("shiranami", _root);
        Assert.Equal($"& '{Path.Combine(dir, "claude-shiranami.ps1")}'", launcher);

        var fresh = HostManager.NewClaudeCommand("sid-1", null, "--dangerously-skip-permissions", launcher: launcher);
        Assert.StartsWith(launcher + " --session-id sid-1 --dangerously-skip-permissions --settings \"", fresh);
        var resumed = HostManager.ResumeClaudeCommand("sid-1", "--dangerously-skip-permissions", launcher);
        Assert.StartsWith(launcher + " --resume sid-1 --dangerously-skip-permissions --settings \"", resumed);
    }

    [Fact]
    public void An_unknown_or_empty_profile_launches_plain_claude()
    {
        Assert.Equal("", ClaudeProfiles.CommandFor("", _root));
        Assert.Equal("", ClaudeProfiles.CommandFor("gone", _root));
        Assert.StartsWith("claude --resume sid-1 ", HostManager.ResumeClaudeCommand("sid-1", "", ClaudeProfiles.CommandFor("gone", _root)));
    }

    [Fact]
    public void A_quote_in_the_script_path_is_doubled_for_pwsh()
    {
        Assert.Equal("& 'C:\\it''s\\claude-x.ps1'", new ClaudeProfile("x", "C:\\it's\\claude-x.ps1").Command);
    }

    [Fact]
    public void The_profile_survives_the_crash_resume_registry()
    {
        var saved = new SavedSession { Id = "sid-1", Provider = SessionProvider.Claude, Profile = "shiranami" };
        var back = JsonSerializer.Deserialize<SavedSession>(JsonSerializer.Serialize(saved))!;
        Assert.Equal("shiranami", back.Profile);
        var old = JsonSerializer.Deserialize<SavedSession>("{\"Id\":\"sid-2\"}")!;
        Assert.Equal("", old.Profile);
    }
}
