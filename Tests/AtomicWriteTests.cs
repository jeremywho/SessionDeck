using System.IO;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>A crash is a transition too: what Jeremy arranged must survive one mid-write, and a damaged file must never be saved over.</summary>
public class AtomicWriteTests
{
    static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sd-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static Settings Arranged(string group)
    {
        var s = new Settings { FlagsVersion = 2 };
        s.SessionGroups.Add(new SessionGroup { Name = group, Collapsed = true, Members = { "s1", "s2" } });
        s.Deck = new DeckLayout
        {
            Focused = 1,
            Columns =
            {
                new DeckColumn { Hosts = { "a", "b" }, Sessions = { "s1", "s2" }, Active = "b", Fraction = 0.6 },
                new DeckColumn { Hosts = { "c" }, Sessions = { "s3" }, Active = "c", Fraction = 0.4 },
            },
        };
        return s;
    }

    static void AssertArranged(Settings s, string group)
    {
        var g = Assert.Single(s.SessionGroups);
        Assert.Equal((group, true), (g.Name, g.Collapsed));
        Assert.Equal(new[] { "s1", "s2" }, g.Members);
        Assert.Equal(2, s.Deck.Columns.Count);
        Assert.Equal(new[] { "a", "b" }, s.Deck.Columns[0].Hosts);
        Assert.Equal(("b", 0.6, 1), (s.Deck.Columns[0].Active, s.Deck.Columns[0].Fraction, s.Deck.Focused));
    }

    [Fact]
    public void A_crash_between_the_temp_write_and_the_swap_leaves_the_old_file_whole()
    {
        string path = Path.Combine(NewDir(), "settings.json");
        Arranged("before").Save(path);
        string before = File.ReadAllText(path);

        AtomicFile.BeforeSwap = p => { if (string.Equals(p, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("power cut"); };
        try { Arranged("after").Save(path); }
        finally { AtomicFile.BeforeSwap = null; }

        Assert.Equal(before, File.ReadAllText(path));
        AssertArranged(Settings.Load(path), "before");
    }

    [Fact]
    public void A_truncated_settings_file_loads_the_last_good_copy_and_is_kept_aside()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "settings.json");
        Arranged("first").Save(path);
        Arranged("second").Save(path);
        byte[] whole = File.ReadAllBytes(path);
        byte[] truncated = whole[..(whole.Length / 2)];
        File.WriteAllBytes(path, truncated);

        var loaded = Settings.Load(path);
        AssertArranged(loaded, "first");

        var aside = Assert.Single(Directory.GetFiles(dir, "settings.json.corrupt-*"));
        loaded.Save(path);
        Assert.Equal(truncated, File.ReadAllBytes(aside));
        AssertArranged(Settings.Load(path), "first");
    }

    [Fact]
    public void An_empty_settings_file_is_not_saved_over_with_defaults()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "settings.json");
        Arranged("kept").Save(path);
        Arranged("kept").Save(path);
        File.WriteAllText(path, "");

        AssertArranged(Settings.Load(path), "kept");
        Assert.Single(Directory.GetFiles(dir, "settings.json.corrupt-*"));
    }

    /// <summary>When the damaged file cannot even be moved aside, it is the only copy of what it held: nothing may write over it.</summary>
    [Fact]
    public void A_damaged_file_that_cannot_be_moved_aside_is_never_written_over()
    {
        string path = Path.Combine(NewDir(), "settings.json");
        File.WriteAllText(path, "{\"SessionGroups\":[{\"Name\":\"bz\"");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Settings.Load(path);

        new Settings { FlagsVersion = 2 }.Save(path);
        Assert.Equal("{\"SessionGroups\":[{\"Name\":\"bz\"", File.ReadAllText(path));
    }

    [Fact]
    public void A_leftover_temp_file_from_a_crash_is_ignored()
    {
        string path = Path.Combine(NewDir(), "settings.json");
        Arranged("real").Save(path);
        File.WriteAllText(path + ".tmp", "{\"Session");
        AssertArranged(Settings.Load(path), "real");
        Arranged("next").Save(path);
        AssertArranged(Settings.Load(path), "next");
    }

    [Fact]
    public void The_auto_restart_guard_survives_a_relaunch()
    {
        string path = Path.Combine(NewDir(), "auto-restarts.json");
        new AutoRestartLedger(path).Record("s1", "2.1.283");

        var relaunched = new AutoRestartLedger(path);
        Assert.True(relaunched.Done("s1", "2.1.283"));
        Assert.False(relaunched.Done("s1", "2.1.284"));
        Assert.False(relaunched.Done("s2", "2.1.283"));
    }

    [Fact]
    public void The_auto_restart_guard_keeps_only_the_current_version()
    {
        string path = Path.Combine(NewDir(), "auto-restarts.json");
        var ledger = new AutoRestartLedger(path);
        ledger.Record("s1", "2.1.283");
        ledger.Record("s2", "2.1.284");

        var relaunched = new AutoRestartLedger(path);
        Assert.False(relaunched.Done("s1", "2.1.283"));
        Assert.True(relaunched.Done("s2", "2.1.284"));
        Assert.DoesNotContain("s1", File.ReadAllText(path));
    }
}
