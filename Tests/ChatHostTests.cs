using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>
/// Runs the real host process in chat mode against a script that speaks the CLI's line protocol;
/// nothing here touches the deck's own hosts or needs Claude.
/// </summary>
public class ChatHostTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-chathost-" + Guid.NewGuid().ToString("N"));
    Process? _host;

    public ChatHostTests() => Directory.CreateDirectory(Path.Combine(_dir, "hosts"));

    public void Dispose()
    {
        try { if (_host is { HasExited: false }) _host.Kill(true); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    static string HostExe => Path.Combine(Path.GetDirectoryName(typeof(HostProgram).Assembly.Location)!, "SessionDeck.exe");

    // Each message gets a turn: the session line, the message echoed, a token delta, a reply, the result.
    // "ask" stops at a permission request, "mcp" at a request only the host can answer, "quit" ends the process.
    // It answers the host's initialize and get_settings, and takes an effort change.
    const string FakeCli = """
        [Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $out = [Console]::Out
        $out.WriteLine('a line that is not part of the protocol')
        $effort = 'high'
        while ($null -ne ($line = [Console]::In.ReadLine())) {
            $m = $line | ConvertFrom-Json
            if ($m.type -eq 'user') {
                $text = $m.message.content
                $content = $text
                if ($text -isnot [string]) { $text = 'an image' }
                $out.WriteLine('{"type":"system","subtype":"init","session_id":"live-session","model":"fake-model","permissionMode":"default"}')
                if ($text -like 'h*') { $out.WriteLine('{"type":"system","subtype":"session_title_changed","title":"named by the cli"}') }
                $out.WriteLine((@{ type = 'user'; isReplay = $true; message = @{ role = 'user'; content = $content } } | ConvertTo-Json -Compress -Depth 8))
                if ($text -eq 'quit') { [Console]::Error.WriteLine('fake cli: told to quit'); exit 3 }
                if ($text -eq 'ask') { $out.WriteLine('{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{},"tool_use_id":"t1"}}'); continue }
                if ($text -eq 'mcp') { $out.WriteLine('{"type":"control_request","request_id":"m1","request":{"subtype":"mcp_message"}}'); continue }
                $out.WriteLine('{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"echo"}}}')
                $out.WriteLine((@{ type = 'assistant'; message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = "echo: $text" }) } } | ConvertTo-Json -Compress -Depth 6))
                $out.WriteLine('{"type":"result","subtype":"success","is_error":false}')
            }
            elseif ($m.type -eq 'control_request') {
                $id = $m.request_id
                if ($m.request.subtype -eq 'initialize') { $out.WriteLine('{"type":"control_response","response":{"subtype":"success","request_id":"' + $id + '","response":{"current_permission_mode":"default","models":[{"value":"fake","resolvedModel":"fake-model","displayName":"Fake","description":"","supportedEffortLevels":["low","high"]}],"commands":[{"name":"compact","description":"Summarize","argumentHint":""}]}}}') }
                elseif ($m.request.subtype -eq 'get_settings') { $out.WriteLine('{"type":"control_response","response":{"subtype":"success","request_id":"' + $id + '","response":{"applied":{"model":"fake-model","effort":"' + $effort + '"}}}}') }
                elseif ($m.request.subtype -eq 'apply_flag_settings') { $effort = $m.request.settings.effortLevel; $out.WriteLine('{"type":"control_response","response":{"subtype":"success","request_id":"' + $id + '"}}') }
            }
            elseif ($m.type -eq 'control_response') {
                $out.WriteLine($line)
                $out.WriteLine((@{ type = 'assistant'; message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = "answered $($m.response.request_id): $($m.response.subtype)" }) } } | ConvertTo-Json -Compress -Depth 6))
                $out.WriteLine('{"type":"result","subtype":"success","is_error":false}')
            }
        }
        """;

    [Fact]
    public async Task A_chat_host_carries_a_conversation_between_a_viewer_and_the_cli()
    {
        string script = Path.Combine(_dir, "fake-cli.ps1");
        File.WriteAllText(script, FakeCli);
        string transcript = Path.Combine(_dir, "transcript.jsonl");
        File.WriteAllLines(transcript, new[]
        {
            """{"type":"user","message":{"role":"user","content":"said before the restart"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"answered before the restart"}]}}""",
        });
        var spec = new HostSpec
        {
            Id = "chattest0001", SessionId = "planned-session", Provider = "Claude", Kind = ChatStream.Kind, HistoryPath = transcript,
            CommandLine = $"\"{SessionLauncher.ResolvePowerShell()}\" -NoLogo -NoProfile -File \"{script}\"",
            Cwd = _dir, HostsDir = Path.Combine(_dir, "hosts"), RingBytes = 256 * 1024,
        };
        var psi = new ProcessStartInfo(HostExe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add("--host");
        _host = Process.Start(psi)!;
        _host.StandardInput.Write(JsonSerializer.Serialize(spec));
        _host.StandardInput.Close();
        Assert.Equal("ready", await _host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));

        var record = Record();
        Assert.Equal((ChatStream.Kind, "idle", transcript, "planned-session"), (record.Kind, record.AgentStatus, record.TranscriptPath, record.SessionId));
        Assert.True(record.IsChat);

        using var viewer = await Viewer.Attach(record, after: 0);
        Assert.Equal(ChatStream.Kind, viewer.Snapshot.GetProperty("kind").GetString());
        var first = await viewer.Until(l => Type(l) == "host");
        Assert.Equal(new[] { "user", "assistant", "host" }, first.Select(Type));
        Assert.True(first[0].GetProperty("history").GetBoolean());
        Assert.Equal("said before the restart", first[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("a line that is not part of the protocol", first[2].GetProperty("text").GetString());

        await viewer.Pump(() => viewer.Catalog.HasValue && viewer.Settings.Any(f => f.GetProperty("effort").GetString() == "high"));
        Assert.Equal("fake", viewer.Catalog!.Value.GetProperty("models")[0].GetProperty("value").GetString());
        Assert.Equal("compact", viewer.Catalog.Value.GetProperty("commands")[0].GetProperty("name").GetString());
        Assert.Equal(("default", "fake-model", "high"), SettingsOf(viewer));
        Assert.Empty(viewer.Live);

        await viewer.Send(ChatStream.UserLine("héllo wörld"));
        var turn = await viewer.Until(l => Type(l) == "result");
        Assert.Equal(new[] { "user", "assistant", "result" }, turn.Select(Type));
        Assert.Equal("héllo wörld", turn[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("echo: héllo wörld", turn[1].GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(new[] { "system", "system", "stream_event" }, viewer.Live.Select(Type));
        record = Record();
        Assert.Equal(("idle", "live-session", "fake-model", "named by the cli", "Stop"), (record.AgentStatus, record.SessionId, record.Model, record.Title, record.LastEvent));
        Assert.Equal(new[] { "named by the cli" }, viewer.Titles);

        await viewer.Send("this is not json\n" + ChatStream.UserLine("ask"));
        var asked = await viewer.Until(l => Type(l) == "control_request");
        Assert.Equal(new[] { "user", "control_request" }, asked.Select(Type));
        Assert.Equal("ask", asked[0].GetProperty("message").GetProperty("content").GetString());
        record = await RecordWhen(r => r.AgentStatus == "waiting");
        Assert.Equal(("Write", "PermissionRequest"), (record.LastTool, record.LastEvent));

        await viewer.Send("""{"type":"control_response","response":{"subtype":"success","request_id":"r1","response":{"behavior":"allow","updatedInput":{}}}}""");
        var allowed = await viewer.Until(l => Type(l) == "result");
        Assert.Equal(new[] { "control_response", "assistant", "result" }, allowed.Select(Type));
        Assert.Equal("answered r1: success", allowed[1].GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("idle", (await RecordWhen(r => r.AgentStatus == "idle")).AgentStatus);

        await viewer.Send(ChatStream.UserLine("mcp"));
        var declined = await viewer.Until(l => Type(l) == "result");
        Assert.Equal("answered m1: error", declined.Single(l => Type(l) == "assistant").GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());

        using (var late = await Viewer.Attach(record, after: 0))
        {
            var replay = await late.Until(l => Type(l) == "result" && late.Lines.Count(x => Type(x) == "result") == 3);
            Assert.Equal(viewer.Lines.Select(l => l.GetRawText()), replay.Select(l => l.GetRawText()));
            Assert.DoesNotContain(replay, l => Type(l) == "stream_event" || Type(l) == "system");
            // What the CLI offers and what is in force reach a viewer that was not there when the CLI said so.
            await late.Pump(() => late.Catalog.HasValue && late.Settings.Count > 0);
            Assert.Equal("fake", late.Catalog!.Value.GetProperty("models")[0].GetProperty("value").GetString());
            Assert.Equal(("default", "fake-model", "high"), SettingsOf(late));
            Assert.Empty(late.Live);
        }

        // A viewer changes the effort: the CLI's bare "done" is followed by the host asking what is in force and telling every viewer.
        await viewer.Send("""{"type":"control_request","request_id":"sd-set-1","request":{"subtype":"apply_flag_settings","settings":{"effortLevel":"low"}}}""");
        await viewer.Pump(() => viewer.Settings.Any(f => f.GetProperty("effort").GetString() == "low"));
        Assert.Equal(("default", "fake-model", "low"), SettingsOf(viewer));
        Assert.Equal("sd-set-1", viewer.Live[^1].GetProperty("response").GetProperty("request_id").GetString());

        // A pasted image goes to the CLI whole and into the scrollback without its bytes.
        string image = new string('Q', 20000);
        await viewer.Send("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"look"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"DATA"}}]},"parent_tool_use_id":null,"session_id":""}""".Replace("DATA", image));
        var pasted = await viewer.Until(l => Type(l) == "result");
        var source = pasted[0].GetProperty("message").GetProperty("content")[1].GetProperty("source");
        Assert.Equal(("", 20000), (source.GetProperty("data").GetString(), source.GetProperty("omitted").GetInt32()));
        Assert.Equal("echo: an image", pasted[1].GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());

        await viewer.Send(ChatStream.UserLine("quit"));
        var last = await viewer.Until(l => Type(l) == "host");
        Assert.Equal(("stderr", "fake cli: told to quit"), (last[^1].GetProperty("subtype").GetString(), last[^1].GetProperty("text").GetString()));
        Assert.Equal(3, await viewer.Exited());
        Assert.True(_host.WaitForExit(15000), "host did not exit after its child did");
        Assert.Equal(3u, Record().ExitCode);
    }

    HostRecord Record() => JsonSerializer.Deserialize<HostRecord>(File.ReadAllText(Path.Combine(_dir, "hosts", "chattest0001.json")))!;

    async Task<HostRecord> RecordWhen(Func<HostRecord, bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            HostRecord? r = null;
            try { r = Record(); } catch (IOException) { } catch (JsonException) { }
            if (r != null && ready(r)) return r;
            Assert.True(DateTime.UtcNow < deadline, $"the record never got there; last status {r?.AgentStatus}");
            await Task.Delay(50);
        }
    }

    static string Type(JsonElement line) => line.GetProperty("type").GetString() ?? "";

    static (string? Mode, string? Model, string? Effort) SettingsOf(Viewer v) =>
        (v.Settings[^1].GetProperty("mode").GetString(), v.Settings[^1].GetProperty("model").GetString(), v.Settings[^1].GetProperty("effort").GetString());

    /// <summary>A viewer as the deck page is one: scrollback bytes split into lines, live lines kept apart.</summary>
    sealed class Viewer : IDisposable
    {
        readonly ClientWebSocket _ws = new();
        readonly MemoryStream _partial = new();
        readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _taken;

        public JsonElement Snapshot { get; private set; }
        public List<JsonElement> Lines { get; } = new();
        public List<JsonElement> Live { get; } = new();
        public List<string> Titles { get; } = new();
        public JsonElement? Catalog { get; private set; }
        public List<JsonElement> Settings { get; } = new();

        /// <summary>Read frames until <paramref name="done"/> holds.</summary>
        public async Task Pump(Func<bool> done)
        {
            while (!done()) await Read();
        }

        public async Task<int> Exited()
        {
            while (!_exit.Task.IsCompleted) await Read();
            return await _exit.Task;
        }

        public static async Task<Viewer> Attach(HostRecord record, long after)
        {
            var v = new Viewer();
            await v._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{record.Port}/attach?token={record.Token}&after={after}"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await v.Read();
            Assert.Equal("snapshot", v.Snapshot.GetProperty("type").GetString());
            return v;
        }

        public Task Send(string lines) =>
            _ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "input", data = lines + "\n" })), WebSocketMessageType.Text, true, CancellationToken.None);

        /// <summary>The lines that arrived since the last call, read until one of them satisfies <paramref name="done"/>.</summary>
        public async Task<List<JsonElement>> Until(Func<JsonElement, bool> done)
        {
            while (!Lines.Skip(_taken).Any(done)) await Read();
            var fresh = Lines.Skip(_taken).ToList();
            _taken = Lines.Count;
            return fresh;
        }

        async Task Read()
        {
            var buf = new byte[65536];
            using var msg = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buf, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
                if (r.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("the host closed the socket");
                msg.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Text)
            {
                var frame = JsonDocument.Parse(msg.ToArray()).RootElement.Clone();
                switch (Type(frame))
                {
                    case "snapshot": Snapshot = frame; break;
                    case "live": Live.Add(frame.GetProperty("line").Clone()); break;
                    case "title": Titles.Add(frame.GetProperty("title").GetString() ?? ""); break;
                    case "catalog": Catalog = frame; break;
                    case "settings": Settings.Add(frame); break;
                    case "exit": _exit.TrySetResult(frame.GetProperty("code").GetInt32()); break;
                }
                return;
            }
            foreach (byte b in msg.ToArray())
            {
                if (b != (byte)'\n') { _partial.WriteByte(b); continue; }
                Lines.Add(JsonDocument.Parse(_partial.ToArray()).RootElement.Clone());
                _partial.SetLength(0);
            }
        }

        public void Dispose()
        {
            try { _ws.Abort(); } catch { }
            _ws.Dispose();
        }
    }
}
