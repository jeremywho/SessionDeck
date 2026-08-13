using Xunit;

namespace ClaudeSessionMonitor.Tests;

public class SessionRowTests
{
    static SessionRow Row(SessionInfo s) => new(s);

    static SessionRow WithModel(string model, string effort = "", SessionProvider p = SessionProvider.Claude) =>
        Row(new SessionInfo { SessionId = "x", Model = model, Effort = effort, Provider = p });

    // The pill regressed on exactly this: the original pattern required TWO version numbers
    // (`-(\d+)-(\d+)`), so `claude-opus-4-8` rendered fine while `claude-opus-5` and `claude-fable-5`
    // — two of the three models actually in use — fell through and showed the raw id.
    [Theory]
    [InlineData("claude-opus-4-8", "Opus 4.8")]
    [InlineData("claude-opus-5", "Opus 5")]
    [InlineData("claude-fable-5", "Fable 5")]
    [InlineData("claude-sonnet-5", "Sonnet 5")]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")]   // trailing release date is ignored
    [InlineData("claude-opus-5[1m]", "Opus 5")]              // bracketed context variant is ignored
    public void Claude_model_ids_shorten_to_family_and_version(string id, string expected)
        => Assert.Equal(expected, WithModel(id).ModelChip);

    [Theory]
    [InlineData("gpt-5.6-sol", "Sol")]
    [InlineData("gpt-5.6-codex", "Codex")]
    [InlineData("gpt-5.6", "GPT-5.6")]
    public void Codex_model_ids_prefer_the_named_variant(string id, string expected)
        => Assert.Equal(expected, WithModel(id, p: SessionProvider.Codex).ModelChip);

    [Fact]
    public void An_unrecognised_model_id_is_shown_as_is_rather_than_blanked()
        => Assert.Equal("some-new-model", WithModel("some-new-model").ModelChip);

    [Fact]
    public void No_model_means_no_pill()
    {
        var row = WithModel("");
        Assert.False(row.HasModel);
        Assert.Equal("", row.ModelChip);
    }

    // Both CLIs report an effort now, so this turns on whether one was seen — not on which CLI it
    // came from. An empty string has to keep the trailing text collapsed rather than render a stray
    // gap in the pill.
    [Fact]
    public void Effort_shows_only_when_the_cli_reports_one()
    {
        Assert.True(WithModel("gpt-5.6-sol", "ultra", SessionProvider.Codex).HasEffort);
        Assert.True(WithModel("claude-opus-5", "max").HasEffort);
        Assert.False(WithModel("claude-opus-5").HasEffort);
    }

    [Fact]
    public void A_claude_effort_reaches_the_model_tooltip()
    {
        Assert.Equal("Claude Code · claude-fable-5 · max effort", WithModel("claude-fable-5", "max").ModelTooltip);
    }

    [Fact]
    public void Provider_marks_differ_so_the_two_clis_are_distinguishable()
    {
        var claude = WithModel("claude-opus-5");
        var codex = WithModel("gpt-5.6-sol", "ultra", SessionProvider.Codex);
        Assert.NotEqual(claude.ProviderGlyph, codex.ProviderGlyph);
        Assert.False(claude.IsCodex);
        Assert.True(codex.IsCodex);
    }

    // Update raises only what changed: a blanket raise of every property on the 2s tick made the
    // grid re-run bindings, converters, and live-sort placement for every row while nothing was
    // visibly different. An identical scan must be silent; a real change must still notify.
    [Fact]
    public void Update_with_identical_data_raises_nothing()
    {
        var row = Row(new SessionInfo { SessionId = "x", Pid = 1, Status = "busy", Model = "claude-opus-5" });
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        row.Update(new SessionInfo { SessionId = "x", Pid = 1, Status = "busy", Model = "claude-opus-5" });

        Assert.Empty(raised);
    }

    [Fact]
    public void Update_with_a_status_change_raises_the_state_and_sort_properties()
    {
        var row = Row(new SessionInfo { SessionId = "x", Pid = 1, Status = "busy", Model = "claude-opus-5" });
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        row.Update(new SessionInfo { SessionId = "x", Pid = 1, Status = "waiting", Model = "claude-opus-5" });

        Assert.Contains(nameof(SessionRow.Status), raised);
        Assert.Contains(nameof(SessionRow.State), raised);
        Assert.Contains(nameof(SessionRow.SortPriority), raised);
        Assert.DoesNotContain(nameof(SessionRow.Name), raised);       // unchanged fields stay silent
        Assert.DoesNotContain(nameof(SessionRow.ContextPct), raised);
    }

    // Codex reports its own window per turn; Claude's isn't exposed to a standalone app, so those rows
    // fall back to the app-wide default. Sharing one divisor would have shown Codex sessions at ~a
    // quarter of their real fullness (258k window scored against 1M).
    [Fact]
    public void Context_pct_uses_the_sessions_own_window_when_it_reports_one()
    {
        var codex = Row(new SessionInfo
        {
            SessionId = "c", Provider = SessionProvider.Codex,
            Model = "gpt-5.6-sol", ContextTokens = 129_200, ContextWindow = 258_400,
        });
        Assert.Equal(50, codex.ContextPct);

        var claude = Row(new SessionInfo { SessionId = "a", Model = "claude-opus-5", ContextTokens = 500_000 });
        Assert.Equal(50, claude.ContextPct);   // no per-session window -> the 1M default
    }
}
