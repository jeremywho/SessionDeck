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
        $session = if ($args.Count -gt 0) { $args[0] } else { 'live-session' }
        # As the real CLI fails a resume: one error result on stdout, the reason on stderr, exit 1, and no answer to anything.
        if ($session -like 'dead*') { $out.WriteLine('{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["No conversation found"]}'); [Console]::Error.WriteLine("No conversation found with session ID: $session"); exit 1 }
        $out.WriteLine('a line that is not part of the protocol')
        $effort = 'high'
        $mode = 'default'
        while ($null -ne ($line = [Console]::In.ReadLine())) {
            $m = $line | ConvertFrom-Json
            if ($m.type -eq 'user') {
                $text = $m.message.content
                $content = $text
                if ($text -isnot [string]) { $text = 'an image' }
                $out.WriteLine('{"type":"system","subtype":"init","session_id":"' + $session + '","model":"fake-model","permissionMode":"' + $mode + '"}')
                if ($text -like 'h*') { $out.WriteLine('{"type":"system","subtype":"session_title_changed","title":"named by the cli"}') }
                $out.WriteLine((@{ type = 'user'; isReplay = $true; message = @{ role = 'user'; content = $content } } | ConvertTo-Json -Compress -Depth 8))
                if ($text -eq 'quit') { [Console]::Error.WriteLine('fake cli: told to quit'); exit 3 }
                if ($text -eq 'ask') { $out.WriteLine('{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{},"tool_use_id":"t1"}}'); continue }
                if ($text -eq 'mcp') { $out.WriteLine('{"type":"control_request","request_id":"m1","request":{"subtype":"mcp_message"}}'); continue }
                $out.WriteLine('{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"echo"}}}')
                $said = if ($text -eq 'who') { "i am $session" } else { "echo: $text" }
                $out.WriteLine((@{ type = 'assistant'; message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = $said }) } } | ConvertTo-Json -Compress -Depth 6))
                $out.WriteLine('{"type":"result","subtype":"success","is_error":false}')
            }
            elseif ($m.type -eq 'control_request') {
                $id = $m.request_id
                if ($m.request.subtype -eq 'initialize') { $out.WriteLine('{"type":"control_response","response":{"subtype":"success","request_id":"' + $id + '","response":{"current_permission_mode":"default","models":[{"value":"fake","resolvedModel":"fake-model","displayName":"Fake","description":"","supportedEffortLevels":["low","high"]}],"commands":[{"name":"compact","description":"Summarize","argumentHint":""}]}}}') }
                elseif ($m.request.subtype -eq 'get_settings') { $out.WriteLine('{"type":"control_response","response":{"subtype":"success","request_id":"' + $id + '","response":{"applied":{"model":"fake-model","effort":"' + $effort + '"}}}}') }
                elseif ($m.request.subtype -eq 'set_permission_mode') { $mode = $m.request.mode; $out.WriteLine('{"type":"control_response","response":{"subtype":"success","request_id":"' + $id + '","response":{"mode":"' + $mode + '"}}}') }
                elseif ($m.request.subtype -eq 'apply_flag_settings') { $effort = $m.request.settings.effortLevel; $out.WriteLine('{"type":"control_response","response":{"subtype":"success","request_id":"' + $id + '"}}') }
                elseif ($m.request.subtype -eq 'rename_session') {
                    # As the real CLI: the new name first, then the answer; a name it will not take is an error and no name.
                    if ($m.request.title -eq 'taken') { $out.WriteLine('{"type":"control_response","response":{"subtype":"error","request_id":"' + $id + '","error":"that name is taken"}}') }
                    else {
                        $out.WriteLine((@{ type = 'system'; subtype = 'session_title_changed'; title = "$($m.request.title) [$($m.request.source) $($m.request.session_id)]" } | ConvertTo-Json -Compress))
                        $out.WriteLine('{"type":"control_response","response":{"subtype":"success","request_id":"' + $id + '"}}')
                    }
                }
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
            // More than the scrollback is started with, and more than one page of earlier conversation holds.
            """{"type":"user","uuid":"u-old","message":{"role":"user","content":"OLD"}}""".Replace("OLD", new string('o', 300 * 1024)),
            """{"type":"user","uuid":"u-0","message":{"role":"user","content":"said long before"}}""",
            """{"type":"user","uuid":"u-1","message":{"role":"user","content":"said before the restart"}}""",
            """{"type":"assistant","uuid":"u-2","message":{"role":"assistant","content":[{"type":"text","text":"answered before the restart"}]}}""",
        });
        var spec = new HostSpec
        {
            Id = "chattest0001", SessionId = "planned-session", Provider = "Claude", Kind = ChatStream.Kind, HistoryPath = transcript,
            CommandLine = $"\"{SessionLauncher.ResolvePowerShell()}\" -NoLogo -NoProfile -File \"{script}\"",
            Cwd = _dir, HostsDir = Path.Combine(_dir, "hosts"), RingBytes = 256 * 1024,
            // As a restart hands a session on: it was planning, at medium effort.
            RestoreMode = "plan", RestoreEffort = "medium",
        };
        await Start(spec);

        var record = Record();
        Assert.Equal((ChatStream.Kind, "idle", transcript, "planned-session"), (record.Kind, record.AgentStatus, record.TranscriptPath, record.SessionId));
        Assert.True(record.IsChat);

        using var viewer = await Viewer.Attach(record, after: 0);
        Assert.Equal(ChatStream.Kind, viewer.Snapshot.GetProperty("kind").GetString());
        Assert.Equal((ChatStream.Revision, "planned-session"), (viewer.Snapshot.GetProperty("chat").GetInt32(), viewer.Snapshot.GetProperty("session").GetString()));
        Assert.Equal(ChatStream.Revision, record.ChatRevision);
        Assert.True(viewer.Snapshot.GetProperty("earlier").GetBoolean());
        var first = await viewer.Until(l => Type(l) == "host");
        Assert.Equal(new[] { "user", "user", "assistant", "host" }, first.Select(Type));
        Assert.True(first[1].GetProperty("history").GetBoolean());
        Assert.Equal("said before the restart", first[1].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("a line that is not part of the protocol", first[3].GetProperty("text").GetString());

        // The session comes back planning at medium effort: the host put both back before any viewer asked.
        await viewer.Pump(() => viewer.Catalog.HasValue && viewer.Settings.Any(f => f.GetProperty("effort").GetString() == "medium" && f.GetProperty("mode").GetString() == "plan"));
        Assert.Equal("fake", viewer.Catalog!.Value.GetProperty("models")[0].GetProperty("value").GetString());
        Assert.Equal("compact", viewer.Catalog.Value.GetProperty("commands")[0].GetProperty("name").GetString());
        Assert.Equal(("plan", "fake-model", "medium"), SettingsOf(viewer));
        Assert.DoesNotContain(viewer.Live, l => Type(l) != "control_response");
        viewer.Live.Clear();
        record = await RecordWhen(r => r.Effort == "medium");
        Assert.Equal(("plan", "medium"), (record.PermissionMode, record.Effort));

        // Earlier conversation is paged from the transcript: before a place in the file, or before a message known only by its uuid.
        long second = first[1].GetProperty("at").GetInt64();
        await viewer.SendFrame("{\"type\":\"history\",\"request\":\"q1\",\"before\":" + second + ",\"uuid\":\"u-1\"}");
        var page = await viewer.NextHistory();
        Assert.Equal("q1", page.GetProperty("request").GetString());
        Assert.Equal(new[] { "said long before" }, page.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("message").GetProperty("content").GetString()));
        // The page stops short of the long message before it, and says where to go on from.
        Assert.Equal(first[0].GetProperty("at").GetInt64(), page.GetProperty("next").GetInt64());
        await viewer.SendFrame("""{"type":"history","request":"q2","uuid":"u-2"}""");
        page = await viewer.NextHistory();
        Assert.Equal(new[] { "u-0", "u-1" }, page.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("uuid").GetString()));
        await viewer.SendFrame("""{"type":"history","request":"q3","uuid":"no-such-message"}""");
        page = await viewer.NextHistory();
        Assert.Equal(("q3", 0), (page.GetProperty("request").GetString(), page.GetProperty("lines").GetArrayLength()));

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
            Assert.Equal(("plan", "fake-model", "medium"), SettingsOf(late));
            Assert.Empty(late.Live);
        }

        // A viewer changes the effort: the CLI's bare "done" is followed by the host asking what is in force and telling every viewer.
        await viewer.Send("""{"type":"control_request","request_id":"sd-set-1","request":{"subtype":"apply_flag_settings","settings":{"effortLevel":"low"}}}""");
        await viewer.Pump(() => viewer.Settings.Any(f => f.GetProperty("effort").GetString() == "low"));
        Assert.Equal(("plan", "fake-model", "low"), SettingsOf(viewer));
        Assert.Equal("low", (await RecordWhen(r => r.Effort == "low")).Effort);
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

    [Fact]
    public async Task A_rename_is_asked_of_the_cli_by_its_own_request_and_is_never_a_message()
    {
        string script = Path.Combine(_dir, "fake-cli.ps1");
        File.WriteAllText(script, FakeCli);
        string transcript = Path.Combine(_dir, "transcript.jsonl");
        File.WriteAllText(transcript, """{"type":"user","uuid":"u-1","message":{"role":"user","content":"said before"}}""" + "\n");
        await Start(new HostSpec
        {
            Id = "chattest0001", SessionId = "planned-session", Provider = "Claude", Kind = ChatStream.Kind, HistoryPath = transcript,
            CommandLine = $"\"{SessionLauncher.ResolvePowerShell()}\" -NoLogo -NoProfile -File \"{script}\"",
            Cwd = _dir, HostsDir = Path.Combine(_dir, "hosts"), RingBytes = 256 * 1024,
        });
        using var viewer = await Viewer.Attach(Record(), after: 0);
        await viewer.Until(l => Type(l) == "host");
        await viewer.Pump(() => viewer.Catalog.HasValue);
        int lines = viewer.Lines.Count;

        await viewer.SendFrame("""{"type":"rename","request":"n1","name":"  ship\n  the thing  "}""");
        var done = await viewer.NextAnswer();
        // The name told back is the one the CLI gave, which the stand-in marks with who it was asked as and for which session.
        const string Named = "ship the thing [host planned-session]";
        Assert.Equal(("renamed", "n1", true, Named), (Type(done), done.GetProperty("request").GetString(), done.GetProperty("ok").GetBoolean(), done.GetProperty("name").GetString()));
        Assert.Equal(Named, viewer.Titles[^1]);
        var record = await RecordWhen(r => r.Title == Named);
        Assert.Equal("idle", record.AgentStatus);

        await viewer.SendFrame("""{"type":"rename","request":"n2","name":"taken"}""");
        var refused = await viewer.NextAnswer();
        Assert.Equal(("renamed", "n2", false, "that name is taken"), (Type(refused), refused.GetProperty("request").GetString(), refused.GetProperty("ok").GetBoolean(), refused.GetProperty("error").GetString()));
        await viewer.SendFrame("""{"type":"rename","request":"n3","name":" \n "}""");
        var blank = await viewer.NextAnswer();
        Assert.Equal(("n3", false, "Give the session a name: /rename <name>"), (blank.GetProperty("request").GetString(), blank.GetProperty("ok").GetBoolean(), blank.GetProperty("error").GetString()));
        Assert.Equal(Named, Record().Title);

        // Nothing was typed to the CLI and nothing joined the conversation: the next message is the first turn.
        Assert.Equal(lines, viewer.Lines.Count);
        Assert.DoesNotContain(viewer.Live, l => Type(l) == "control_response" && l.GetProperty("response").GetProperty("request_id").GetString()!.StartsWith(ChatStream.RenamePrefix, StringComparison.Ordinal));
        await viewer.Send(ChatStream.UserLine("after the rename"));
        var turn = await viewer.Until(l => Type(l) == "result");
        Assert.Equal(new[] { "user", "assistant", "result" }, turn.Select(Type));
        Assert.Equal(("after the rename", "echo: after the rename"), (Said(turn[0]), Said(turn[1])));

        await viewer.SendFrame("""{"type":"kill"}""");
        await viewer.Exited();
        Assert.True(_host!.WaitForExit(15000), "host did not exit after it was told to end");
    }

    async Task Start(HostSpec spec)
    {
        var psi = new ProcessStartInfo(HostExe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add("--host");
        _host = Process.Start(psi)!;
        _host.StandardInput.Write(JsonSerializer.Serialize(spec));
        _host.StandardInput.Close();
        Assert.Equal("ready", await _host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
    }

    const string Open = "aaaaaaaa-1111-4111-8111-111111111111", Bees = "bbbbbbbb-2222-4222-8222-222222222222", Dead = "deadbeef-3333-4333-8333-333333333333";

    static string Said(JsonElement line) =>
        line.GetProperty("message").GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()! : line.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task A_chat_moves_to_another_conversation_in_its_folder_and_a_pick_that_fails_brings_the_last_one_back()
    {
        string script = Path.Combine(_dir, "fake-cli.ps1");
        File.WriteAllText(script, FakeCli);
        string Transcript(string id, DateTime written, params string[] lines)
        {
            string path = Path.Combine(_dir, id + ".jsonl");
            File.WriteAllLines(path, lines);
            File.SetLastWriteTimeUtc(path, written);
            return path;
        }
        var t0 = DateTime.UtcNow.AddHours(-1);
        string open = Transcript(Open, t0.AddMinutes(3), """{"type":"user","uuid":"a-1","message":{"role":"user","content":"said in the open one"}}""");
        string bees = Transcript(Bees, t0.AddMinutes(2), """{"type":"user","uuid":"b-1","message":{"role":"user","content":"tell me about bees"}}""",
            """{"type":"assistant","uuid":"b-2","message":{"role":"assistant","content":[{"type":"text","text":"they hum"}]}}""", """{"type":"custom-title","customTitle":"Bees"}""");
        Transcript(Dead, t0.AddMinutes(1), """{"type":"user","uuid":"d-1","message":{"role":"user","content":"this one will not come up"}}""");
        string cli = $"\"{SessionLauncher.ResolvePowerShell()}\" -NoLogo -NoProfile -File \"{script}\"";
        await Start(new HostSpec
        {
            Id = "chattest0001", SessionId = Open, Provider = "Claude", Kind = ChatStream.Kind, HistoryPath = open,
            CommandLine = cli + " " + Open, ChatResumeCommand = cli + " " + ChatSessions.SessionPlaceholder,
            Cwd = _dir, HostsDir = Path.Combine(_dir, "hosts"), RingBytes = 256 * 1024, RestoreMode = "plan",
        });
        var record = Record();
        int firstChild = record.ChildPid;
        using var viewer = await Viewer.Attach(record, after: 0);
        Assert.Equal((0, false, Open), (viewer.Snapshot.GetProperty("epoch").GetInt32(), viewer.Snapshot.GetProperty("cut").GetBoolean(), viewer.Snapshot.GetProperty("session").GetString()));
        var before = await viewer.Until(l => Type(l) == "host");
        Assert.Equal("said in the open one", Said(before[0]));
        await viewer.Send("""{"type":"control_request","request_id":"sd-set-1","request":{"subtype":"apply_flag_settings","settings":{"effortLevel":"low"}}}""");
        await viewer.Pump(() => viewer.Settings.Any(f => f.GetProperty("effort").GetString() == "low" && f.GetProperty("mode").GetString() == "plan"));
        await RecordWhen(r => r.Effort == "low" && r.Model == "fake-model");

        // What there is to move to: every conversation in the folder but the one open here, newest first, by name.
        await viewer.SendFrame("""{"type":"sessions","request":"s1"}""");
        var list = await viewer.NextAnswer();
        Assert.Equal(("sessions", "s1", Open), (Type(list), list.GetProperty("request").GetString(), list.GetProperty("current").GetString()));
        Assert.Equal(new[] { (Bees, "Bees"), (Dead, "this one will not come up") }, list.GetProperty("sessions").EnumerateArray().Select(e => (e.GetProperty("id").GetString()!, e.GetProperty("title").GetString()!)));

        // Asked for something that is not a conversation here, or is the one already open: told so, and nothing changes.
        foreach (var (session, why) in new[] { ("..\\elsewhere", "not in this folder"), ("cccccccc-4444-4444-8444-444444444444", "not in this folder"), (Open, "the one open here") })
        {
            await viewer.SendFrame(JsonSerializer.Serialize(new { type = "resume", request = "r-no", session }));
            var refused = await viewer.NextAnswer();
            Assert.Equal(("resumed", false), (Type(refused), refused.GetProperty("ok").GetBoolean()));
            Assert.Contains(why, refused.GetProperty("error").GetString());
        }
        Assert.Equal(firstChild, Record().ChildPid);

        // Moved, once the CLI has spoken on the conversation picked: every viewer is told to start over, and gets it from its transcript.
        await viewer.SendFrame(JsonSerializer.Serialize(new { type = "resume", request = "r1", session = Bees }));
        await viewer.Pump(() => viewer.Snapshots.Count == 2);
        var moved = viewer.Snapshot;
        Assert.Equal((1, false, Bees, "Bees"), (moved.GetProperty("epoch").GetInt32(), moved.GetProperty("cut").GetBoolean(), moved.GetProperty("session").GetString(), moved.GetProperty("title").GetString()));
        Assert.Equal(moved.GetProperty("start").GetInt64(), moved.GetProperty("end").GetInt64());
        Assert.True(moved.GetProperty("start").GetInt64() > 0);
        // (What the stand-in prints outside the protocol comes before it introduces itself, and is no part of either conversation.)
        var after = await viewer.Until(l => Type(l) == "assistant");
        Assert.Equal(new[] { "tell me about bees", "they hum" }, after.Select(Said));
        Assert.All(after, l => Assert.True(l.GetProperty("history").GetBoolean()));
        record = await RecordWhen(r => r.SessionId == Bees && r.Effort == "low" && r.PermissionMode == "plan");
        Assert.Equal((bees, "Bees", "idle"), (record.TranscriptPath, record.Title, record.AgentStatus));
        Assert.NotEqual(firstChild, record.ChildPid);
        // Started on the conversation picked, on the model the chat was on, and back in the mode and effort it was in.
        Assert.EndsWith(Bees + " --model \"fake-model\"", record.CommandLine);
        await viewer.Send(ChatStream.UserLine("who"));
        Assert.Equal("i am " + Bees, Said((await viewer.Until(l => Type(l) == "result"))[1]));

        // A viewer that attaches now sees only this conversation, from a whole line, and can page it.
        using (var late = await Viewer.Attach(record, after: 0))
        {
            Assert.Equal((1, false, Bees), (late.Snapshot.GetProperty("epoch").GetInt32(), late.Snapshot.GetProperty("cut").GetBoolean(), late.Snapshot.GetProperty("session").GetString()));
            var replay = await late.Until(l => Type(l) == "result");
            Assert.Equal("tell me about bees", Said(replay[0]));
            Assert.DoesNotContain(replay, l => Type(l) == "user" && Said(l) == "said in the open one");
            await late.SendFrame("""{"type":"history","request":"h1","uuid":"b-2"}""");
            Assert.Equal(new[] { "b-1" }, (await late.NextHistory()).GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("uuid").GetString()));
        }

        // A pick whose CLI never says a word: no viewer is told to start over, the conversation before it is started again,
        // and what the CLI said for itself is both the answer to the request and a line in the conversation.
        int secondChild = record.ChildPid;
        await viewer.SendFrame(JsonSerializer.Serialize(new { type = "resume", request = "r2", session = Dead }));
        var failed = await viewer.NextAnswer();
        Assert.Equal(("resumed", false), (Type(failed), failed.GetProperty("ok").GetBoolean()));
        Assert.StartsWith("Could not resume that conversation: No conversation found with session ID: " + Dead, failed.GetProperty("error").GetString());
        var back = await viewer.Until(l => Type(l) == "host" && l.GetProperty("subtype").GetString() == "stderr");
        Assert.Equal(failed.GetProperty("error").GetString(), back[^1].GetProperty("text").GetString());
        // Nothing of the conversation that did not come up reached this one: not its transcript, not the error it ended on.
        Assert.Equal(new[] { "host" }, back.Select(Type));
        Assert.Equal(2, viewer.Snapshots.Count);
        record = await RecordWhen(r => r.ChildPid != secondChild && r.PermissionMode == "plan" && r.Effort == "low");
        Assert.Equal((Bees, bees, (uint?)null), (record.SessionId, record.TranscriptPath, record.ExitCode));
        await viewer.Send(ChatStream.UserLine("who"));
        Assert.Equal("i am " + Bees, Said((await viewer.Until(l => Type(l) == "result")).Last(l => Type(l) == "assistant")));
        // And it can still move: the failed pick did not leave it thinking a move is under way.
        await viewer.SendFrame(JsonSerializer.Serialize(new { type = "resume", request = "r3", session = Open }));
        await viewer.Pump(() => viewer.Snapshots.Count == 3);
        Assert.Equal((2, Open), (viewer.Snapshot.GetProperty("epoch").GetInt32(), viewer.Snapshot.GetProperty("session").GetString()));
        await viewer.Send(ChatStream.UserLine("who"));
        Assert.Equal("i am " + Open, Said((await viewer.Until(l => Type(l) == "result")).Last(l => Type(l) == "assistant")));

        // Ending the session is still ending it: the CLI going away is not read as making way for another conversation.
        // Also when a move was asked for in the same breath: the CLI ending for the move must not start the next conversation.
        await viewer.SendFrame(JsonSerializer.Serialize(new { type = "resume", request = "r4", session = Bees }));
        await viewer.SendFrame("""{"type":"kill"}""");
        await viewer.Exited();
        Assert.Equal(3, viewer.Snapshots.Count);
        Assert.True(_host!.WaitForExit(15000), "host did not exit after it was told to end");
        Assert.NotNull(Record().ExitedAt);
    }

    // Stands in for `codex app-server`: JSON-RPC, one object per line. It opens or resumes a thread, runs a turn
    // that echoes what was said, lists two threads, and pages each thread's one turn of history.
    const string FakeCodex = """
        [Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $out = [Console]::Out
        function Say($o) { $out.WriteLine(($o | ConvertTo-Json -Compress -Depth 12)) }
        $thread = ''
        $turn = 0
        while ($null -ne ($line = [Console]::In.ReadLine())) {
            $m = $line | ConvertFrom-Json
            if ($null -eq $m.id) { continue }
            switch ($m.method) {
                'initialize' { Say @{ id = $m.id; result = @{ userAgent = 'fake' } } }
                'model/list' { Say @{ id = $m.id; result = @{ data = @(@{ id = 'fake-codex'; model = 'fake-codex'; displayName = 'Fake Codex'; description = ''; hidden = $false; supportedReasoningEfforts = @(@{ reasoningEffort = 'low' }) }) } } }
                'thread/start' { $thread = 'cccccccc-1111-4111-8111-111111111111'; Say @{ id = $m.id; result = @{ thread = @{ id = $thread; preview = ''; name = $null; path = 'C:\rollouts\new.jsonl' }; model = 'fake-codex'; reasoningEffort = 'low' } } }
                'thread/resume' {
                    if ($m.params.threadId -like 'dead*') { Say @{ id = $m.id; error = @{ code = -32000; message = 'thread not found' } }; continue }
                    $thread = $m.params.threadId
                    Say @{ id = $m.id; result = @{ thread = @{ id = $thread; preview = 'asked there'; name = 'Other thread'; path = 'C:\rollouts\other.jsonl' }; model = 'fake-codex'; reasoningEffort = 'low' } }
                }
                'thread/turns/list' { Say @{ id = $m.id; result = @{ nextCursor = $null; data = @(@{ id = 'old-turn'; status = 'completed'; items = @(@{ type = 'userMessage'; id = 'old-u'; content = @(@{ type = 'text'; text = 'asked there' }) }, @{ type = 'agentMessage'; id = 'old-a'; text = 'answered there' }) }) } } }
                'thread/list' { Say @{ id = $m.id; result = @{ data = @(@{ id = $thread; name = $null; preview = 'this one'; updatedAt = 20 }, @{ id = 'dddddddd-2222-4222-8222-222222222222'; name = 'Other thread'; preview = 'asked there'; updatedAt = 10 }) } } }
                'turn/start' {
                    $turn++
                    $text = $m.params.input[0].text
                    Say @{ id = $m.id; result = @{ turn = @{ id = "turn-$turn"; status = 'inProgress' } } }
                    Say @{ method = 'turn/started'; params = @{ threadId = $thread; turn = @{ id = "turn-$turn" } } }
                    Say @{ method = 'item/completed'; params = @{ threadId = $thread; item = @{ type = 'userMessage'; id = "u-$turn"; content = @(@{ type = 'text'; text = $text }) } } }
                    if ($text -eq 'ask') { Say @{ id = 70; method = 'item/commandExecution/requestApproval'; params = @{ threadId = $thread; turnId = "turn-$turn"; itemId = 'exec-1'; command = 'make'; reason = 'builds' } }; continue }
                    Say @{ method = 'item/started'; params = @{ threadId = $thread; item = @{ type = 'agentMessage'; id = "a-$turn"; text = '' } } }
                    Say @{ method = 'item/agentMessage/delta'; params = @{ threadId = $thread; itemId = "a-$turn"; delta = 'echo' } }
                    Say @{ method = 'item/completed'; params = @{ threadId = $thread; item = @{ type = 'agentMessage'; id = "a-$turn"; text = "echo: $text in $thread" } } }
                    Say @{ method = 'turn/completed'; params = @{ threadId = $thread; turn = @{ id = "turn-$turn"; status = 'completed'; error = $null } } }
                }
                default {
                    # The answer to the approval it asked for: the turn goes on and ends.
                    if ($m.id -eq 70 -and $null -ne $m.result) {
                        Say @{ method = 'item/completed'; params = @{ threadId = $thread; item = @{ type = 'agentMessage'; id = 'a-ask'; text = "decision: $($m.result.decision)" } } }
                        Say @{ method = 'turn/completed'; params = @{ threadId = $thread; turn = @{ id = "turn-$turn"; status = 'completed'; error = $null } } }
                    }
                }
            }
        }
        """;

    [Fact]
    public async Task A_codex_chat_host_speaks_json_rpc_to_the_app_server_and_the_same_lines_to_a_viewer()
    {
        string script = Path.Combine(_dir, "fake-codex.ps1");
        File.WriteAllText(script, FakeCodex);
        await Start(new HostSpec
        {
            Id = "chattest0001", SessionId = "", Provider = "Codex", Kind = ChatStream.Kind,
            CommandLine = $"\"{SessionLauncher.ResolvePowerShell()}\" -NoLogo -NoProfile -File \"{script}\"",
            Cwd = _dir, HostsDir = Path.Combine(_dir, "hosts"), RingBytes = 256 * 1024, RestoreMode = CodexChat.Ask,
        });
        const string First = "cccccccc-1111-4111-8111-111111111111", Second = "dddddddd-2222-4222-8222-222222222222";
        // The thread is the app server's to name: the record learns it once the thread is open.
        var record = await RecordWhen(r => r.SessionId == First && r.Model == "fake-codex");
        Assert.Equal((ChatStream.Kind, "Codex", "default", "low", @"C:\rollouts\new.jsonl"), (record.Kind, record.Provider, record.PermissionMode, record.Effort, record.TranscriptPath));

        using var viewer = await Viewer.Attach(record, after: 0);
        await viewer.Pump(() => viewer.Catalog.HasValue && viewer.Settings.Count > 0);
        Assert.Equal(("Codex", "fake-codex"), (viewer.Catalog!.Value.GetProperty("agent").GetString(), viewer.Catalog.Value.GetProperty("models")[0].GetProperty("value").GetString()));
        Assert.Equal(("default", "fake-codex", "low"), SettingsOf(viewer));

        await viewer.Send(ChatStream.UserLine("héllo"));
        var turn = await viewer.Until(l => Type(l) == "result");
        Assert.Equal(new[] { "user", "assistant", "result" }, turn.Select(Type));
        Assert.Equal(("héllo", "echo: héllo in " + First), (Said(turn[0]), Said(turn[1])));
        // The start of the turn and the words as they came went to the viewer attached then, and not into the scrollback.
        Assert.Equal(new[] { "system", "stream_event", "stream_event" }, viewer.Live.Select(Type));
        record = await RecordWhen(r => r.AgentStatus == "idle" && r.LastEvent == "Stop");
        Assert.Equal("héllo", record.Title);
        Assert.Equal(new[] { "héllo" }, viewer.Titles);

        // An approval reaches the viewer as a request, and the viewer's answer reaches the app server as Codex names it.
        await viewer.Send(ChatStream.UserLine("ask"));
        var asked = await viewer.Until(l => Type(l) == "control_request");
        Assert.Equal(("cx-70", "Shell", "make"), (asked[^1].GetProperty("request_id").GetString(), asked[^1].GetProperty("request").GetProperty("tool_name").GetString(), asked[^1].GetProperty("request").GetProperty("input").GetProperty("command").GetString()));
        Assert.Equal("waiting", (await RecordWhen(r => r.AgentStatus == "waiting")).AgentStatus);
        await viewer.Send("""{"type":"control_response","response":{"subtype":"success","request_id":"cx-70","response":{"behavior":"allow","updatedPermissions":[{"type":"addRules"}]}}}""");
        var answered = await viewer.Until(l => Type(l) == "result");
        Assert.Equal(new[] { "control_response", "assistant", "result" }, answered.Select(Type));
        Assert.Equal("decision: acceptForSession", Said(answered[1]));

        // The conversations in the folder, without the one open here; then a move to one, in the same process.
        int child = Record().ChildPid;
        await viewer.SendFrame("""{"type":"sessions","request":"s1"}""");
        var list = await viewer.NextAnswer();
        Assert.Equal(new[] { (Second, "Other thread") }, list.GetProperty("sessions").EnumerateArray().Select(e => (e.GetProperty("id").GetString()!, e.GetProperty("title").GetString()!)));
        await viewer.SendFrame(JsonSerializer.Serialize(new { type = "resume", request = "r0", session = "dead0000-3333-4333-8333-333333333333" }));
        var refused = await viewer.NextAnswer();
        Assert.Equal((false, "Could not resume that conversation: thread not found"), (refused.GetProperty("ok").GetBoolean(), refused.GetProperty("error").GetString()));
        await viewer.SendFrame(JsonSerializer.Serialize(new { type = "resume", request = "r1", session = Second }));
        await viewer.Pump(() => viewer.Snapshots.Count == 2);
        Assert.Equal((1, Second, "Other thread"), (viewer.Snapshot.GetProperty("epoch").GetInt32(), viewer.Snapshot.GetProperty("session").GetString(), viewer.Snapshot.GetProperty("title").GetString()));
        var seeded = await viewer.Until(l => Type(l) == "assistant");
        Assert.Equal(new[] { "asked there", "answered there" }, seeded.Select(Said));
        Assert.All(seeded, l => Assert.True(l.GetProperty("history").GetBoolean()));
        record = await RecordWhen(r => r.SessionId == Second);
        Assert.Equal((child, "Other thread", @"C:\rollouts\other.jsonl"), (record.ChildPid, record.Title, record.TranscriptPath));
        await viewer.Send(ChatStream.UserLine("who"));
        Assert.Equal("echo: who in " + Second, Said((await viewer.Until(l => Type(l) == "result"))[1]));

        // Earlier turns are the app server's to page: asked by the oldest message shown, answered to the asker.
        await viewer.SendFrame("""{"type":"history","request":"h1","uuid":"old-u"}""");
        var page = await viewer.NextHistory();
        Assert.Equal(("h1", 0, JsonValueKind.Null), (page.GetProperty("request").GetString(), page.GetProperty("lines").GetArrayLength(), page.GetProperty("next").ValueKind));

        await viewer.SendFrame("""{"type":"kill"}""");
        await viewer.Exited();
        Assert.True(_host!.WaitForExit(15000), "host did not exit after it was told to end");
    }

    const string FakeAcp = """
        [Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $out = [Console]::Out
        function Say($o) { $out.WriteLine(($o | ConvertTo-Json -Compress -Depth 12)) }
        $session = 'ses_fakeacp00000000000000001'
        while ($null -ne ($line = [Console]::In.ReadLine())) {
            $m = $line | ConvertFrom-Json
            if ($null -eq $m.id) { continue }
            switch ($m.method) {
                'initialize' { Say @{ jsonrpc = '2.0'; id = $m.id; result = @{ protocolVersion = 1; agentCapabilities = @{ loadSession = $true; sessionCapabilities = @{ list = @{} } }; agentInfo = @{ name = 'Fake ACP'; version = '1' } } } }
                'session/new' { Say @{ jsonrpc = '2.0'; id = $m.id; result = @{ sessionId = $session; configOptions = @(@{ id = 'model'; name = 'Model'; category = 'model'; type = 'select'; currentValue = 'fake-model'; options = @(@{ value = 'fake-model'; name = 'Fake Model' }) }, @{ id = 'effort'; name = 'Effort'; category = 'thought_level'; type = 'select'; currentValue = 'low'; options = @(@{ value = 'low'; name = 'Low' }) }) } } }
                'session/prompt' {
                    $text = $m.params.prompt[0].text
                    Say @{ jsonrpc = '2.0'; method = 'session/update'; params = @{ sessionId = $session; update = @{ sessionUpdate = 'agent_message_chunk'; messageId = 'msg_fake_1'; content = @{ type = 'text'; text = "echo: $text in $($m.params.sessionId)" } } } }
                    Say @{ jsonrpc = '2.0'; id = $m.id; result = @{ stopReason = 'end_turn' } }
                }
            }
        }
        """;

    [Fact]
    public async Task An_acp_chat_host_sends_what_a_viewer_types_as_a_prompt_and_the_agents_reply_reaches_the_viewer()
    {
        string script = Path.Combine(_dir, "fake-acp.ps1");
        File.WriteAllText(script, FakeAcp);
        await Start(new HostSpec
        {
            Id = "chattest0001", SessionId = "", Provider = "OpenCode", Kind = ChatStream.Kind,
            CommandLine = $"\"{SessionLauncher.ResolvePowerShell()}\" -NoLogo -NoProfile -File \"{script}\"",
            Cwd = _dir, HostsDir = Path.Combine(_dir, "hosts"), RingBytes = 256 * 1024,
        });
        const string Session = "ses_fakeacp00000000000000001";
        var record = await RecordWhen(r => r.SessionId == Session && r.Model == "fake-model");
        Assert.Equal((ChatStream.Kind, "OpenCode", "low"), (record.Kind, record.Provider, record.Effort));

        using var viewer = await Viewer.Attach(record, after: 0);
        await viewer.Pump(() => viewer.Catalog.HasValue && viewer.Settings.Count > 0);
        Assert.Equal(("OpenCode", "fake-model"), (viewer.Catalog!.Value.GetProperty("agent").GetString(), viewer.Catalog.Value.GetProperty("models")[0].GetProperty("value").GetString()));
        Assert.Equal(("", "fake-model", "low"), SettingsOf(viewer));

        await viewer.Send(ChatStream.UserLine("héllo"));
        var turn = await viewer.Until(l => Type(l) == "result");
        Assert.Equal(new[] { "user", "assistant", "result" }, turn.Select(Type));
        Assert.Equal(("héllo", "echo: héllo in " + Session), (Said(turn[0]), Said(turn[1])));
        Assert.Equal("success", turn[2].GetProperty("subtype").GetString());
        Assert.Equal(new[] { "system", "stream_event", "stream_event" }, viewer.Live.Select(Type));
        record = await RecordWhen(r => r.AgentStatus == "idle" && r.LastEvent == "Stop");
        Assert.Equal("héllo", record.Title);

        // A rename is kept by the host beside its records, and the agent hears nothing of it: the next prompt it gets is the next message.
        await viewer.SendFrame("""{"type":"rename","request":"n1","name":"named here"}""");
        var renamed = await viewer.NextAnswer();
        Assert.Equal(("renamed", true, "named here"), (Type(renamed), renamed.GetProperty("ok").GetBoolean(), renamed.GetProperty("name").GetString()));
        Assert.Equal("named here", viewer.Titles[^1]);
        Assert.Equal("named here", (await RecordWhen(r => r.Title == "named here")).Title);
        Assert.Equal("named here", new ChatNames(_dir, "OpenCode").Of(Session));
        await viewer.Send(ChatStream.UserLine("again"));
        var second = await viewer.Until(l => Type(l) == "result");
        Assert.Equal(new[] { "user", "assistant", "result" }, second.Select(Type));
        Assert.Equal(("again", "echo: again in " + Session), (Said(second[0]), Said(second[1])));
        Assert.Equal("named here", Record().Title);

        await viewer.SendFrame("""{"type":"kill"}""");
        await viewer.Exited();
        Assert.True(_host!.WaitForExit(15000), "host did not exit after it was told to end");
    }

    HostRecord Record() => HostManager.ReadRecord(Path.Combine(_dir, "hosts", "chattest0001.json")) ?? throw new IOException("the host record could not be read");

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
        readonly Queue<JsonElement> _history = new();
        readonly Queue<JsonElement> _answers = new();
        public List<JsonElement> Snapshots { get; } = new();

        /// <summary>The next answer to a request of this viewer's own: a list of conversations, or a refusal to move.</summary>
        public async Task<JsonElement> NextAnswer()
        {
            while (_answers.Count == 0) await Read();
            return _answers.Dequeue();
        }

        /// <summary>A frame of the viewer's own, not typed input: a request the host answers.</summary>
        public Task SendFrame(string json) => _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<JsonElement> NextHistory()
        {
            while (_history.Count == 0) await Read();
            return _history.Dequeue();
        }

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
                    case "snapshot": Snapshot = frame; Snapshots.Add(frame); break;
                    case "sessions": case "resumed": case "renamed": _answers.Enqueue(frame); break;
                    case "live": Live.Add(frame.GetProperty("line").Clone()); break;
                    case "title": Titles.Add(frame.GetProperty("title").GetString() ?? ""); break;
                    case "catalog": Catalog = frame; break;
                    case "settings": Settings.Add(frame); break;
                    case "history": _history.Enqueue(frame); break;
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
