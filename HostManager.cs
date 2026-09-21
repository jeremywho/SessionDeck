using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SessionDeck.Host;

namespace SessionDeck;

/// <summary>
/// Spawns and rediscovers pty-host processes. Each host is <c>SessionDeck.exe --host</c> with a
/// <see cref="HostSpec"/> on stdin; it writes <c>hosts/&lt;id&gt;.json</c> and stays up until its
/// child exits, whether or not this app is running.
/// </summary>
internal static class HostManager
{
    public static string HostsDir =>
        Environment.GetEnvironmentVariable("SD_DATA_DIR") is { Length: > 0 } d
            ? Path.Combine(d, "hosts")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionDeck", "hosts");

    /// <summary>The claude launch for a brand-new session with a preassigned id, so the transcript
    /// is known before the first byte is written.</summary>
    public static string NewClaudeCommand(string sessionId, string? name, string extraFlags)
    {
        string cmd = $"claude --session-id {sessionId}";
        if (!string.IsNullOrWhiteSpace(name)) cmd += $" --name \"{name.Replace("\"", "\\\"")}\"";
        if (!string.IsNullOrWhiteSpace(extraFlags)) cmd += " " + extraFlags.Trim();
        return cmd;
    }

    public static string ResumeClaudeCommand(string sessionId, string extraFlags)
    {
        string cmd = $"claude --resume {sessionId}";
        if (!string.IsNullOrWhiteSpace(extraFlags)) cmd += " " + extraFlags.Trim();
        return cmd;
    }

    public static string NewCodexCommand(string extraFlags)
    {
        string cmd = "codex";
        if (!string.IsNullOrWhiteSpace(extraFlags)) cmd += " " + extraFlags.Trim();
        return cmd;
    }

    /// <summary>
    /// The host runs the CLI under pwsh so its own PATH resolution and profile apply, exactly as a
    /// Windows Terminal tab would. <c>-NoExit</c> keeps the pane readable after the CLI exits.
    /// </summary>
    static string WrapInShell(string cmd) =>
        $"\"{SessionLauncher.ResolvePowerShell()}\" -NoLogo -NoExit -Command \"{cmd.Replace("\"", "\\\"")}\"";

    public static HostRecord Spawn(string sessionId, SessionProvider provider, string command, string cwd, string? title)
    {
        Directory.CreateDirectory(HostsDir);
        string id = Guid.NewGuid().ToString("N")[..12];
        var spec = new HostSpec
        {
            Id = id,
            SessionId = sessionId,
            Provider = provider.ToString(),
            CommandLine = WrapInShell(command),
            Cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : cwd,
            HostsDir = HostsDir,
        };

        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = spec.Cwd,
        };
        psi.ArgumentList.Add("--host");
        SessionLauncher.StripFromChild(psi, SessionLauncher.InjectedColorKillSwitchesForChild());
        SessionLauncher.StripFromChild(psi, InheritedSessionMarkers(psi.Environment.Keys));
        var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start host");
        p.StandardInput.Write(JsonSerializer.Serialize(spec));
        p.StandardInput.Close();

        string? ready = p.StandardOutput.ReadLine();
        if (ready != "ready")
            throw new InvalidOperationException($"host did not start (exit {(p.HasExited ? p.ExitCode : -1)})");

        var rec = ReadRecord(Path.Combine(HostsDir, id + ".json")) ?? throw new InvalidOperationException("host wrote no record");
        if (!string.IsNullOrWhiteSpace(title)) rec.Title = title;
        return rec;
    }

    /// <summary>
    /// Variables a running Claude or Codex session stamps on its children. If this app was itself
    /// started from inside one (an agent launching it to test, say), every session it hosts would
    /// inherit them and the CLI would believe it was nested. <c>ANTHROPIC_*</c> is deliberately not
    /// on this list: the CPA proxy is configured through it and must reach every session.
    /// </summary>
    public static IEnumerable<string> InheritedSessionMarkers(IEnumerable<string> names) =>
        names.Where(n => n.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
                      || n.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                      || n.Equals("CLAUDE_PID", StringComparison.OrdinalIgnoreCase)
                      || n.Equals("CLAUDE_EFFORT", StringComparison.OrdinalIgnoreCase)
                      || n.Equals("CLAUDE_PLUGIN_DATA", StringComparison.OrdinalIgnoreCase)
                      || n.Equals("CODEX_THREAD_ID", StringComparison.OrdinalIgnoreCase))
             .ToList();

    /// <summary>Every host record on disk whose process is still alive. Dead hosts' records are
    /// removed as they are found, so the folder is self-cleaning.</summary>
    public static List<HostRecord> Discover()
    {
        var list = new List<HostRecord>();
        if (!Directory.Exists(HostsDir)) return list;
        foreach (var f in Directory.GetFiles(HostsDir, "*.json"))
        {
            var rec = ReadRecord(f);
            if (rec == null) continue;
            if (IsAlive(rec)) list.Add(rec);
            else { try { File.Delete(f); } catch { } }
        }
        return list;
    }

    public static bool IsAlive(HostRecord rec)
    {
        try
        {
            using var p = Process.GetProcessById(rec.HostPid);
            if (p.HasExited) return false;
            long start = p.StartTime.ToUniversalTime().Ticks;
            return Math.Abs(start - rec.HostStartTicks) < TimeSpan.TicksPerSecond;
        }
        catch { return false; }
    }

    public static HostRecord? ReadRecord(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try { return JsonSerializer.Deserialize<HostRecord>(File.ReadAllText(path)); }
            catch (IOException) { Thread.Sleep(20); }
            catch { return null; }
        }
        return null;
    }
}
