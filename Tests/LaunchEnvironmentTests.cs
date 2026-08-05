using System.Diagnostics;
using Xunit;

namespace ClaudeSessionMonitor.Tests;

/// <summary>
/// Guards the launched terminal's environment and argument vector.
///
/// The bug these exist for: terminals are started with <c>UseShellExecute=false</c>, so the child
/// inherits this app's environment. An agent harness that sets <c>NO_COLOR=1</c> for clean tool output
/// relaunched the app as a child, and every session opened from the buttons afterwards was monochrome —
/// with no code change anywhere, which is what made it hard to see. If a future refactor stops
/// scrubbing the child environment, or quietly changes how the terminal is invoked, these fail.
/// </summary>
public class LaunchEnvironmentTests
{
    const string NoColor = "NO_COLOR";

    static ProcessStartInfo Build(LaunchTarget target = LaunchTarget.NewWindow) =>
        // The strip list is passed explicitly so the test never depends on the real machine's
        // environment — otherwise a dev box with a genuine NO_COLOR would flip the result.
        SessionLauncher.BuildTerminalStart(@"C:\x", "claude", target, new[] { NoColor });

    [Fact]
    public void A_launched_terminal_does_not_inherit_NO_COLOR()
    {
        var prior = Environment.GetEnvironmentVariable(NoColor);
        try
        {
            Environment.SetEnvironmentVariable(NoColor, "1");
            var psi = Build();
            Assert.False(psi.Environment.ContainsKey(NoColor),
                "NO_COLOR reached the child environment — every launched session will be monochrome.");
        }
        finally { Environment.SetEnvironmentVariable(NoColor, prior); }
    }

    [Fact]
    public void The_fallback_terminal_scrubs_the_environment_too()
    {
        var prior = Environment.GetEnvironmentVariable(NoColor);
        try
        {
            Environment.SetEnvironmentVariable(NoColor, "1");
            var psi = SessionLauncher.BuildFallbackStart(@"C:\x", "claude", new[] { NoColor });
            Assert.False(psi.Environment.ContainsKey(NoColor));
            // UseShellExecute must stay false: it is the only mode where the environment is editable
            // at all, so flipping it back to true would silently reopen the hole.
            Assert.False(psi.UseShellExecute);
        }
        finally { Environment.SetEnvironmentVariable(NoColor, prior); }
    }

    [Fact]
    public void Scrubbing_leaves_the_rest_of_the_environment_alone()
    {
        var psi = Build();
        Assert.True(psi.Environment.Count > 5);                  // still a real inherited environment
        Assert.True(psi.Environment.ContainsKey("PATH") || psi.Environment.ContainsKey("Path"));
    }

    // A user who genuinely sets NO_COLOR in their own environment wants no color, and must keep
    // getting none. Only the injected case -- present on the process, persisted nowhere -- is dropped.
    [Theory]
    [InlineData("1", null, null, true)]    // injected by whatever launched us -> drop it
    [InlineData("1", "1", null, false)]    // the user chose this -> honor it
    [InlineData("1", null, "1", false)]    // machine-wide policy -> honor it
    [InlineData(null, null, null, false)]  // not set at all -> nothing to do
    [InlineData("", null, null, false)]    // empty is not "set"
    public void Only_an_injected_variable_is_dropped(string? process, string? user, string? machine, bool expected)
        => Assert.Equal(expected, SessionLauncher.WasInjectedIntoThisProcess(process, user, machine));

    // The invocation itself. `;` is Windows Terminal's own subcommand separator, so a command
    // containing one would be split across tabs -- worth knowing if flags are ever templated in.
    [Fact]
    public void A_new_window_is_requested_with_w_minus_one()
    {
        var argv = Build(LaunchTarget.NewWindow).ArgumentList;
        Assert.Equal(new[] { "-w", "-1", "new-tab", "-d", @"C:\x", "pwsh", "-NoExit", "-Command", "claude" }, argv);
    }

    [Fact]
    public void A_new_tab_targets_the_last_used_window_with_w_zero()
    {
        var argv = Build(LaunchTarget.LastWindow).ArgumentList;
        Assert.Equal("0", argv[1]);
        Assert.Equal("new-tab", argv[2]);
    }

    [Fact]
    public void The_command_is_passed_as_one_argument_so_the_shell_cannot_resplit_it()
    {
        var psi = SessionLauncher.BuildTerminalStart(@"C:\x", "claude --name 'a b'", LaunchTarget.NewWindow,
                                                     Array.Empty<string>());
        Assert.Equal("claude --name 'a b'", psi.ArgumentList[^1]);
    }
}
