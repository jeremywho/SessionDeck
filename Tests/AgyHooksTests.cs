using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class AgyHooksTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void The_hooks_file_wires_the_three_events_through_curl_and_names_each_in_the_query()
    {
        string json = Hooks.AgyHooksJson(47631, "ABCDEF");
        using var doc = JsonDocument.Parse(json);
        var named = doc.RootElement.GetProperty("sessiondeck");
        foreach (var ev in new[] { "SessionStart", "PreInvocation", "Stop" })
        {
            var handler = named.GetProperty(ev)[0];
            string command = handler.GetProperty("command").GetString()!;
            Assert.Contains("curl.exe", command);
            Assert.Contains("http://127.0.0.1:47631/hook/" + ev + "?token=ABCDEF", command);
            Assert.Equal(5, handler.GetProperty("timeout").GetInt32());
        }
        Assert.False(named.TryGetProperty("PreToolUse", out _));
        Assert.DoesNotContain('\\', json);
    }

    [Theory]
    [InlineData("/hook/Stop?token=X", "Stop")]
    [InlineData("/hook/PreInvocation", "PreInvocation")]
    [InlineData("/hook?token=X", "")]
    [InlineData("/hook", "")]
    public void The_event_is_read_from_the_path_when_the_forwarder_put_it_there(string path, string expected) =>
        Assert.Equal(expected, HostProgram.EventFromPath(path));

    [Fact]
    public void A_model_call_means_working_and_a_stop_means_the_turn_ended()
    {
        Assert.Equal("busy", Hooks.StatusFor("PreInvocation", J("{\"invocationNum\": 0}"), pending: false));
        Assert.Equal("idle", Hooks.StatusFor("Stop", J("{\"terminationReason\": \"NO_TOOL_CALL\", \"fullyIdle\": true}"), pending: false));
        Assert.Equal("idle", Hooks.StatusFor("SessionStart", J("{\"conversationId\": \"x\"}"), pending: false));
    }

    [Fact]
    public void The_event_comes_from_the_body_when_it_names_one_and_from_the_query_otherwise()
    {
        Assert.Equal("Stop", HostProgram.EventName(J("{\"hook_event_name\": \"Stop\"}"), "PreInvocation"));
        Assert.Equal("PreInvocation", HostProgram.EventName(J("{\"conversationId\": \"x\"}"), "PreInvocation"));
        Assert.Equal("", HostProgram.EventName(J("{\"hook_event_name\": \"\"}"), ""));
    }

    [Fact]
    public void Agy_launches_and_resumes_with_its_hooks_folder_and_the_skip_permissions_flag()
    {
        Assert.Equal("agy --dangerously-skip-permissions --add-dir \"{SD_AGY_HOOKS_DIR}\"", HostManager.NewAgyCommand(HostManager.AgyDefaultFlags));
        Assert.Equal("agy --conversation b34a47d3-a8cf-4e41-98cc-7eca5276e0f5 --dangerously-skip-permissions --add-dir \"{SD_AGY_HOOKS_DIR}\"",
            HostManager.ResumeAgyCommand("b34a47d3-a8cf-4e41-98cc-7eca5276e0f5", HostManager.AgyDefaultFlags));
        Assert.Equal("agy --conversation b34a47d3-a8cf-4e41-98cc-7eca5276e0f5",
            SessionLauncher.ResumeCommand(new SavedSession { Id = "b34a47d3-a8cf-4e41-98cc-7eca5276e0f5", Provider = SessionProvider.Agy }, ""));
    }

    // Seen live on 2026-10-28: an agy pane ran a codex exec, the exec was paired to the pane by
    // descent, and the row turned into a Codex session named Oura; the registry then alternated
    // between Codex and Agy for the same conversation id.
    [Fact]
    public void A_tool_running_under_an_agy_pane_never_becomes_the_panes_session()
    {
        var agyHost = new HostRecord { Id = "h1", SessionId = "", Provider = "Agy", HostPid = 1, ChildPid = 10, StartedAt = DateTime.UtcNow };
        var codexExec = new SessionInfo { SessionId = "t1", Pid = 99, Provider = SessionProvider.Codex, Kind = "exec", Model = "gpt-6-astra" };
        var pairs = HostPairing.Pair(new[] { agyHost }, new[] { codexExec }, (_, _) => true, new HashSet<SessionInfo>());
        Assert.NotSame(codexExec, pairs.Single().Info);
        Assert.Equal(SessionProvider.Agy, pairs.Single().Info.Provider);

        var claudeHost = new HostRecord { Id = "h2", SessionId = "", Provider = "Shell", HostPid = 1, ChildPid = 20, StartedAt = DateTime.UtcNow };
        Assert.False(HostPairing.CanDescend(codexExec, claudeHost));
        Assert.True(HostPairing.CanDescend(new SessionInfo { Provider = SessionProvider.Codex, Kind = "tui" }, new HostRecord { Provider = "Codex" }));
        Assert.True(HostPairing.CanDescend(new SessionInfo { Provider = SessionProvider.Claude, Kind = "interactive" }, claudeHost));
    }

    // Seen live the same day: the app restarted for an update, the pre-hook Oura pane had no
    // session id on its host, so the registry's Oura looked orphaned and was resumed a second time.
    [Fact]
    public void A_running_pre_hook_agy_pane_still_counts_its_conversation_as_live()
    {
        var table = new Dictionary<int, (int Parent, string Name)> { [10] = (1, "pwsh.exe"), [11] = (10, "agy.exe") };
        var hooked = new HostRecord { Id = "a", SessionId = "conv-a", Provider = "Agy", ChildPid = 50 };
        var preHook = new HostRecord { Id = "b", SessionId = "", Provider = "Agy", ChildPid = 10 };
        var shell = new HostRecord { Id = "c", SessionId = "", Provider = "Shell", ChildPid = 10 };
        var ids = App.HostedConversations(new[] { hooked, preHook, shell }, table).ToList();
        Assert.Equal(new[] { "conv-a" }, ids);
    }

    [Fact]
    public void An_agy_host_is_an_agy_row_not_a_claude_one()
    {
        Assert.Equal(SessionProvider.Agy, SessionRow.ProviderOf("Agy"));
        Assert.Equal(SessionProvider.Codex, SessionRow.ProviderOf("codex"));
        Assert.Equal(SessionProvider.Claude, SessionRow.ProviderOf("Claude"));
        Assert.Equal(SessionProvider.Claude, SessionRow.ProviderOf("Shell"));
    }

    /// <summary>The pane's shell is 50; its agy is 51; a hook's curl runs under cmd under the agy that fired it.</summary>
    static readonly Dictionary<int, (int Parent, string Name)> Tree = new()
    {
        [50] = (1, "pwsh.exe"),
        [51] = (50, "agy.exe"),
        [52] = (51, "cmd.exe"),
        [53] = (52, "curl.exe"),
        [60] = (51, "pwsh.exe"),
        [61] = (60, "agy.exe"),
        [62] = (61, "cmd.exe"),
        [63] = (62, "curl.exe"),
        [70] = (61, "bash.exe"),
        [71] = (70, "agy.exe"),
        [72] = (71, "curl.exe"),
        [90] = (1, "agy.exe"),
        [91] = (90, "curl.exe"),
    };

    [Fact]
    public void The_panes_own_agy_is_one_hop_and_a_fanned_out_agent_is_two_or_more()
    {
        Assert.Equal(1, Hooks.AgyHops(Tree, 53, 50));
        Assert.False(Hooks.FromChildAgy(Hooks.AgyHops(Tree, 53, 50)));
        Assert.Equal(2, Hooks.AgyHops(Tree, 63, 50));
        Assert.True(Hooks.FromChildAgy(Hooks.AgyHops(Tree, 63, 50)));
        Assert.Equal(3, Hooks.AgyHops(Tree, 72, 50));
        Assert.True(Hooks.FromChildAgy(Hooks.AgyHops(Tree, 72, 50)));
    }

    [Fact]
    public void A_pane_whose_child_is_agy_itself_counts_the_same_way()
    {
        // Seen in an isolated host: `agy -p …` as the pane's command runs agy.exe as the pty child, no shell.
        var tree = new Dictionary<int, (int Parent, string Name)>
        {
            [40] = (1, "agy.exe"),
            [41] = (40, "cmd.exe"),
            [42] = (41, "curl.exe"),
            [43] = (40, "pwsh.exe"),
            [44] = (43, "agy.exe"),
            [45] = (44, "cmd.exe"),
            [46] = (45, "curl.exe"),
        };
        Assert.Equal(1, Hooks.AgyHops(tree, 42, 40));
        Assert.False(Hooks.FromChildAgy(Hooks.AgyHops(tree, 42, 40)));
        Assert.Equal(2, Hooks.AgyHops(tree, 46, 40));
        Assert.True(Hooks.FromChildAgy(Hooks.AgyHops(tree, 46, 40)));
    }

    [Fact]
    public void The_agents_a_pane_fanned_out_to_are_never_sessions_to_restore()
    {
        var pane = new HostRecord { Id = "a", SessionId = "whoop-conv", Provider = "Agy", ChildConversations = { "child-1", "child-2" } };
        var other = new HostRecord { Id = "b", SessionId = "oura-conv", Provider = "Agy", ChildConversations = { "CHILD-2", "child-3" } };
        var claude = new HostRecord { Id = "c", SessionId = "s", Provider = "Claude" };
        Assert.Equal(new[] { "child-1", "child-2", "child-3" }, App.ChildConversations(new[] { pane, other, claude }).ToList());
        var back = System.Text.Json.JsonSerializer.Deserialize<HostRecord>(System.Text.Json.JsonSerializer.Serialize(pane))!;
        Assert.Equal(new[] { "child-1", "child-2" }, back.ChildConversations);
        var old = System.Text.Json.JsonSerializer.Deserialize<HostRecord>("{\"Id\":\"old\",\"SessionId\":\"x\"}")!;
        Assert.Empty(App.ChildConversations(new[] { old }));
    }

    [Fact]
    public void A_sender_outside_the_pane_or_unknown_is_taken_at_its_word()
    {
        Assert.Equal(-1, Hooks.AgyHops(Tree, 91, 50));
        Assert.False(Hooks.FromChildAgy(Hooks.AgyHops(Tree, 91, 50)));
        Assert.Equal(-1, Hooks.AgyHops(Tree, 12345, 50));
        Assert.False(Hooks.FromChildAgy(-1));
        Assert.False(Hooks.FromChildAgy(0));
    }

    [Fact]
    public void The_tcp_table_names_the_process_behind_a_loopback_connection()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = listener.AcceptTcpClient();
            int clientPort = ((System.Net.IPEndPoint)accepted.Client.RemoteEndPoint!).Port;
            Assert.Equal(Environment.ProcessId, TcpPeer.OwningPid(clientPort));
            Assert.Equal(0, TcpPeer.OwningPid(1));
        }
        finally { listener.Stop(); }
    }
}
