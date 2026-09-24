using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SessionDeck;

/// <summary>
/// The one WebView2 behind the deck. Its page, the embedded <c>deck.html</c>, hosts an xterm.js
/// terminal per open tab and talks to each pty host over its own loopback WebSocket, so no terminal
/// bytes pass through here. Every terminal lives in the same document: switching tabs is a DOM
/// visibility toggle, which is what makes it flash-free. A second browser per tab would mean a second
/// child window, and WPF re-shows hosted child windows on every layout pass, which paints the
/// backdrop for a frame before the page repaints.
/// </summary>
internal sealed class DeckBrowser : Grid
{
    const string VirtualHost = "sessiondeck.local";
    static readonly Assembly Self = typeof(DeckBrowser).Assembly;
    static Task<CoreWebView2Environment>? _env;
    static Settings? _settings;
    public static void UseSettings(Settings s) => _settings = s;

    WebView2? _web;
    bool _ready;
    bool _dark;
    readonly Queue<object> _pending = new();
    readonly TextBlock _status = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.7,
        Text = "Starting terminal…",
    };

    /// <summary>A message from one terminal: host id, message type, and the whole message.</summary>
    public event Action<string, string, JsonElement>? Message;

    /// <summary>The page is (re)loaded and listening. Callers re-open their terminals here.</summary>
    public event Action? Ready;

    public DeckBrowser(bool dark)
    {
        _dark = dark;
        Children.Add(_status);
    }

    static Task<CoreWebView2Environment> Environment_()
    {
        return _env ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SessionDeck", "WebView2"),
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection" });
    }

    public async void Start()
    {
        if (_web != null) return;
        bool opaque = Environment.GetEnvironmentVariable("SD_OPAQUE_WEBVIEW") == "1";
        var web = new WebView2 { DefaultBackgroundColor = opaque ? System.Drawing.Color.FromArgb(12, 12, 12) : System.Drawing.Color.Transparent };
        _web = web;
        Children.Add(web);
        try
        {
            await web.EnsureCoreWebView2Async(await Environment_());
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.AddWebResourceRequestedFilter($"https://{VirtualHost}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += ServeEmbedded;
            core.WebMessageReceived += OnMessage;
            core.NavigationStarting += (_, _) => _ready = false;
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenOutside(e.Uri); };
            core.Navigate($"https://{VirtualHost}/deck.html?theme={(_dark ? "dark" : "light")}{LookQuery()}");
            _status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _status.Text = "WebView2 failed: " + ex.Message;
            App.LogError(ex);
        }
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
        e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {mime}; charset=utf-8\r\nCache-Control: no-store");
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";
            switch (type)
            {
                case "ready":
                    _ready = true;
                    while (_pending.Count > 0) Send(_pending.Dequeue());
                    Ready?.Invoke();
                    return;
                case "open":
                    OpenOutside(root.GetProperty("uri").GetString());
                    return;
                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch (Exception ex) { App.LogError(ex); }
                    return;
                case "paste":
                    string pasted = "";
                    try { if (Clipboard.ContainsText()) pasted = Clipboard.GetText(); } catch (Exception ex) { App.LogError(ex); }
                    Send(new { type = "paste", id = root.GetProperty("id").GetString(), text = pasted });
                    return;
            }
            if (root.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } hostId)
                Message?.Invoke(hostId, type, root);
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    static void OpenOutside(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Deliver to the page, or hold it until the page says it is ready.</summary>
    public void Post(object msg)
    {
        if (!_ready) { _pending.Enqueue(msg); return; }
        Send(msg);
    }

    void Send(object msg)
    {
        try { _web?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { }
    }

    public void FocusPage()
    {
        _web?.Focus();
        Post(new { type = "focus" });
    }

    public void Fit() => Post(new { type = "fit" });

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        Post(new { type = "theme", dark, look = Look() });
    }

    public void ApplyLook() => Post(new { type = "theme", dark = _dark, look = Look() });

    /// <summary>The terminal surface as a WPF brush: the scheme background at the configured opacity.</summary>
    public static System.Windows.Media.Brush SurfaceBrush(bool dark)
    {
        var s = _settings ?? new Settings();
        string hex = s.TerminalScheme switch
        {
            "One Half Dark" => "#282c34",
            "Deck" => "#1d1e21",
            "Deck Light" => "#ffffff",
            "Campbell" => "#0c0c0c",
            _ => dark ? "#1d1e21" : "#ffffff",
        };
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        c.A = (byte)Math.Round(Math.Clamp(s.TerminalOpacity, 0, 100) * 2.55);
        var b = new System.Windows.Media.SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static object Look()
    {
        var s = _settings ?? new Settings();
        return new { font = s.TerminalFont, size = s.TerminalFontSize, opacity = Math.Clamp(s.TerminalOpacity, 0, 100), scheme = s.TerminalScheme };
    }

    static string LookQuery()
    {
        var s = _settings ?? new Settings();
        return $"&font={Uri.EscapeDataString(s.TerminalFont)}&size={s.TerminalFontSize}&opacity={Math.Clamp(s.TerminalOpacity, 0, 100)}&scheme={Uri.EscapeDataString(s.TerminalScheme)}";
    }
}
