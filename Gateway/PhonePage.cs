using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionDeck.Gateway;

/// <summary>What the gateway answers a phone that asked for the page, or for something the page fetches.</summary>
internal sealed record PhonePageAnswer(int Status, string ContentType, byte[] Body);

/// <summary>
/// One web page on the deck's own machine that a paired phone may read through the gateway: a
/// dashboard that listens on loopback, shown on the phone wherever the phone is. It is named in
/// <c>phone-page.json</c> beside the settings:
/// <code>{ "name": "Usage", "url": "http://127.0.0.1:9000/", "paths": ["/api/usage"] }</code>
/// <para>It is a narrow way through, not a proxy. The phone never says where to go: it asks for the
/// page or for one of the listed paths, by GET, and the gateway fetches that from the configured
/// place and nothing else. Whatever the page can change (it may have buttons that POST) cannot be
/// reached from the phone. Only loopback is allowed, redirects are not followed, and an answer over
/// <see cref="MostBytes"/> is refused.</para>
/// </summary>
internal sealed class PhonePage
{
    public const string FileName = "phone-page.json";
    public const int MostBytes = 2 * 1024 * 1024;

    static readonly Regex SafePath = new(@"^(/[A-Za-z0-9._~-]+)+/?$", RegexOptions.Compiled);
    static readonly string[] Shown = { "text/html", "application/json", "text/css", "text/javascript", "application/javascript", "text/plain", "image/svg+xml", "image/png" };

    public string Name { get; }
    /// <summary>The page itself.</summary>
    public Uri Page { get; }
    /// <summary>What the page fetches from its own server, each an absolute path.</summary>
    public IReadOnlyList<string> Paths { get; }

    PhonePage(string name, Uri page, IReadOnlyList<string> paths) => (Name, Page, Paths) = (name, page, paths);

    /// <summary>The page the file names; null when there is no file or it does not name one that may be shown.</summary>
    public static PhonePage? Load(string file)
    {
        try { return File.Exists(file) ? Parse(File.ReadAllText(file)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static PhonePage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            string url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
            if (name.Length is 0 or > 40 || name.Any(char.IsControl)) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme != "http" || !page.IsLoopback || page.UserInfo.Length > 0 || page.Query.Length > 0 || page.Fragment.Length > 0) return null;
            var paths = new List<string>();
            if (root.TryGetProperty("paths", out var listed))
            {
                if (listed.ValueKind != JsonValueKind.Array) return null;
                foreach (var entry in listed.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } path || !SafePath.IsMatch(path) || path.Contains("..")) return null;
                    paths.Add(path);
                }
            }
            return new PhonePage(name, page, paths);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Where a request path under <c>/page</c> leads: the page for <c>/page</c> and <c>/page/</c>, a
    /// listed path for <c>/page/that/path</c>, and null for anything else.
    /// </summary>
    public Uri? Target(string requestPath)
    {
        if (requestPath is "/page" or "/page/") return Page;
        if (!requestPath.StartsWith("/page/", StringComparison.Ordinal)) return null;
        string path = requestPath["/page".Length..];
        return Paths.Contains(path, StringComparer.Ordinal) ? new Uri(Page, path) : null;
    }

    public async Task<PhonePageAnswer> Read(string requestPath, HttpClient http, CancellationToken cancel = default)
    {
        if (Target(requestPath) is not { } target) return Said(404, "Not part of the page.");
        try
        {
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode != HttpStatusCode.OK) return Said(502, $"{Name} answered {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MostBytes) return Said(502, $"{Name} sent more than can be shown.");
            await using var body = await response.Content.ReadAsStreamAsync(cancel);
            var kept = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, cancel)) > 0)
            {
                if (kept.Length + read > MostBytes) return Said(502, $"{Name} sent more than can be shown.");
                kept.Write(buffer, 0, read);
            }
            string media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (!Shown.Contains(media)) return Said(502, $"{Name} sent something that is not a page.");
            bool text = media.StartsWith("text/", StringComparison.Ordinal) || media.EndsWith("json", StringComparison.Ordinal) || media.EndsWith("javascript", StringComparison.Ordinal);
            return new PhonePageAnswer(200, text ? media + "; charset=utf-8" : media, kept.ToArray());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return Said(502, $"{Name} is not answering on the deck's computer.");
        }
    }

    static PhonePageAnswer Said(int status, string message) => new(status, "text/plain; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(message));

    /// <summary>How the page is fetched: straight from this machine, no proxy, no redirect followed, and not for long.</summary>
    public static HttpClient Fetcher() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(12) };
}
