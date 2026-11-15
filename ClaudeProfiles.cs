using System.IO;
using System.Text.RegularExpressions;

namespace SessionDeck;

/// <summary>
/// A wrapper script on the path that points the Claude CLI somewhere else: another endpoint and model, and a
/// config folder of its own (<c>claude-swift</c>, a local model). The script owns the pointing. The deck runs it
/// in place of <c>claude</c>, and reads from it only where that CLI keeps its files, since transcripts and
/// session records are looked for there.
/// </summary>
internal sealed record ClaudeWrapper(string Profile, string ScriptPath, string ConfigDir)
{
    /// <summary>What every launch through a wrapper carries, whatever the deck's own Claude flags are: those may name a model, and the wrapper's model is the point of it.</summary>
    public const string Flags = "--dangerously-skip-permissions";

    /// <summary>The command that stands in for <c>claude</c> inside the host's pwsh.</summary>
    public string ShellCommand => $"& '{ScriptPath.Replace("'", "''")}'";
    public string ProjectsDir => Path.Combine(ConfigDir, "projects");
    public string SessionsDir => Path.Combine(ConfigDir, "sessions");
}

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

    // ---- wrappers ----

    /// <summary>The profile name of a session started through <c>claude-swift</c>.</summary>
    public const string Swift = "swift";

    public static bool IsWrapper(string profile) => string.Equals(profile, Swift, StringComparison.OrdinalIgnoreCase);

    static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The Claude CLI's own folder when nothing points it elsewhere.</summary>
    public static string DefaultConfigDir => Path.Combine(UserHome, ".claude");

    /// <summary>Where the wrapper's script is: on the path, or where the Claude installer puts its own launcher.</summary>
    internal static Func<string?> SwiftScriptLocator { get; set; } = () =>
        HostManager.FindOnPath("claude-swift.cmd", Environment.GetEnvironmentVariable("PATH") ?? "", File.Exists, Path.Combine(UserHome, ".local", "bin"));

    static readonly object WrapperGate = new();
    static (DateTime At, ClaudeWrapper? Found) _swift;
    static readonly TimeSpan WrapperKeep = TimeSpan.FromSeconds(15);

    internal static void ForgetWrappers() { lock (WrapperGate) _swift = default; }

    /// <summary>
    /// The wrapper behind a profile, or null: not a wrapper profile, its script is not there, or the script names
    /// a config folder in a way this cannot read. Asked on every scan, so what it finds is kept a few seconds.
    /// </summary>
    public static ClaudeWrapper? Wrapper(string profile)
    {
        if (!IsWrapper(profile)) return null;
        lock (WrapperGate)
        {
            if (DateTime.UtcNow - _swift.At < WrapperKeep) return _swift.Found;
            ClaudeWrapper? found = null;
            try
            {
                string? script = SwiftScriptLocator();
                if (script != null) found = Read(Swift, script, File.ReadAllText(script));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _swift = (DateTime.UtcNow, found);
            return found;
        }
    }

    /// <summary>
    /// As <see cref="Wrapper"/>, for starting a session: a wrapper that cannot be found is an error. It is never
    /// replaced by plain <c>claude</c>, which would put the conversation on another account and model.
    /// </summary>
    public static ClaudeWrapper Require(string profile) =>
        Wrapper(profile) ?? throw new InvalidOperationException($"claude-{profile.ToLowerInvariant()} was not found on the path, or its script does not say where it keeps its files");

    /// <summary>The config folders of the wrappers there are, beside the CLI's own: sessions run through one are recorded there.</summary>
    public static IReadOnlyList<string> WrapperConfigDirs() =>
        Wrapper(Swift) is { } w && !SameDir(w.ConfigDir, DefaultConfigDir) ? new[] { w.ConfigDir } : Array.Empty<string>();

    /// <summary>
    /// The profile of a session found in a config folder: a wrapper's when the folder is its own, else none. A session
    /// started outside the deck through a wrapper is brought in on that wrapper, never on plain <c>claude</c>.
    /// </summary>
    public static string ProfileOfConfigDir(string configDir) =>
        configDir.Length > 0 && Wrapper(Swift) is { } w && SameDir(w.ConfigDir, configDir) ? w.Profile : "";

    /// <summary>The CLI's folder for a profile's sessions: the wrapper's, else the CLI's own.</summary>
    public static string ConfigDirFor(string profile) => Wrapper(profile)?.ConfigDir ?? DefaultConfigDir;

    static bool SameDir(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static readonly Regex ConfigDirLine = new(@"^[ \t@]*set[ \t]+""?CLAUDE_CONFIG_DIR=([^""\r\n]*)""?[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// A wrapper from its script. The config folder is the one the script sets (<c>set "CLAUDE_CONFIG_DIR=…"</c>,
    /// with <c>%NAME%</c> variables filled in), or the CLI's own when the script sets none. A script that names one
    /// in a way this does not follow gives null rather than a guess: a wrong folder means a session that looks
    /// new when it is not.
    /// </summary>
    internal static ClaudeWrapper? Read(string profile, string scriptPath, string script)
    {
        if (script.IndexOf("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase) < 0) return new ClaudeWrapper(profile, scriptPath, DefaultConfigDir);
        var set = ConfigDirLine.Matches(script);
        if (set.Count != 1) return null;
        string dir = Environment.ExpandEnvironmentVariables(set[0].Groups[1].Value.Trim());
        if (dir.Length == 0 || dir.Contains('%') || dir.Contains('!') || !Path.IsPathFullyQualified(dir)) return null;
        return new ClaudeWrapper(profile, scriptPath, dir);
    }
}
