using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace SessionDeck.Host;

/// <summary>What the app hands a new host on stdin.</summary>
internal sealed class HostSpec
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Provider { get; set; } = "Claude";
    public string CommandLine { get; set; } = "";
    public string Cwd { get; set; } = "";
    public short Cols { get; set; } = 120;
    public short Rows { get; set; } = 30;
    public string HostsDir { get; set; } = "";
    public int RingBytes { get; set; } = 4 * 1024 * 1024;
    /// <summary>Typed into the session once it has drawn something and then stayed quiet for a
    /// moment: the CLI's prompt is up by then. Text first, Enter as a separate write, because the
    /// Codex TUI does not submit both when they arrive in one chunk.</summary>
    public string InitialPrompt { get; set; } = "";

    /// <summary>The tab's name until the CLI sets a title of its own: the name the session had before a restart or resume.</summary>
    public string Title { get; set; } = "";

    /// <summary>The Claude account profile the session runs as (see <see cref="ClaudeProfiles"/>); empty for the default login.</summary>
    public string Profile { get; set; } = "";

    /// <summary>Empty for a terminal on a pseudoconsole; <see cref="ChatStream.Kind"/> for a CLI spoken to in stream-json over plain pipes.</summary>
    public string Kind { get; set; } = "";

    /// <summary>A chat session's transcript: where the conversation so far is read from when the session is resumed.</summary>
    public string HistoryPath { get; set; } = "";

    /// <summary>The permission mode and effort a restarted chat session had, put back once the CLI is up; empty to leave them as the CLI starts.</summary>
    public string RestoreMode { get; set; } = "";
    public string RestoreEffort { get; set; } = "";

    /// <summary>The command that starts the CLI on another conversation in the same folder, with <see cref="ChatSessions.SessionPlaceholder"/> where its id goes; empty for a session that cannot switch.</summary>
    public string ChatResumeCommand { get; set; } = "";
}

/// <summary>
/// The host's public record, <c>hosts/&lt;id&gt;.json</c>. Written at start, rewritten when the
/// title changes and when the child exits. The app reads it to reattach after its own restart.
/// </summary>
internal sealed class HostRecord
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Provider { get; set; } = "Claude";
    public string CommandLine { get; set; } = "";
    public string Cwd { get; set; } = "";
    public int HostPid { get; set; }
    public long HostStartTicks { get; set; }
    public int ChildPid { get; set; }
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public uint? ExitCode { get; set; }
    public DateTime? ExitedAt { get; set; }
    public string AgentStatus { get; set; } = "";
    public string LastEvent { get; set; } = "";
    public string LastTool { get; set; } = "";
    public DateTime? StatusAt { get; set; }
    public string TranscriptPath { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Conversations of the agents this pane's agy fanned out to, as their hooks named them: not this pane's, never to be restored as sessions of their own.</summary>
    public List<string> ChildConversations { get; set; } = new();

    /// <summary>Hooks seen by this process; kept out of the file so a hook that changes nothing else writes nothing.</summary>
    [JsonIgnore] public int HookEvents { get; set; }

    /// <summary>The stamped copy this host (and its hook forwarders) run from; the app must not prune it while the host lives.</summary>
    public string HostBin { get; set; } = "";

    /// <summary>The Claude account profile the session runs as; a restart or resume must launch it the same way.</summary>
    public string Profile { get; set; } = "";

    /// <summary>The current turn armed something that will wake the session by itself.</summary>
    public bool Pending { get; set; }

    /// <summary>Empty for a terminal; <see cref="ChatStream.Kind"/> for a chat session, which has no terminal to attach to and must be restarted as one.</summary>
    public string Kind { get; set; } = "";

    [JsonIgnore] public bool IsChat => Kind == ChatStream.Kind;

    /// <summary>What this chat host can do (<see cref="ChatStream.Revision"/>); 0 for a terminal, and for a chat host from before revisions were written.</summary>
    public int ChatRevision { get; set; }

    /// <summary>A chat session's permission mode and effort as last reported: what a restart puts back.</summary>
    public string PermissionMode { get; set; } = "";
    public string Effort { get; set; } = "";

    /// <summary>How this host derives status. 2: idle notifications no longer mean waiting; turns can end
    /// scheduled. 3: a background shell command no longer counts as scheduling a wake-up. 4: the pty size
    /// travels with the attach stream, which a phone needs.</summary>
    public int Protocol { get; set; }

    public const int CurrentProtocol = 4;

    [JsonIgnore] public bool HasExited => ExitCode.HasValue;

    /// <summary>
    /// The CLI did not end on its own: the console was closed under it (Windows shutdown, logoff,
    /// a killed window) or it was interrupted. Such a session is still one to bring back.
    /// </summary>
    [JsonIgnore] public bool WasKilled => ExitCode is 0x40010004 or 0xC000013A or 0xC000013B;
}

/// <summary>
/// <c>SessionDeck.exe --host</c>: one process owning one pseudoconsole. It outlives the app window
/// on purpose — the app is a viewer that attaches over a loopback WebSocket and can come and go.
/// </summary>
internal static class HostProgram
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly byte[] DsrQuery = "\x1b[6n"u8.ToArray();
    static readonly byte[] DsrReply = "\x1b[1;1R"u8.ToArray();

    sealed class Client
    {
        public readonly Channel<(bool Text, byte[] Data)> Out = Channel.CreateUnbounded<(bool, byte[])>(new UnboundedChannelOptions { SingleReader = true });
        /// <summary>A viewer that answers none of the program's terminal queries (a phone through the gateway); the host answers for it.</summary>
        public bool Passive;
    }

    /// <summary>The host answers cursor-position queries itself unless a viewer that can answer them is attached: exactly one responder, never two.</summary>
    internal static bool HostAnswersQueries(IEnumerable<bool> viewersPassive) => viewersPassive.All(p => p);

    static readonly List<Client> Clients = new();
    static readonly object ClientsLock = new();
    /// <summary>Orders a resize against an attach so no viewer misses one; never taken by the output reader, which only needs <see cref="ClientsLock"/>.</summary>
    static readonly object SizeLock = new();
    static Ring _ring = null!;
    static ConPty _pty = null!;
    static PipeChild? _pipe;
    /// <summary>Whether the transcript held more conversation than the scrollback was started with. (A scrollback that has since dropped its oldest lines has more behind it too.)</summary>
    static bool _chatEarlier;
    static readonly ChatStream _chat = new();
    static readonly object ChatInputLock = new();
    static readonly StringBuilder _chatErrors = new();
    /// <summary>The conversation the CLI was last started on, its transcript, and the command that started it. The record's session id can run ahead of this: the CLI names its session at each turn.</summary>
    static string _chatSession = "", _chatHistoryPath = "", _chatCommand = "";
    /// <summary>Goes up each time the CLI is started on another conversation; a viewer that sees it change drops what it shows.</summary>
    static int _chatEpoch;
    /// <summary>Where the scrollback was last emptied: what it holds begins at a whole line unless it has dropped bytes since.</summary>
    static long _ringFloor;
    static readonly object SwapLock = new();
    /// <summary>The conversation to start when the CLI ends, asked for by a viewer; null when its end is the session's end.</summary>
    static string? _swapTo;
    /// <summary>The conversation the CLI was just started on and has not yet introduced itself in (<see cref="ChatStream.Introduces"/>). It becomes the one served when it does; if the CLI ends before that, the one before it is started again and no viewer ever saw the change.</summary>
    static Conversation? _unproven;
    static volatile bool _swapping, _killRequested;
    static HostRecord _record = null!;
    static string _recordPath = "";
    static HostSpec _spec = new();
    static volatile bool _exited;
    static uint _exitCode;
    static readonly MemoryStream _oscBuf = new();
    static bool _inOsc;

    public static int Run()
    {
        HostSpec spec;
        try
        {
            string json = Console.In.ReadToEnd();
            spec = JsonSerializer.Deserialize<HostSpec>(json) ?? throw new InvalidDataException("empty spec");
        }
        catch (Exception ex)
        {
            Log($"bad spec: {ex}");
            return 2;
        }

        try { return Serve(spec); }
        catch (Exception ex)
        {
            Log($"host {spec.Id}: {ex}");
            return 1;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);
    const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;

    /// <summary>
    /// A ConPTY child inherits its parent's standard-handle VALUES when they are redirected, and
    /// those values mean nothing in the child, so it reads EOF and exits at once. The spec arrives on
    /// our stdin, so before the child is created our std slots are cleared; a process with no std
    /// handles is what the console subsystem expects and it then hands the child real console handles.
    /// </summary>
    static void DetachStdHandles()
    {
        SetStdHandle(STD_INPUT_HANDLE, IntPtr.Zero);
        SetStdHandle(STD_OUTPUT_HANDLE, IntPtr.Zero);
        SetStdHandle(STD_ERROR_HANDLE, IntPtr.Zero);
    }

    static int Serve(HostSpec spec)
    {
        _spec = spec;
        Directory.CreateDirectory(spec.HostsDir);
        _recordPath = Path.Combine(spec.HostsDir, spec.Id + ".json");
        _ring = new Ring(Math.Max(spec.RingBytes, 64 * 1024));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        string hookCmd = Hooks.HookCommand(Environment.ProcessPath!, port, token);
        string commandLine = spec.CommandLine.Replace(Hooks.CommandPlaceholder, hookCmd);
        if (commandLine.Contains(Hooks.ClaudeSettingsPlaceholder))
        {
            string settingsDir = Path.Combine(Path.GetDirectoryName(spec.HostsDir)!, "hook-settings");
            Directory.CreateDirectory(settingsDir);
            string settingsPath = Path.Combine(settingsDir, spec.Id + ".claude.json");
            File.WriteAllText(settingsPath, Hooks.ClaudeSettingsJson(hookCmd));
            commandLine = commandLine.Replace(Hooks.ClaudeSettingsPlaceholder, settingsPath.Replace('\\', '/'));
        }
        if (commandLine.Contains(Hooks.AgyHooksDirPlaceholder))
        {
            string hooksDir = AgyHooksDir(spec);
            Directory.CreateDirectory(Path.Combine(hooksDir, ".agents"));
            File.WriteAllText(Path.Combine(hooksDir, ".agents", "hooks.json"), Hooks.AgyHooksJson(port, token));
            commandLine = commandLine.Replace(Hooks.AgyHooksDirPlaceholder, hooksDir.Replace('\\', '/'));
        }

        var ready = Console.Out;
        bool chat = spec.Kind == ChatStream.Kind;
        if (chat) _pipe = new PipeChild(commandLine, spec.Cwd);
        else
        {
            DetachStdHandles();
            _pty = new ConPty(commandLine, spec.Cwd, spec.Cols, spec.Rows);
        }
        var self = Process.GetCurrentProcess();
        _record = new HostRecord
        {
            Id = spec.Id,
            SessionId = spec.SessionId,
            Provider = spec.Provider,
            CommandLine = spec.CommandLine,
            Cwd = spec.Cwd,
            HostPid = self.Id,
            HostStartTicks = self.StartTime.ToUniversalTime().Ticks,
            HostBin = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "",
            Profile = spec.Profile,
            Protocol = HostRecord.CurrentProtocol,
            Kind = spec.Kind,
            ChatRevision = chat ? ChatStream.Revision : 0,
            ChildPid = chat ? _pipe!.Pid : _pty.Pid,
            TranscriptPath = chat ? spec.HistoryPath : "",
            Port = port,
            Token = token,
            StartedAt = DateTime.UtcNow,
            Title = spec.Title,
        };
        // A resumed agy conversation fires no SessionStart; it sits at its prompt until spoken to.
        if (spec.Provider == "Agy" && spec.CommandLine.Contains(Hooks.AgyHooksDirPlaceholder))
        {
            _record.AgentStatus = "idle";
            _record.StatusAt = _record.StartedAt;
        }
        if (chat)
        {
            // The CLI reports nothing until it is spoken to: a chat session starts out waiting for the person.
            _record.AgentStatus = "idle";
            _record.StatusAt = _record.StartedAt;
            (_chatSession, _chatHistoryPath, _chatCommand) = (spec.SessionId, spec.HistoryPath, commandLine);
            var seed = ChatTranscript.Before(spec.HistoryPath, long.MaxValue, Math.Max(spec.RingBytes, 64 * 1024) / 2, int.MaxValue);
            _chatEarlier = seed.Next >= 0;
            foreach (var line in seed.Lines) AppendChatLine(line);
        }
        WriteRecord();
        ready.WriteLine("ready");
        ready.Flush();

        Thread reader, waiter;
        if (chat)
        {
            // One thread sees the CLI through its life, and through each conversation it is started again on.
            reader = waiter = new Thread(() => ChatSupervise(spec)) { IsBackground = true, Name = "chat-child" };
            reader.Start();
        }
        else
        {
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" };
            reader.Start();
            if (spec.InitialPrompt.Length > 0)
                new Thread(() => TypeWhenQuiet(spec.InitialPrompt)) { IsBackground = true, Name = "initial-prompt" }.Start();
            waiter = new Thread(() =>
            {
                _exitCode = _pty.WaitForExit();
                _exited = true;
                _pty.ClosePseudoConsole();
            }) { IsBackground = true, Name = "pty-wait" };
            waiter.Start();
        }

        var accept = Task.Run(() => AcceptLoop(listener));
        reader.Join();
        waiter.Join();

        if (chat)
        {
            string errors;
            lock (_chatErrors) errors = _chatErrors.ToString().Trim();
            if (errors.Length > 0) AppendChatLine(ChatStream.HostLine("stderr", errors));
        }
        _record.ExitCode = _exitCode;
        _record.ExitedAt = DateTime.UtcNow;
        WriteRecord();
        try { File.Delete(Path.Combine(Path.GetDirectoryName(spec.HostsDir)!, "hook-settings", spec.Id + ".claude.json")); } catch { }
        try { Directory.Delete(AgyHooksDir(spec), true); } catch { }
        Broadcast(true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "exit", code = _exitCode })));
        lock (ClientsLock) foreach (var c in Clients) c.Out.Writer.TryComplete();
        listener.Stop();
        Thread.Sleep(300);
        if (chat) _pipe!.Dispose(); else _pty.Dispose();
        return 0;
    }

    /// <summary>The folder handed to agy as <c>--add-dir</c>, holding only this host's hooks.json.</summary>
    static string AgyHooksDir(HostSpec spec) => Path.Combine(Path.GetDirectoryName(spec.HostsDir)!, "hook-settings", spec.Id + ".agy");

    static long _lastOutputTicks;
    static long _outputBytes;

    /// <summary>
    /// Ready means: the CLI has drawn something, has been quiet for two seconds, is not showing a
    /// folder-trust question, and (Claude) has reported its first hook event — that event only fires
    /// once the conversation exists, i.e. after any trust dialog. Codex has no start-of-session hook
    /// in its TUI, so the trust check is what guards it. Waits up to five minutes, then gives up.
    /// </summary>
    static void TypeWhenQuiet(string prompt)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        bool claude = string.Equals(_record.Provider, "Claude", StringComparison.OrdinalIgnoreCase);
        while (DateTime.UtcNow < deadline && !_exited)
        {
            Thread.Sleep(250);
            long bytes = Interlocked.Read(ref _outputBytes);
            long last = Interlocked.Read(ref _lastOutputTicks);
            if (bytes < 200 || last == 0) continue;
            if (DateTime.UtcNow.Ticks - last < TimeSpan.TicksPerSecond * 2) continue;
            if (claude && _record.HookEvents == 0) continue;
            var (_, tail) = _ring.Since(Math.Max(0, _ring.End - 6000));
            string recent = Encoding.UTF8.GetString(tail);
            if (recent.Contains("trust this folder", StringComparison.OrdinalIgnoreCase)
                || recent.Contains("trust", StringComparison.OrdinalIgnoreCase) && recent.Contains("Enter to confirm", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var text = Encoding.UTF8.GetBytes(prompt.Replace("\r\n", "\n").Replace('\n', ' '));
                lock (_pty) { _pty.Input.Write(text); _pty.Input.Flush(); }
                Thread.Sleep(400);
                lock (_pty) { _pty.Input.Write("\r"u8); _pty.Input.Flush(); }
            }
            catch (Exception ex) { Log($"initial prompt: {ex.Message}"); }
            return;
        }
    }

    static void ReadLoop()
    {
        var buf = new byte[65536];
        while (true)
        {
            int n;
            try { n = _pty.Output.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            var chunk = buf.AsSpan(0, n);
            Interlocked.Add(ref _outputBytes, n);
            Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
            AppendAndSend(chunk.ToArray());
            ScanTitle(chunk);
            bool hostAnswers;
            lock (ClientsLock) hostAnswers = HostAnswersQueries(Clients.Select(c => c.Passive));
            if (hostAnswers && chunk.IndexOf(DsrQuery) >= 0)
            {
                try { _pty.Input.Write(DsrReply); _pty.Input.Flush(); } catch { }
            }
        }
    }

    /// <summary>
    /// Into the scrollback and out to every viewer as one step, under the lock an attach takes to read
    /// the scrollback and join the viewers: a viewer gets each byte exactly once, from one or the other.
    /// </summary>
    static void AppendAndSend(byte[] data)
    {
        lock (ClientsLock)
        {
            _ring.Append(data);
            foreach (var c in Clients) c.Out.Writer.TryWrite((false, data));
        }
    }

    static void AppendChatLine(byte[] line)
    {
        var framed = new byte[line.Length + 1];
        line.CopyTo(framed, 0);
        framed[^1] = (byte)'\n';
        AppendAndSend(framed);
    }

    /// <summary>The conversation a chat host's CLI runs: its id, the command that starts the CLI on it, its transcript, and what to call it.</summary>
    sealed record Conversation(string Session, string Command, string HistoryPath, string Title);

    /// <summary>
    /// Sees the CLI through its life. A chat can be moved to another conversation; on pipes the CLI
    /// cannot do that itself, so it is ended and started again on the one picked. The host, its
    /// viewers and its tab stay: only the child and the scrollback change, and those only once the
    /// CLI has introduced itself on the new conversation (<see cref="Serve(Conversation)"/>). If it
    /// ends without doing so, the conversation before it is started again and a line says why, so
    /// a bad pick never leaves a dead session.
    /// </summary>
    static void ChatSupervise(HostSpec spec)
    {
        string mode = spec.RestoreMode, effort = spec.RestoreEffort, prompt = spec.InitialPrompt;
        while (true)
        {
            var pipe = _pipe!;
            var reader = new Thread(() => ChatReadLoop(pipe)) { IsBackground = true, Name = "chat-read" };
            var errors = new Thread(() => DrainChatErrors(pipe)) { IsBackground = true, Name = "chat-stderr" };
            reader.Start();
            errors.Start();
            // What the CLI offers and what is in force, asked once so every viewer can be told without asking itself.
            WriteChatInput(ChatStream.InitializeLine());
            foreach (var line in ChatStream.RestoreLines(mode, effort)) WriteChatInput(line);
            WriteChatInput(ChatStream.SettingsLine());
            if (prompt.Length > 0) WriteChatInput(ChatStream.UserLine(prompt));
            prompt = "";
            // A kill that arrived while the CLI was between conversations found nothing to end.
            if (_killRequested) pipe.KillTree();

            uint code = pipe.WaitForExit();
            // Something the CLI started can hold the output pipe open past the CLI's own exit; nothing more will be said on it.
            if (!reader.Join(TimeSpan.FromSeconds(3))) pipe.KillTree();
            reader.Join();
            errors.Join(TimeSpan.FromSeconds(3));

            string? picked;
            Conversation? silent;
            lock (SwapLock)
            {
                picked = _swapTo;
                _swapTo = null;
                silent = _unproven;
                _unproven = null;
            }
            // The mode and effort are what the session was in a moment ago; the CLI that reported them is gone.
            lock (_record) (mode, effort) = (_record.PermissionMode, _record.Effort);
            bool started = false;
            if (_killRequested) { }
            else if (picked != null)
                started = Begin(For(picked), true, "", pipe) || Begin(Current(), false, "Could not resume that conversation.", pipe);
            else if (silent != null)
            {
                string said;
                lock (_chatErrors) said = _chatErrors.ToString().Trim();
                started = Begin(Current(), false, "Could not resume that conversation" + (said.Length > 0 ? ": " + said : "."), pipe);
            }
            if (started) continue;
            lock (SwapLock)
            {
                _swapping = false;
                _exitCode = code;
                _exited = true;
            }
            return;
        }
    }

    /// <summary>The conversation served now, as something to start the CLI on again. Once it has a transcript the CLI refuses to start a session under its id and resumes it instead; before that it is the other way round.</summary>
    static Conversation Current()
    {
        string session, title, model;
        lock (_record) { session = _record.SessionId; title = _record.Title; model = _record.Model; }
        string path = ChatTranscriptPath();
        bool resumable = _spec.ChatResumeCommand.Length > 0 && ChatSessions.IsSessionId(session) && File.Exists(path);
        return new Conversation(session, resumable ? ChatSessions.Command(_spec.ChatResumeCommand, session, model) : _chatCommand, path, title);
    }

    static Conversation For(string session)
    {
        string model;
        lock (_record) model = _record.Model;
        string path = ChatSessions.TranscriptOf(Path.GetDirectoryName(_chatHistoryPath) ?? "", session);
        return new Conversation(session, ChatSessions.Command(_spec.ChatResumeCommand, session, model), path, path.Length > 0 ? ChatSessions.Title(path) ?? "" : "");
    }

    /// <summary>
    /// Starts the CLI on a conversation. One that is <paramref name="unproven"/> (another than the
    /// one served) changes nothing a viewer sees until the CLI introduces itself. Otherwise it is the
    /// conversation already served, started again after a pick that did not come up: the scrollback
    /// stands, and <paramref name="note"/> is added to it and sent to the viewers as the answer to
    /// their request. False when the CLI could not be started at all.
    /// </summary>
    static bool Begin(Conversation c, bool unproven, string note, PipeChild old)
    {
        if (c.Command.Length == 0) return false;
        PipeChild fresh;
        try { fresh = new PipeChild(c.Command, _spec.Cwd); }
        catch (Exception ex) { Log($"start {c.Session}: {ex.Message}"); return false; }
        lock (ChatInputLock) _pipe = fresh;
        old.Dispose();
        lock (_chatErrors) _chatErrors.Clear();
        lock (_record)
        {
            _chat.NewConversation();
            _record.ChildPid = fresh.Pid;
            _record.AgentStatus = "idle";
            _record.StatusAt = DateTime.UtcNow;
            _record.LastTool = "";
            _record.LastEvent = "";
        }
        _chatCommand = unproven ? _chatCommand : c.Command;
        lock (SwapLock)
        {
            _unproven = unproven ? c : null;
            if (!unproven) _swapping = false;
        }
        WriteRecord();
        Log($"started on {c.Session} child={fresh.Pid} unproven={unproven}");
        if (note.Length > 0)
        {
            AppendChatLine(ChatStream.HostLine("stderr", note));
            Broadcast(true, ChatSessions.RefusedFrame("", note));
        }
        return true;
    }

    /// <summary>
    /// The CLI has introduced itself on the conversation it was started on, so that is the one this host serves
    /// from here: the record names it, the scrollback is emptied and filled from its transcript, and
    /// every viewer is told to start over (a snapshot with a new epoch).
    /// </summary>
    static void Serve(Conversation c)
    {
        var seed = ChatTranscript.Before(c.HistoryPath, long.MaxValue, Math.Max(_spec.RingBytes, 64 * 1024) / 2, int.MaxValue);
        lock (_record)
        {
            _record.SessionId = c.Session;
            _record.TranscriptPath = c.HistoryPath;
            _record.CommandLine = c.Command;
            _record.Title = c.Title;
        }
        (_chatSession, _chatHistoryPath, _chatCommand) = (c.Session, c.HistoryPath, c.Command);
        lock (ClientsLock)
        {
            _chatEarlier = seed.Next >= 0;
            _ringFloor = _ring.Clear();
            _chatEpoch++;
            byte[] snapshot = Encoding.UTF8.GetBytes(SnapshotJson(_ringFloor, _ringFloor, c.Title, 0, 0, ChatStream.Kind, ChatStream.Revision, c.Session, _chatEarlier, _chatEpoch));
            foreach (var client in Clients) client.Out.Writer.TryWrite((true, snapshot));
            foreach (var line in seed.Lines) AppendChatLine(line);
        }
        lock (SwapLock) _swapping = false;
        WriteRecord();
        Log($"serving {c.Session} epoch={_chatEpoch}");
    }

    static void ChatReadLoop(PipeChild pipe)
    {
        var buf = new byte[65536];
        var partial = new MemoryStream();
        while (true)
        {
            int n;
            try { n = pipe.Output.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            int from = 0;
            for (int i = 0; i < n; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                partial.Write(buf, from, i - from);
                from = i + 1;
                ChatLine(partial.ToArray());
                partial.SetLength(0);
            }
            partial.Write(buf, from, n - from);
        }
        if (partial.Length > 0) ChatLine(partial.ToArray());
    }

    static void ChatLine(byte[] raw)
    {
        int length = raw.Length;
        while (length > 0 && (raw[length - 1] == (byte)'\r' || raw[length - 1] == (byte)' ')) length--;
        if (length == 0) return;
        byte[] line = length == raw.Length ? raw : raw[..length];
        bool durable, ownAnswer;
        ChatStream.Change change;
        string? renamed, decline, catalog = null, settings = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Conversation? proven = null;
            bool unproven;
            lock (SwapLock)
            {
                unproven = _unproven != null;
                if (unproven && ChatStream.Introduces(root)) (proven, _unproven) = (_unproven, null);
            }
            if (proven != null) Serve(proven);
            // What a CLI says before it has introduced itself is not part of the conversation still served: one that
            // cannot come up says only why, and that is told once it has ended.
            else if (unproven) return;
            lock (_record)
            {
                string title = _record.Title;
                durable = _chat.Durable(root);
                change = _chat.Apply(root, _record, DateTime.UtcNow);
                renamed = _record.Title != title ? _record.Title : null;
                if (change.Catalog) catalog = _chat.CatalogFrame();
                if (change.Settings) settings = _chat.SettingsFrame();
            }
            decline = ChatStream.Decline(root);
            ownAnswer = ChatStream.AnswersTheHost(root);
        }
        catch (JsonException)
        {
            bool unproven;
            lock (SwapLock) unproven = _unproven != null;
            if (!unproven) AppendChatLine(ChatStream.HostLine("output", Encoding.UTF8.GetString(line)));
            return;
        }
        if (change.Record) WriteRecord();
        if (renamed != null) Broadcast(true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "title", title = renamed })));
        if (catalog != null) Broadcast(true, Encoding.UTF8.GetBytes(catalog));
        if (settings != null) Broadcast(true, Encoding.UTF8.GetBytes(settings));
        if (durable) AppendChatLine(ChatStream.ForRing(line));
        else if (!ownAnswer) Broadcast(true, ChatStream.LiveFrame(line));
        if (decline != null) WriteChatInput(decline);
        if (change.Refresh) WriteChatInput(ChatStream.SettingsLine());
    }

    static void WriteChatInput(string line)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (ChatInputLock) { _pipe!.Input.Write(bytes); _pipe.Input.Flush(); }
        }
        catch (Exception ex) { Log($"chat input: {ex.Message}"); }
    }

    /// <summary>What the CLI says outside the protocol: kept to its last few kilobytes, shown when it ends.</summary>
    static void DrainChatErrors(PipeChild pipe)
    {
        var buf = new byte[4096];
        while (true)
        {
            int n;
            try { n = pipe.Error.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            lock (_chatErrors)
            {
                _chatErrors.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (_chatErrors.Length > 8192) _chatErrors.Remove(0, _chatErrors.Length - 8192);
            }
        }
    }

    /// <summary>OSC 0 / OSC 2 window-title sequences, the way a terminal would read them.</summary>
    static void ScanTitle(ReadOnlySpan<byte> chunk)
    {
        for (int i = 0; i < chunk.Length; i++)
        {
            byte b = chunk[i];
            if (!_inOsc)
            {
                if (b == 0x1b && i + 1 < chunk.Length && chunk[i + 1] == (byte)']')
                {
                    _inOsc = true;
                    _oscBuf.SetLength(0);
                    i++;
                }
                continue;
            }
            if (b == 0x07 || b == 0x1b)
            {
                _inOsc = false;
                string s = Encoding.UTF8.GetString(_oscBuf.GetBuffer(), 0, (int)_oscBuf.Length);
                if (s.StartsWith("0;") || s.StartsWith("2;"))
                {
                    string title = s[2..].Trim();
                    // A pane given a name keeps it over the shell's own path; a CLI that titles itself still wins.
                    bool shellPath = TitleMarks.IsExePath(title) && _spec.Title.Length > 0;
                    if (title.Length > 0 && title != _record.Title && !shellPath)
                    {
                        // A spinner frame changes only the leading mark; the record on disk keeps the
                        // name it has, since every write makes the app rescan.
                        bool renamed = !TitleMarks.SameName(title, _record.Title);
                        _record.Title = title;
                        if (renamed) WriteRecord();
                        Broadcast(true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "title", title })));
                    }
                }
                continue;
            }
            if (_oscBuf.Length < 512) _oscBuf.WriteByte(b);
        }
    }

    static void Broadcast(bool text, byte[] data)
    {
        Client[] snapshot;
        lock (ClientsLock) snapshot = Clients.ToArray();
        foreach (var c in snapshot) c.Out.Writer.TryWrite((text, data));
    }

    static async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => ServeClient(tcp));
        }
    }

    static async Task ServeClient(TcpClient tcp)
    {
        tcp.NoDelay = true;
        using var _ = tcp;
        var stream = tcp.GetStream();
        var (request, leftover) = await Gateway.MiniHttp.ReadHead(stream);
        if (request == null) return;
        var lines = request.Split("\r\n");
        var reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return;
        string path = reqLine[1];
        if (reqLine[0] == "POST")
        {
            int peerPort = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Port ?? 0;
            await ServeHook(stream, lines, path, leftover, peerPort);
            return;
        }
        if (reqLine[0] != "GET") return;
        string? key = null;
        foreach (var l in lines.Skip(1))
            if (l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = l[18..].Trim();
        if (key == null) return;

        var query = Gateway.MiniHttp.ParseQuery(path);
        if (!query.TryGetValue("token", out var token) || token != _record.Token)
        {
            await Gateway.MiniHttp.WriteAscii(stream, "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
            return;
        }
        long after = query.TryGetValue("after", out var a) && long.TryParse(a, out var parsed) ? parsed : 0;

        string accept = Gateway.MiniHttp.AcceptKey(key);
        await Gateway.MiniHttp.WriteAscii(stream, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");

        using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
        var client = new Client { Passive = query.TryGetValue("passive", out var passive) && passive == "1" };

        // Size and registration under one lock: a resize between them would be announced to every
        // viewer but this one, which would then hold the old size until the next resize.
        lock (SizeLock)
        {
            lock (ClientsLock)
            {
                (long start, byte[] data) = _ring.Since(after);
                long end = start + data.Length;
                string snapshot = _pipe != null
                    ? SnapshotJson(start, end, _record.Title, 0, 0, ChatStream.Kind, ChatStream.Revision, _record.SessionId, _chatEarlier || _ring.Start > _ringFloor, _chatEpoch, start > _ringFloor)
                    : SnapshotJson(start, end, _record.Title, _pty.Cols, _pty.Rows);
                client.Out.Writer.TryWrite((true, Encoding.UTF8.GetBytes(snapshot)));
                if (data.Length > 0) client.Out.Writer.TryWrite((false, data));
                if (_pipe != null)
                {
                    string catalog, settings;
                    lock (_record) { catalog = _chat.CatalogFrame(); settings = _chat.SettingsFrame(); }
                    if (catalog.Length > 0) client.Out.Writer.TryWrite((true, Encoding.UTF8.GetBytes(catalog)));
                    client.Out.Writer.TryWrite((true, Encoding.UTF8.GetBytes(settings)));
                }
                Clients.Add(client);
            }
        }
        if (_exited)
        {
            client.Out.Writer.TryWrite((true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "exit", code = _exitCode }))));
            client.Out.Writer.TryComplete();
        }

        var send = Task.Run(async () =>
        {
            try
            {
                await foreach (var (text, bytes) in client.Out.Reader.ReadAllAsync())
                    await ws.SendAsync(bytes, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "exit", CancellationToken.None);
            }
            catch { }
        });

        try
        {
            var inbuf = new byte[16384];
            var msg = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(inbuf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                msg.Write(inbuf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                HandleMessage(msg.ToArray(), client);
                msg.SetLength(0);
            }
        }
        catch { }
        finally
        {
            lock (ClientsLock) Clients.Remove(client);
            client.Out.Writer.TryComplete();
            try { await send; } catch { }
        }
    }

    static void HandleMessage(byte[] payload, Client client)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "history":
                    if (_pipe == null) return;
                    SendHistory(client, root);
                    break;
                case "sessions":
                    if (_pipe == null) return;
                    SendSessions(client, root);
                    break;
                case "resume":
                    if (_pipe == null) return;
                    RequestResume(client, root);
                    break;
                case "input":
                    if (_exited) return;
                    if (_pipe != null)
                    {
                        foreach (var line in ChatStream.InputLines(root.GetProperty("data").GetString() ?? "")) WriteChatInput(line);
                        break;
                    }
                    var bytes = Encoding.UTF8.GetBytes(root.GetProperty("data").GetString() ?? "");
                    lock (_pty) { _pty.Input.Write(bytes); _pty.Input.Flush(); }
                    break;
                case "resize":
                    if (_exited || _pipe != null) return;
                    lock (SizeLock)
                    {
                        _pty.Resize((short)root.GetProperty("cols").GetInt32(), (short)root.GetProperty("rows").GetInt32());
                        Broadcast(true, Encoding.UTF8.GetBytes(SizeJson(_pty.Cols, _pty.Rows)));
                    }
                    break;
                case "kill":
                    // Said first: the CLI ending is otherwise read as making way for another conversation, when one was asked for.
                    _killRequested = true;
                    if (_pipe != null) _pipe.KillTree(); else _pty.KillTree();
                    break;
            }
        }
        catch (Exception ex) { Log($"message: {ex.Message}"); }
    }

    /// <summary><c>POST /hook?token=…</c> from <c>SessionDeck.exe --hook</c>: one CLI hook event as
    /// JSON. Updates the record (status, last tool, identity) and tells attached viewers.</summary>
    static async Task ServeHook(NetworkStream stream, string[] lines, string path, byte[] leftover, int peerPort = 0)
    {
        var query = Gateway.MiniHttp.ParseQuery(path);
        if (!path.StartsWith("/hook") || !query.TryGetValue("token", out var token) || token != _record.Token)
        {
            await Gateway.MiniHttp.WriteAscii(stream, "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
            return;
        }
        int length = 0;
        foreach (var l in lines.Skip(1))
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l[15..].Trim(), out length);
        length = Math.Min(length, 1 << 20);
        var body = new byte[length];
        int have = Math.Min(leftover.Length, length);
        Array.Copy(leftover, body, have);
        while (have < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(have));
            if (n <= 0) break;
            have += n;
        }
        try
        {
            using var doc = JsonDocument.Parse(body.AsMemory(0, have));
            // Who sent it is decided while curl still holds its socket, before anything else is read.
            bool childAgy = doc.RootElement.TryGetProperty("conversationId", out _) && FromChildAgy(peerPort);
            ApplyHook(doc.RootElement, EventFromPath(path), childAgy);
        }
        catch (Exception ex) { Log($"hook: {ex.Message}"); }
        await Gateway.MiniHttp.WriteAscii(stream, "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
    }

    /// <summary>The first text frame of an attach: where the bytes that follow start in the ring, and the pty's size so a viewer that may not resize can render it.</summary>
    /// <summary>For a chat host, <paramref name="chat"/> says what it can do, <paramref name="session"/> names the conversation, which a viewer keys an unsent draft by, and <paramref name="earlier"/> says the transcript goes back further than the scrollback did at its start.</summary>
    /// <summary><paramref name="epoch"/> changes when a chat host moves to another conversation: what a viewer shows is then of a conversation no longer here. <paramref name="cut"/> says the bytes that follow may begin mid-line.</summary>
    internal static string SnapshotJson(long start, long end, string title, int cols, int rows, string kind = "", int chat = 0, string session = "", bool earlier = false, int epoch = 0, bool cut = false) =>
        JsonSerializer.Serialize(new { type = "snapshot", start, end, title, cols, rows, kind, chat, session, earlier, epoch, cut });

    /// <summary>The transcript of the conversation this host runs now: beside the one it started with, under the id the CLI last reported.</summary>
    static string ChatTranscriptPath()
    {
        string dir = Path.GetDirectoryName(_chatHistoryPath) ?? "";
        string session;
        lock (_record) session = _record.SessionId;
        return dir.Length == 0 || session.Length == 0 || session == _chatSession ? _chatHistoryPath : Path.Combine(dir, session + ".jsonl");
    }

    /// <summary>
    /// <c>{"type":"history","request":"…","before":N,"uuid":"…"}</c>: a page of the conversation
    /// older than the viewer's oldest message, answered to the asking viewer alone. A message read
    /// from the transcript is named by its place and its uuid, one that arrived live by its uuid
    /// alone. The file the session writes now is looked in first, then the one it started with
    /// (see <see cref="ChatTranscript.BeforeMessage"/>).
    /// </summary>
    static void SendHistory(Client client, JsonElement ask)
    {
        string request = Text(ask, "request");
        long before = ask.TryGetProperty("before", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var at) ? at : -1;
        string uuid = Text(ask, "uuid");
        string[] paths = { ChatTranscriptPath(), _chatHistoryPath };
        _ = Task.Run(() =>
        {
            try
            {
                var page = ChatTranscript.BeforeMessage(paths, before, uuid, 256 * 1024, 400);
                client.Out.Writer.TryWrite((true, ChatStream.HistoryFrame(page, request)));
            }
            catch (Exception ex)
            {
                // The viewer is waiting on an answer: it gets one that holds nothing rather than none.
                Log($"history: {ex.Message}");
                client.Out.Writer.TryWrite((true, ChatStream.HistoryFrame(new ChatTranscript.Page(new List<byte[]>(), -1), request)));
            }
        });
    }

    /// <summary><c>{"type":"sessions","request":"…"}</c>: the conversations in this session's folder, answered to the asking viewer alone.</summary>
    static void SendSessions(Client client, JsonElement ask)
    {
        string request = Text(ask, "request");
        string dir = Path.GetDirectoryName(_chatHistoryPath) ?? "";
        string current;
        int child;
        lock (_record) { current = _record.SessionId; child = _record.ChildPid; }
        _ = Task.Run(() =>
        {
            var entries = new List<ChatSessions.Entry>();
            try { entries = ChatSessions.List(dir, current, ChatSessions.LiveElsewhere(SessionScanner.SessionsDirectory, child)); }
            catch (Exception ex) { Log($"sessions: {ex.Message}"); }
            client.Out.Writer.TryWrite((true, ChatSessions.ListFrame(request, current, entries)));
        });
    }

    /// <summary>
    /// <c>{"type":"resume","request":"…","session":"…"}</c>: move this chat to another conversation
    /// in its folder. Refused, with the reason, to the asking viewer alone; carried out by ending
    /// the CLI, which <see cref="ChatSupervise"/> then starts again on the conversation picked.
    /// </summary>
    static void RequestResume(Client client, JsonElement ask)
    {
        string request = Text(ask, "request"), session = Text(ask, "session");
        string path = ChatSessions.TranscriptOf(Path.GetDirectoryName(_chatHistoryPath) ?? "", session);
        string refusal = "";
        lock (_record)
        {
            if (_spec.ChatResumeCommand.Length == 0) refusal = "This session cannot switch conversations. Restart it and try again.";
            else if (path.Length == 0) refusal = "That conversation is not in this folder.";
            else if (string.Equals(session, _record.SessionId, StringComparison.OrdinalIgnoreCase)) refusal = "That conversation is the one open here.";
            else if (_record.AgentStatus is "busy" or "waiting") refusal = "Stop the current turn first.";
        }
        if (refusal.Length == 0)
            lock (SwapLock)
            {
                if (_exited || _killRequested || _swapping) refusal = "The session is already changing.";
                else { _swapTo = session; _swapping = true; }
            }
        if (refusal.Length > 0)
        {
            client.Out.Writer.TryWrite((true, ChatSessions.RefusedFrame(request, refusal)));
            return;
        }
        Log($"resume asked: {session}");
        _pipe!.KillTree();
    }

    internal static string SizeJson(int cols, int rows) =>
        JsonSerializer.Serialize(new { type = "size", cols, rows });

    /// <summary><c>/hook/Stop?token=…</c> names the event in the path; <c>/hook?token=…</c> names none.</summary>
    internal static string EventFromPath(string path)
    {
        int q = path.IndexOf('?');
        string p = q < 0 ? path : path[..q];
        return p.StartsWith("/hook/") ? p["/hook/".Length..].Trim('/') : "";
    }

    /// <summary>Claude and Codex name the event in the body; agy's forwarder names it in the path instead.</summary>
    internal static string EventName(JsonElement root, string fromQuery) =>
        root.TryGetProperty("hook_event_name", out var e) && e.ValueKind == JsonValueKind.String && (e.GetString() ?? "").Length > 0 ? e.GetString()! : fromQuery;

    static Dictionary<int, (int Parent, string Name)>? _processTable;
    static DateTime _processTableAt;
    static int _senderLogs;

    /// <summary>True when the hook came from an agy fanned out by the pane's agy rather than from the pane's agy itself.</summary>
    static bool FromChildAgy(int peerPort)
    {
        if (peerPort <= 0) return false;
        try
        {
            int sender = TcpPeer.OwningPid(peerPort);
            if (sender <= 0) { if (_senderLogs++ < 3) Log($"hook from port {peerPort}: no owning process found"); return false; }
            // The sender is a curl that lives for milliseconds: a table that does not know it is stale, however recent.
            if (_processTable == null || !_processTable.ContainsKey(sender) || DateTime.UtcNow - _processTableAt > TimeSpan.FromSeconds(2))
            {
                _processTable = Native.BuildProcessTable();
                _processTableAt = DateTime.UtcNow;
            }
            int childPid;
            lock (_record) childPid = _record.ChildPid;
            int hops = Hooks.AgyHops(_processTable, sender, childPid);
            if (hops < 0 && DateTime.UtcNow - _processTableAt > TimeSpan.FromMilliseconds(50))
            {
                // An ancestor the table does not know yet (a shell agy just opened): look once more at the present.
                _processTable = Native.BuildProcessTable();
                _processTableAt = DateTime.UtcNow;
                hops = Hooks.AgyHops(_processTable, sender, childPid);
            }
            if (_senderLogs < 3 || hops != 1)
            {
                _senderLogs++;
                var chain = new List<string>();
                for (int p = sender, i = 0; p > 0 && i < 12 && _processTable.TryGetValue(p, out var e); i++, p = e.Parent) chain.Add($"{e.Name}({p})");
                Log($"hook from pid {sender}: {hops} agy between it and the pane {childPid}; chain {string.Join(" < ", chain)}");
            }
            return Hooks.FromChildAgy(hops);
        }
        catch (Exception ex) { Log($"hook sender: {ex.Message}"); return false; }
    }

    static void ApplyHook(JsonElement root, string eventFromQuery = "", bool childAgy = false)
    {
        string ev = EventName(root, eventFromQuery);
        if (ev.Length == 0) return;
        if (Hooks.FromSubagent(root) || childAgy)
        {
            bool learned = false;
            lock (_record)
            {
                _record.HookEvents++;
                string child = childAgy ? Text(root, "conversationId") : "";
                if (child.Length > 0 && _record.ChildConversations.Count < 64 && !_record.ChildConversations.Contains(child, StringComparer.OrdinalIgnoreCase))
                {
                    _record.ChildConversations.Add(child);
                    learned = true;
                }
            }
            if (learned) WriteRecord();
            return;
        }
        lock (_record)
        {
            _record.HookEvents++;
            // PostToolUse follows a PreToolUse that already said busy and names the tool; recording it
            // would rewrite the file on every tool call for nothing the app reads.
            if (ev != "PostToolUse") _record.LastEvent = ev;
            bool turnStarts = ev is "UserPromptSubmit" or "PreToolUse" or "PreInvocation" && _record.AgentStatus is "idle" or "scheduled" or "waiting" or "";
            if (ev == "SessionStart" || turnStarts) _record.Pending = false;
            if (ev == "PreToolUse" && Hooks.Defers(root)) _record.Pending = true;
            if (ev == "PreToolUse" && Hooks.StopsLoop(root)) _record.Pending = false;
            string? status = Hooks.StatusFor(ev, root, _record.Pending);
            if (ev == "SessionStart" && _record.AgentStatus == "busy") status = null;
            // StatusAt is when the status last changed, not when a hook last spoke: a turn's end keeps
            // its time through the idle notification a minute later, and an unchanged status is no write.
            if (status != null && status != _record.AgentStatus) { _record.AgentStatus = status; _record.StatusAt = DateTime.UtcNow; }
            _record.StatusAt ??= DateTime.UtcNow;
            if (root.TryGetProperty("tool_name", out var t) && t.ValueKind == JsonValueKind.String && ev == "PreToolUse")
                _record.LastTool = t.GetString() ?? "";
            // Always the latest: /resume or /clear inside the session switches it to another conversation,
            // and every hook after that carries the new id and transcript. A restart must resume the
            // conversation that is live, not the one the process was started with.
            if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String && (sid.GetString() ?? "").Length > 0)
                _record.SessionId = sid.GetString() ?? "";
            if (root.TryGetProperty("transcript_path", out var tp) && tp.ValueKind == JsonValueKind.String && (tp.GetString() ?? "").Length > 0)
                _record.TranscriptPath = tp.GetString() ?? "";
            if (Text(root, "conversationId") is { Length: > 0 } conversation) _record.SessionId = conversation;
            if (Text(root, "transcriptPath") is { Length: > 0 } transcript) _record.TranscriptPath = transcript;
            if (Text(root, "modelName") is { Length: > 0 } model) _record.Model = model;
        }
        WriteRecord();
        Broadcast(true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "hook", @event = ev, status = _record.AgentStatus, tool = _record.LastTool })));
    }

    static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";


    static string? _lastWritten;

    /// <summary>The record goes to disk only when its content changed: the app rescans on every write.</summary>
    static void WriteRecord()
    {
        try
        {
            lock (_record)
            {
                string json = JsonSerializer.Serialize(_record, Json);
                if (!RecordGate.Changed(json, ref _lastWritten)) return;
                string tmp = _recordPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _recordPath, overwrite: true);
            }
        }
        catch (Exception ex) { Log($"record: {ex.Message}"); }
    }

    static void Log(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "sessiondeck-host.log"),
                $"{DateTime.Now:O} [{Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch { }
    }
}
