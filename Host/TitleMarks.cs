using System.Buffers;
using System.Text;

namespace SessionDeck.Host;

/// <summary>
/// Claude leads its terminal title with a mark that doubles as a spinner while it works (✳ ◐ ◑ ◒ …),
/// Codex with ◆. The mark is not part of the name: the name is what is left after it.
/// </summary>
internal static class TitleMarks
{
    /// <summary>The title without a leading mark and the space after it; a bare exe path becomes its file name.</summary>
    public static string Strip(string title)
    {
        string t = title.Trim();
        if (Rune.DecodeFromUtf16(t, out var first, out int width) == OperationStatus.Done
            && !Rune.IsLetterOrDigit(first) && t.Length > width && t[width] == ' ')
            return t[(width + 1)..].TrimStart();
        if (t.Contains('\\') && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.GetFileNameWithoutExtension(t);
        return t;
    }

    /// <summary>Whether two titles name the same thing, a spinner frame apart.</summary>
    public static bool SameName(string a, string b) => string.Equals(Strip(a), Strip(b), StringComparison.Ordinal);
}
