using Xunit;

namespace ClaudeSessionMonitor.Tests;

public class CodexProbeTests
{
    [Fact]
    public void First_confirmed_unowned_result_marks_a_miss_but_retains_the_session()
    {
        bool forget = CodexScanner.ShouldForgetAfterVerification(
            FileOwnerState.Unowned, previousMisses: 0, out int nextMisses);

        Assert.False(forget);
        Assert.Equal(1, nextMisses);
    }

    [Fact]
    public void Second_confirmed_unowned_result_forgets_the_session()
    {
        bool forget = CodexScanner.ShouldForgetAfterVerification(
            FileOwnerState.Unowned, previousMisses: 1, out int nextMisses);

        Assert.True(forget);
        Assert.Equal(2, nextMisses);
    }

    [Fact]
    public void Indeterminate_result_retains_owner_and_does_not_count_as_a_miss()
    {
        bool forget = CodexScanner.ShouldForgetAfterVerification(
            FileOwnerState.Indeterminate, previousMisses: 1, out int nextMisses);

        Assert.False(forget);
        Assert.Equal(1, nextMisses);
    }

    [Fact]
    public void Owned_result_clears_an_existing_miss()
    {
        bool forget = CodexScanner.ShouldForgetAfterVerification(
            FileOwnerState.Owned, previousMisses: 1, out int nextMisses);

        Assert.False(forget);
        Assert.Equal(0, nextMisses);
    }

    [Fact]
    public void Indeterminate_owner_result_preserves_restart_manager_diagnostics()
    {
        var result = FileOwnerResult.Indeterminate("list", 234);

        Assert.Equal(FileOwnerState.Indeterminate, result.State);
        Assert.Equal("list", result.ErrorStage);
        Assert.Equal(234, result.ErrorCode);
        Assert.Equal(0, result.Pid);
    }

    [Theory]
    [InlineData((int)FileOwnerState.Owned, true)]
    [InlineData((int)FileOwnerState.Unowned, true)]
    [InlineData((int)FileOwnerState.Indeterminate, false)]
    public void Only_conclusive_candidate_results_are_cached(int state, bool expected)
    {
        Assert.Equal(expected, CodexScanner.ShouldCacheCandidateResult((FileOwnerState)state));
    }
}
