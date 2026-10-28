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
}
