using System.IO;
using System.Text;

namespace SessionDeck;

/// <summary>
/// State files written so a crash never leaves a half-written one, and read so a file that does not
/// parse never leads to a save over it. A write goes to <c>&lt;file&gt;.tmp</c>, is flushed to disk,
/// then swapped in; the file it replaces is kept as <c>&lt;file&gt;.bak</c>, the last good copy.
/// </summary>
internal static class AtomicFile
{
    static readonly object Gate = new();
    static readonly HashSet<string> Unreadable = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> SetAside = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: runs after the temp file is on disk and before the swap, with the target path.</summary>
    internal static Action<string>? BeforeSwap;

    /// <summary>
    /// Replace <paramref name="path"/> with <paramref name="text"/>. Refuses (returns false) for a file
    /// this process found unparseable and could not move aside: that file is all there is of it.
    /// </summary>
    public static bool Write(string path, string text)
    {
        path = Path.GetFullPath(path);
        lock (Gate)
        {
            if (Unreadable.Contains(path)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(Encoding.UTF8.GetBytes(text));
                fs.Flush(flushToDisk: true);
            }
            BeforeSwap?.Invoke(path);
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
            return true;
        }
    }

    /// <summary>
    /// Read and parse <paramref name="path"/>. A file that does not parse is moved aside as
    /// <c>&lt;file&gt;.corrupt-&lt;stamp&gt;</c> and the last good copy is returned instead. Null when
    /// there is no file, or neither it nor its last good copy parses.
    /// </summary>
    public static T? Read<T>(string path, Func<string, T?> parse) where T : class
    {
        path = Path.GetFullPath(path);
        lock (Gate)
        {
            if (!File.Exists(path))
                return SetAside.Contains(path) ? TryParse(path + ".bak", parse) : null;
            if (TryParse(path, parse) is { } value) return value;
            try
            {
                File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
                SetAside.Add(path);
            }
            catch (Exception ex)
            {
                Unreadable.Add(path);
                App.LogError(ex);
            }
            return TryParse(path + ".bak", parse);
        }
    }

    static T? TryParse<T>(string file, Func<string, T?> parse) where T : class
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (!File.Exists(file)) return null;
                string text;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();
                return parse(text);
            }
            catch (IOException) { Thread.Sleep(20); }
            catch { return null; }
        }
        return null;
    }
}
