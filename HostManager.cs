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
    public static string NewClaudeCommand(string sessionId, string? name, string extraFlags, string model = "", string effort = "")
    {
        string cmd = $"claude --session-id {sessionId}";
        if (!string.IsNullOrWhiteSpace(name)) cmd += $" --name \"{name.Replace("\"", "\\\"")}\"";
        if (!string.IsNullOrWhiteSpace(model)) cmd += $" --model \"{model.Trim()}\"";
        if (!string.IsNullOrWhiteSpace(effort)) cmd += $" --effort {effort.Trim()}";
        if (!string.IsNullOrWhiteSpace(extraFlags)) cmd += " " + extraFlags.Trim();
        return cmd + ClaudeHookArgs;
    }

    public static string ResumeClaudeCommand(string sessionId, string extraFlags)
    {
        string cmd = $"claude --resume {sessionId}";
        if (!string.IsNullOrWhiteSpace(extraFlags)) cmd += " " + extraFlags.Trim();
        return cmd + ClaudeHookArgs;
    }

    public static string NewCodexCommand(string extraFlags, string model = "", string effort = "")
    {
        string cmd = "codex";
        if (!string.IsNullOrWhiteSpace(model)) cmd += $" -m \"{model.Trim()}\"";
        if (!string.IsNullOrWhiteSpace(effort)) cmd += $" -c model_reasoning_effort=\"{effort.Trim()}\"";
        if (!string.IsNullOrWhiteSpace(extraFlags)) cmd += " " + extraFlags.Trim();
        return cmd + CodexHookArgs;
    }

    public static string ResumeCodexCommand(string threadId, string extraFlags)
    {
        string cmd = $"codex resume {threadId}";
        if (!string.IsNullOrWhiteSpace(extraFlags)) cmd += " " + extraFlags.Trim();
        return cmd + CodexHookArgs;
    }

    /// <summary>Filled in by the host once it knows its port and token — see <see cref="Host.Hooks"/>.</summary>
    const string ClaudeHookArgs = " --settings \"" + Host.Hooks.ClaudeSettingsPlaceholder + "\"";
    static readonly string CodexHookArgs = " " + Host.Hooks.CodexArgs(Host.Hooks.CommandPlaceholder);

    /// <summary>
    /// The host runs the CLI under pwsh so its own PATH resolution and profile apply, exactly as a
    /// Windows Terminal tab would. <c>-NoExit</c> keeps the pane readable after the CLI exits.
    /// </summary>
    static string WrapInShell(string cmd) =>
        $"\"{SessionLauncher.ResolvePowerShell()}\" -NoLogo -NoExit -Command \"{cmd.Replace("\"", "\\\"")}\"";

    public static HostRecord Spawn(string sessionId, SessionProvider provider, string command, string cwd, string? title, string initialPrompt = "")
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
            InitialPrompt = initialPrompt ?? "",
        };

        var psi = new ProcessStartInfo(HostExe())
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
    /// The binary a host runs from. Never the app's own exe: a host lives for hours and holds its
    /// image open, which would pin the build in a dev tree and block an installed update from
    /// swapping the exe. Each build is copied once into a stamped folder under the data dir, keyed
    /// by the app exe's write time, and hosts run from there. Stale stamped folders whose hosts
    /// have all exited are removed on the way.
    /// </summary>
    static string HostExe()
    {
        string src = Environment.ProcessPath!;
        string srcDir = Path.GetDirectoryName(src)!;
        string stamp = File.GetLastWriteTimeUtc(src).Ticks.ToString();
        string root = Path.Combine(Path.GetDirectoryName(HostsDir)!, "host-bin");
        string dst = Path.Combine(root, stamp);
        string exe = Path.Combine(dst, Path.GetFileName(src));
        if (!File.Exists(exe))
        {
            CopyTree(srcDir, dst);
            if (!File.Exists(exe)) throw new InvalidOperationException("host binary copy failed: " + exe);
        }
        PruneHostBins(root, dst);
        return exe;
    }

    static void CopyTree(string from, string to)
    {
        string tmp = to + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(tmp, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(tmp);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            string rel = Path.GetRelativePath(from, file);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(tmp, rel))!);
            File.Copy(file, Path.Combine(tmp, rel), overwrite: true);
        }
        Directory.Move(tmp, to);
    }

    /// <summary>
    /// Drop stamped copies nothing runs from any more. A copy a live host reports, or whose exe is
    /// locked by a process, stays whole: a recursive delete would strip the runtime config and
    /// dependencies around a locked exe, and the host's next hook forwarder would then die on the
    /// ".NET Desktop Runtime" dialog.
    /// </summary>
    static void PruneHostBins(string root, string keep)
    {
        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(keep) };
        foreach (var rec in Discover())
            if (rec.HostBin.Length > 0) inUse.Add(Path.GetFullPath(rec.HostBin));
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (inUse.Contains(Path.GetFullPath(dir))) continue;
            if (Directory.GetFiles(dir, "*.exe").Any(IsLocked)) continue;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Can the file be opened for exclusive access? A running exe cannot.</summary>
    internal static bool IsLocked(string file)
    {
        try
        {
            using var f = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
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

    /// <summary>
    /// Alive means the recorded pid is running and, when the start time can be read, it matches the
    /// record — pid reuse after a reboot must not resurrect a stale record. A start time that cannot
    /// be read (access denied, process exiting) is treated as a match: dropping a live host's record
    /// on a transient read is worse than keeping a dead one for one more scan.
    /// </summary>
    public static bool IsAlive(HostRecord rec)
    {
        Process p;
        try { p = Process.GetProcessById(rec.HostPid); }
        catch { return false; }
        using (p)
        {
            try { if (p.HasExited) return false; } catch { }
            try
            {
                long start = p.StartTime.ToUniversalTime().Ticks;
                return Math.Abs(start - rec.HostStartTicks) < TimeSpan.TicksPerSecond * 2;
            }
            catch { return true; }
        }
    }

    /// <summary>
    /// Tell a host to end its child. One short WebSocket round trip on a background thread; the
    /// host's own job object does the actual killing, so this works with no viewer attached.
    /// </summary>
    public static void Kill(HostRecord rec)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var ws = new System.Net.WebSockets.ClientWebSocket();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{rec.Port}/attach?token={rec.Token}&after={long.MaxValue}"), cts.Token);
                var msg = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"kill\"}");
                await ws.SendAsync(msg, System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);
                await Task.Delay(200, cts.Token);
            }
            catch (Exception ex) { App.LogError(ex); }
        });
    }

    /// <summary>The record as the host last wrote it, or null once it is gone.</summary>
    public static HostRecord? Reload(HostRecord rec) => ReadRecord(Path.Combine(HostsDir, rec.Id + ".json"));

    /// <summary>Drop an exited host's record so its row goes away.</summary>
    public static void Forget(HostRecord rec)
    {
        try { File.Delete(Path.Combine(HostsDir, rec.Id + ".json")); } catch { }
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
