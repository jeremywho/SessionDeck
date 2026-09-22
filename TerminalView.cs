using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SessionDeck.Host;

namespace SessionDeck;

/// <summary>
/// One xterm.js terminal attached to one pty host. A WebView2 whose only page is the embedded
/// <c>terminal.html</c>; the page talks to the host over a loopback WebSocket directly, so this
/// class carries no bytes — it only relays title, exit and link events back to WPF.
/// <para>A view that is not showing can be <see cref="Suspend"/>ed: the WebView2 (and its renderer
/// process) is dropped, and <see cref="Resume"/> recreates it. The host keeps the scrollback, so the
/// page replays from the ring on reattach and nothing is lost.</para>
/// </summary>
internal sealed class TerminalView : Grid
{
    const string VirtualHost = "sessiondeck.local";
    static readonly Assembly Self = typeof(TerminalView).Assembly;
    static Task<CoreWebView2Environment>? _env;

    WebView2? _web;
    readonly TextBlock _status = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.7,
        Text = "Attaching…",
    };
    bool _dark;

    public HostRecord Host { get; }
    public string Title { get; private set; }
    public bool Exited { get; private set; }
    public bool IsSuspended => _web == null;

    public event Action<TerminalView>? TitleChanged;
    public event Action<TerminalView>? ExitedChanged;

    public TerminalView(HostRecord host, bool dark)
    {
        Host = host;
        Title = string.IsNullOrWhiteSpace(host.Title) ? DefaultTitle(host) : host.Title;
        _dark = dark;
        Exited = host.HasExited;
        Children.Add(_status);
    }

    static string DefaultTitle(HostRecord h) =>
        h.Provider + " · " + (string.IsNullOrEmpty(h.Cwd) ? "" : Path.GetFileName(h.Cwd.TrimEnd('\\', '/')));

    static Task<CoreWebView2Environment> Environment_()
    {
        return _env ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SessionDeck", "WebView2"),
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection" });
    }

    /// <summary>Create the WebView2 if it is not there. Safe to call repeatedly.</summary>
    public async void Resume()
    {
        if (_web != null) return;
        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent, Visibility = Visibility.Hidden };
        _web = web;
        Children.Add(web);
        _status.Visibility = Visibility.Visible;
        _status.Text = "Attaching…";
        try
        {
            try { await web.EnsureCoreWebView2Async(await Environment_()); }
            catch when (_web != web) { return; }
            if (_web != web) return;
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.AddWebResourceRequestedFilter($"https://{VirtualHost}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += ServeEmbedded;
            core.WebMessageReceived += OnMessage;
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenOutside(e.Uri); };
            core.Navigate($"https://{VirtualHost}/terminal.html?port={Host.Port}&token={Host.Token}&theme={(_dark ? "dark" : "light")}");
            web.Visibility = Visibility.Visible;
            _status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _status.Text = "WebView2 failed: " + ex.Message;
            App.LogError(ex);
        }
    }

    /// <summary>Drop the WebView2. The host is untouched; <see cref="Resume"/> reattaches from the ring.</summary>
    public void Suspend()
    {
        var web = _web;
        if (web == null) return;
        _web = null;
        Children.Remove(web);
        try { web.Dispose(); } catch { }
        _status.Text = "Attaching…";
        _status.Visibility = Visibility.Visible;
    }

    void ServeEmbedded(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;
        string path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        var stream = Self.GetManifestResourceStream("www/" + path);
        if (stream == null)
        {
            e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        string mime = Path.GetExtension(path) switch
        {
            ".html" => "text/html",
            ".js" => "application/javascript",
            ".css" => "text/css",
            _ => "application/octet-stream",
        };
        e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {mime}; charset=utf-8");
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "title":
                    string t = root.GetProperty("title").GetString() ?? "";
                    if (t.Length > 0 && t != Title) { Title = t; TitleChanged?.Invoke(this); }
                    break;
                case "exit":
                    Exited = true;
                    ExitedChanged?.Invoke(this);
                    break;
                case "open":
                    OpenOutside(root.GetProperty("uri").GetString());
                    break;
            }
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    static void OpenOutside(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    void Post(object msg)
    {
        try { _web?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { }
    }

    public void FocusTerminal()
    {
        _web?.Focus();
        Post(new { type = "focus" });
    }

    public void Fit() => Post(new { type = "fit" });

    /// <summary>Ask the host to end its child. Works whether or not a WebView2 is attached.</summary>
    public void Kill() => HostManager.Kill(Host);

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        Post(new { type = "theme", dark });
    }

    public void Shutdown() => Suspend();
}
