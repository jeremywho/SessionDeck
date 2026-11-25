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
    readonly PageWatch _watch = new();
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

    /// <summary>A message about the page as a whole (column drops, divider moves, the PR column), not one terminal.</summary>
    public event Action<string, JsonElement>? PageMessage;

    public DeckBrowser(bool dark)
    {
        _dark = dark;
        Children.Add(_status);
    }

    static Task<CoreWebView2Environment> Environment_()
    {
        string profile = Environment.GetEnvironmentVariable("SD_DATA_DIR") is { Length: > 0 } d
            ? Path.Combine(d, "WebView2")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SessionDeck", "WebView2");
        return _env ??= CoreWebView2Environment.CreateAsync(null, profile,
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BrowserArguments((_settings ?? new Settings()).PageAccessibility) });
    }

    /// <summary>
    /// What the browser is started with. Unless asked for, it is told to refuse an accessibility tree: any program
    /// that reads the window through UI Automation (a screen reader, but also a script looking for a button) turns
    /// one on for as long as the deck runs, and with dozens of chats in the page a tab switch then takes most of a
    /// second and typing stutters (AGENTS.md).
    /// </summary>
    internal static string BrowserArguments(bool pageAccessibility) =>
        "--disable-features=msSmartScreenProtection" + (pageAccessibility ? "" : " --disable-renderer-accessibility");

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
            core.ProcessFailed += OnProcessFailed;
            _silence.Tick += (_, _) => CheckSilence();
            _silence.Start();
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
                case "metrics":
                    PerformanceLog.Write("term-metrics " + root.GetRawText());
                    return;
                case "stall":
                case "switched":
                case "switch-parts":
                case "took":
                case "frame":
                case "key":
                case "keys":
                case "pace":
                case "visibility":
                    if (_watch.Line(root, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) is { } watched) PerformanceLog.Write(watched);
                    return;
                case "disconnected":
                case "connected":
                    if (_watch.Line(root, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) is { } socket) PerformanceLog.Write(socket);
                    break;
                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch (Exception ex) { App.LogError(ex); }
                    return;
                case "paste":
                    string pasted = "";
                    try { if (Clipboard.ContainsText()) pasted = Clipboard.GetText(); } catch (Exception ex) { App.LogError(ex); }
                    Send(new { type = "paste", id = root.GetProperty("id").GetString(), text = pasted });
                    return;
            }
            Dispatch(root, Message, PageMessage);
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    internal static void Dispatch(JsonElement root, Action<string, string, JsonElement>? host, Action<string, JsonElement>? page)
    {
        string type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } hostId)
            host?.Invoke(hostId, type, root);
        else
            page?.Invoke(type, root);
    }

    static void OpenOutside(string? uri)
    {
        var (kind, target) = OutsideLink.Decide(uri, File.Exists, Directory.Exists);
        try
        {
            switch (kind)
            {
                case OutsideLink.Kind.Web:
                case OutsideLink.Kind.Open:
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    break;
                case OutsideLink.Kind.Show:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = false });
                    break;
                case OutsideLink.Kind.Folder:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false });
                    break;
            }
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    /// <summary>Deliver to the page, or hold it until the page says it is ready.</summary>
    public void Post(object msg)
    {
        if (!_ready) { _pending.Enqueue(msg); return; }
        Send(msg);
    }

    string Send(object msg)
    {
        try { _web?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); return ""; }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>The stamp for a layout about to be posted; the page hands it back when it has taken the layout, and again when it has drawn it.</summary>
    public long StampLayout() => NowMs;

    /// <summary>Post a layout stamped with <see cref="StampLayout"/>, and start waiting for the page to answer it.</summary>
    public void PostLayout(long at, object msg)
    {
        if (!_ready) { _pending.Enqueue(msg); _watch.Posted(at, "held-until-ready"); return; }
        _watch.Posted(at, Send(msg));
    }

    /// <summary>How long the app's own handler ran around the layout stamped <paramref name="at"/>, for the line about that switch.</summary>
    public void AppWorked(long at, long ms) => _watch.AppWorked(at, ms);

    // ---------------- a page that has stopped answering ----------------

    readonly System.Windows.Threading.DispatcherTimer _silence = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<long> _reloads = new();

    /// <summary>
    /// A page that is hung or has stopped drawing reports nothing itself. When the newest layout has gone
    /// unanswered, say so, with whether each of the browser's processes is alive and how busy it is over a second:
    /// a renderer at a full core is spinning, an idle one is waiting on something, a missing one is gone.
    /// </summary>
    void CheckSilence()
    {
        if (_watch.Silent(NowMs, _ready) is not { } line) return;
        var watched = new List<(string Kind, int Pid)>();
        try
        {
            if (_web?.CoreWebView2?.Environment.GetProcessInfos() is { } infos)
                foreach (var p in infos) watched.Add((p.Kind.ToString(), p.ProcessId));
        }
        catch (Exception ex) { line += " processes=" + ex.GetType().Name; }
        Task.Run(() =>
        {
            try
            {
                var before = watched.Select(w => { try { using var p = Process.GetProcessById(w.Pid); return (double?)p.TotalProcessorTime.TotalMilliseconds; } catch { return null; } }).ToList();
                Thread.Sleep(1000);
                var parts = new List<string>();
                for (int i = 0; i < watched.Count; i++)
                {
                    double? cpu = null; long ws = 0;
                    try { using var p = Process.GetProcessById(watched[i].Pid); if (before[i] is { } was) cpu = (p.TotalProcessorTime.TotalMilliseconds - was) / 10.0; ws = p.WorkingSet64 / (1024 * 1024); } catch { }
                    parts.Add(PageWatch.ProcessState(watched[i].Kind, watched[i].Pid, cpu, ws));
                }
                PerformanceLog.Write(line + (parts.Count > 0 ? " " + string.Join(" ", parts) : ""));
            }
            catch (Exception ex) { App.LogError(ex); }
        });
    }

    /// <summary>
    /// One of the browser's processes failed. It is always logged; nothing else was watching for it. A renderer that
    /// exited leaves a dead page, which is loaded again (the page asks for every pane back once it is ready), at
    /// most three times in five minutes so a renderer that keeps dying is not restarted for ever.
    /// </summary>
    void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        bool reload = false;
        try
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                long now = NowMs;
                _reloads.RemoveAll(t => now - t > 300_000);
                if (_reloads.Count < 3) { _reloads.Add(now); reload = true; }
            }
            string description = "";
            try { description = e.ProcessDescription ?? ""; } catch { }
            PerformanceLog.Write(PageWatch.Failed(e.ProcessFailedKind.ToString(), e.Reason.ToString(), e.ExitCode, description, reload));
            if (reload) (sender as CoreWebView2 ?? _web?.CoreWebView2)?.Reload();
        }
        catch (Exception ex) { App.LogError(ex); }
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
