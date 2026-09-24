using System.IO;
using System.Text.Json;
using SessionDeck.Host;

namespace SessionDeck;

/// <summary>
/// One terminal inside the shared <see cref="DeckBrowser"/> page, attached to one pty host. The page
/// owns the xterm and the socket; this side only names the terminal, relays its title and exit, and
/// asks the page to show, hide or drop it.
/// </summary>
internal sealed class TerminalView
{
    readonly DeckBrowser _browser;

    public HostRecord Host { get; }
    public string Title { get; private set; }
    public bool Exited { get; private set; }

    public event Action<TerminalView>? TitleChanged;
    public event Action<TerminalView>? ExitedChanged;

    public TerminalView(DeckBrowser browser, HostRecord host)
    {
        _browser = browser;
        Host = host;
        Title = string.IsNullOrWhiteSpace(host.Title) ? DefaultTitle(host) : host.Title;
        Exited = host.HasExited;
    }

    static string DefaultTitle(HostRecord h) =>
        h.Provider + " · " + (string.IsNullOrEmpty(h.Cwd) ? "" : Path.GetFileName(h.Cwd.TrimEnd('\\', '/')));

    public void Open() => _browser.Post(new { type = "open", id = Host.Id, port = Host.Port, token = Host.Token });
    public void Close() => _browser.Post(new { type = "close", id = Host.Id });

    /// <summary>Ask the host to end its child. Works whether or not the page is attached.</summary>
    public void Kill() => HostManager.Kill(Host);

    public void FocusTerminal() => _browser.FocusPage();

    internal void OnMessage(string type, JsonElement root)
    {
        switch (type)
        {
            case "title":
                string t = root.GetProperty("title").GetString() ?? "";
                if (t.Length > 0 && t != Title) { Title = t; TitleChanged?.Invoke(this); }
                break;
            case "exit":
                Exited = true;
                ExitedChanged?.Invoke(this);
                break;
        }
    }
}
