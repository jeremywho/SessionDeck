using System.IO;

namespace SessionDeck;

/// <summary>
/// What clicking a link in a pane does. The link was written by an agent, so it is not trusted: a web
/// address goes to the browser, and a file on this machine is opened only when it is a kind that is
/// looked at (a picture, a video, a page, a document). Anything else is shown in its folder, never run.
/// </summary>
internal static class OutsideLink
{
    internal enum Kind { None, Web, Open, Show, Folder }

    /// <summary>Kinds of file whose default program shows them. A program, a script, a shortcut or an installer is not among them.</summary>
    static readonly HashSet<string> Viewed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".mp4", ".webm", ".mov", ".mkv", ".mp3", ".wav",
        ".pdf", ".txt", ".md", ".log", ".csv", ".json", ".xml", ".yaml", ".yml", ".html", ".htm",
        // Source that Windows hands to an editor. Not here, because Windows can hand them to something that runs them:
        // .js and .vbs (Script Host), .py, .ps1, .sh, .bat, .cmd.
        ".cs", ".ts", ".tsx", ".jsx", ".css", ".scss", ".c", ".h", ".cpp", ".hpp", ".rs", ".go", ".java", ".kt", ".swift",
        ".sql", ".toml", ".xaml", ".razor", ".cshtml",
    };

    internal static (Kind Kind, string Target) Decide(string? link, Func<string, bool> fileExists, Func<string, bool> folderExists)
    {
        string uri = (link ?? "").Trim();
        if (uri.Length == 0) return (Kind.None, "");
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return (Kind.Web, uri);
        if (LocalPath(uri, fileExists) is not { } path) return (Kind.None, "");
        if (folderExists(path)) return (Kind.Folder, path);
        if (fileExists(path)) return (Viewed.Contains(Path.GetExtension(path)) ? Kind.Open : Kind.Show, path);
        // The file is not there (not written yet, moved, mistyped): the folder it was to be in says more than nothing happening.
        return Path.GetDirectoryName(path) is { Length: > 0 } parent && folderExists(parent) ? (Kind.Folder, parent) : (Kind.None, "");
    }

    /// <summary>
    /// The file a link names on this machine's own drives, or null. A share on another machine is not
    /// followed: asking Windows for one hands that machine this account's credentials.
    /// </summary>
    static string? LocalPath(string uri, Func<string, bool> fileExists)
    {
        string path;
        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
            path = parsed.LocalPath;
        }
        else path = uri.Replace('/', '\\');
        // Decided from the text alone, before the file system is asked anything: asking whether a file on a share exists is already the request.
        if (!OnADrive(path)) return null;
        // An agent may point at a line (`file.cs#L10`): the file is what can be opened.
        int mark = path.IndexOf('#');
        if (mark > 0 && !fileExists(path)) path = path[..mark];
        try { path = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        // A device name inside a folder comes back as \\.\nul.
        return OnADrive(path) ? path : null;
    }

    /// <summary>A full path on a lettered drive, naming no stream of a file (<c>C:\a.exe:b.png</c> reads as a picture and is not one).</summary>
    static bool OnADrive(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && path.IndexOf(':', 2) < 0;
}
