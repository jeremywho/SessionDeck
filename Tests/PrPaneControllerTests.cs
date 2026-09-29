using Xunit;

namespace SessionDeck.Tests;

public class PrPaneControllerTests
{
    [Theory]
    [InlineData("Working", "working")]
    [InlineData("Awaiting", "awaiting")]
    [InlineData("Error", "error")]
    [InlineData("Scheduled", "scheduled")]
    [InlineData("Completed", "idle")]
    [InlineData("Idle", "idle")]
    public void Session_states_map_to_the_words_the_pane_shows(string state, string token) =>
        Assert.Equal(token, PrPaneController.StateToken(Enum.Parse<SessionState>(state)));

    [Fact]
    public void Github_calls_keep_a_minimum_gap()
    {
        var t = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(PrPaneController.PastMinGap(t, t.AddSeconds(14)));
        Assert.True(PrPaneController.PastMinGap(t, t.AddSeconds(15)));
        Assert.True(PrPaneController.PastMinGap(DateTime.MinValue, t));
    }

    [Fact]
    public void Sources_skip_sessions_without_a_transcript_dedupe_and_carry_folded_threads()
    {
        var codex = new SessionInfo { Provider = SessionProvider.Codex, SessionId = "t1", Kind = "exec", Cwd = @"D:\wt", TranscriptPath = @"C:\r.jsonl" };
        var owner = new SessionInfo { SessionId = "s1", Kind = "interactive", TranscriptPath = @"C:\s1.jsonl" };
        owner.FoldedThreads.Add(codex);
        var shell = new SessionInfo();
        var dup = new SessionInfo { SessionId = "S1", TranscriptPath = @"C:\s1.jsonl" };
        var s = Assert.Single(PrPaneController.SourcesFrom(new[] { owner, shell, dup }));
        Assert.Equal(("s1", @"C:\s1.jsonl", "interactive"), (s.SessionId, s.TranscriptPath, s.Kind));
        Assert.Equal(new[] { (@"C:\r.jsonl", SessionProvider.Codex, "exec", @"D:\wt") }, s.Folded.Select(f => (f.TranscriptPath, f.Provider, f.Kind, f.Cwd)));
    }

    [Fact]
    public void A_refetch_of_the_same_data_does_not_change_the_signature()
    {
        var t = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var board = new Board("ok", "", Array.Empty<string>(), t, 150, 0, Array.Empty<BoardSection>());
        Assert.Equal(PrPaneController.Signature(board), PrPaneController.Signature(board with { FetchedAt = t.AddMinutes(1), Error = "timeout" }));
        Assert.NotEqual(PrPaneController.Signature(board), PrPaneController.Signature(board with { Total = 3 }));
    }

    [Fact]
    public void An_unowned_codex_thread_is_named_by_its_kind_and_is_not_clickable()
    {
        var v = PrPaneController.DescribeUnowned(new SessionInfo { Provider = SessionProvider.Codex, SessionId = "01a0e8d7-7a54", Kind = "exec" });
        Assert.Equal(("Codex exec 01a0e8d7", "Codex", "working", false), (v.Name, v.Provider, v.State, v.Clickable));
    }
}
