using System.IO;

namespace SessionDeck;

/// <summary>
/// A Claude launcher pinned to another Claude.ai login: <c>~/.claude-accounts/&lt;name&gt;/claude-&lt;name&gt;.ps1</c>.
/// The script owns the pinning (config dir, credentials, proxy variables stripped); the deck only runs
/// it in place of <c>claude</c>, with the same flags and hook settings as any session.
/// </summary>
internal sealed record ClaudeProfile(string Name, string ScriptPath)
{
    /// <summary>The command that stands in for <c>claude</c> inside the host's pwsh; the script passes every argument through.</summary>
    public string Command => $"& '{ScriptPath.Replace("'", "''")}'";
}

internal static class ClaudeProfiles
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-accounts");

    public static IReadOnlyList<ClaudeProfile> Discover() => Discover(DefaultRoot);

    /// <summary>Every account folder with its launcher script, by name.</summary>
    public static IReadOnlyList<ClaudeProfile> Discover(string root)
    {
        var found = new List<ClaudeProfile>();
        try
        {
            if (!Directory.Exists(root)) return found;
            foreach (var dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                string script = Path.Combine(dir, $"claude-{name}.ps1");
                if (File.Exists(script)) found.Add(new ClaudeProfile(name, script));
            }
        }
        catch (Exception ex) { App.LogError(ex); }
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    /// <summary>The launcher command for a profile name, or plain <c>claude</c> when the name is empty or its script is gone.</summary>
    public static string CommandFor(string profile, string root)
    {
        if (profile.Length == 0) return "";
        var hit = Discover(root).FirstOrDefault(p => string.Equals(p.Name, profile, StringComparison.OrdinalIgnoreCase));
        if (hit == null) PerformanceLog.Write($"claude-profile missing name={profile}; launching plain claude");
        return hit?.Command ?? "";
    }

    public static string CommandFor(string profile) => CommandFor(profile, DefaultRoot);
}
