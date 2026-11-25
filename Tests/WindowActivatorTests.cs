using Xunit;

namespace SessionDeck.Tests;

public class WindowActivatorTests
{
    const int Deck = 500, DeckHost = 510, DeckCli = 511, Terminal = 600, Shell = 610, OtherCli = 611;
    static readonly IntPtr DeckWindow = new(0x5000), TerminalWindow = new(0x6000);

    static Dictionary<int, List<(IntPtr Hwnd, string Title)>> Windows() => new()
    {
        [Deck] = new() { (DeckWindow, "Session Deck") },
        [Terminal] = new() { (TerminalWindow, "Windows Terminal") },
    };

    static Dictionary<int, int> Parents() => new() { [DeckCli] = DeckHost, [DeckHost] = Deck, [OtherCli] = Shell, [Shell] = Terminal };

    static SessionInfo Session(string id, int pid, string name) => new() { SessionId = id, Pid = pid, Name = name, Provider = SessionProvider.Claude };

    [Fact]
    public void The_decks_own_window_is_never_read_through_UI_Automation_for_a_session_it_hosts()
    {
        var swept = new List<IntPtr>();
        var found = WindowActivator.ResolveWindows(
            new[] { Session("in-deck", DeckCli, "in deck"), Session("outside", OtherCli, "outside") },
            Windows(), Parents(), ownPid: Deck,
            windows => { swept.AddRange(windows); return new() { (TerminalWindow, "outside") }; });

        Assert.Equal(new[] { TerminalWindow }, swept);
        Assert.Equal(DeckWindow, found["in-deck"]);
        Assert.Equal(TerminalWindow, found["outside"]);
    }

    [Fact]
    public void With_only_its_own_sessions_there_is_no_UI_Automation_pass_at_all()
    {
        int passes = 0;
        var found = WindowActivator.ResolveWindows(
            new[] { Session("in-deck", DeckCli, "in deck") }, Windows(), Parents(), ownPid: Deck,
            windows => { passes++; return new(); });

        Assert.Equal(0, passes);
        Assert.Equal(DeckWindow, found["in-deck"]);
    }

    [Fact]
    public void A_session_in_another_programs_window_is_still_found_by_its_tab()
    {
        var found = WindowActivator.ResolveWindows(
            new[] { Session("outside", OtherCli, "outside") }, Windows(), Parents(), ownPid: Deck,
            windows => new() { (TerminalWindow, "outside") });

        Assert.Equal(TerminalWindow, found["outside"]);
    }
}
