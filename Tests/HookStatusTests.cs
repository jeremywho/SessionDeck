using System.Text.Json;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class HookStatusTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_turn_that_armed_a_wakeup_ends_scheduled_not_idle()
    {
        Assert.Equal("scheduled", Hooks.StatusFor("Stop", J("{}"), pending: true));
        Assert.Equal("idle", Hooks.StatusFor("Stop", J("{}"), pending: false));
    }

    [Theory]
    [InlineData("ScheduleWakeup", "{}", true)]
    [InlineData("CronCreate", "{}", true)]
    [InlineData("Monitor", "{}", true)]
    [InlineData("Bash", "{\"run_in_background\": true}", false)]
    [InlineData("Bash", "{\"run_in_background\": false}", false)]
    [InlineData("Bash", "{}", false)]
    [InlineData("Read", "{}", false)]
    public void Only_tools_that_wake_the_session_later_defer(string tool, string input, bool expected)
    {
        Assert.Equal(expected, Hooks.Defers(J($"{{\"tool_name\": \"{tool}\", \"tool_input\": {input}}}")));
    }

    [Fact]
    public void Claudes_idle_notification_changes_nothing()
    {
        Assert.Null(Hooks.StatusFor("Notification", J("{\"notification_type\": \"idle_prompt\", \"message\": \"Claude is waiting for your input\"}"), pending: false));
    }

    [Theory]
    [InlineData("{\"notification_type\": \"permission_prompt\"}")]
    [InlineData("{\"notification_type\": \"elicitation_dialog\"}")]
    [InlineData("{\"message\": \"Claude needs your permission to use Bash\"}")]
    public void Prompts_for_a_person_are_waiting(string json)
    {
        Assert.Equal("waiting", Hooks.StatusFor("Notification", J(json), pending: false));
    }

    [Fact]
    public void Asking_the_user_a_question_is_waiting_at_once()
    {
        Assert.Equal("waiting", Hooks.StatusFor("PreToolUse", J("{\"tool_name\": \"AskUserQuestion\"}"), pending: false));
        Assert.Equal("busy", Hooks.StatusFor("PreToolUse", J("{\"tool_name\": \"Bash\"}"), pending: false));
    }
}
