using System.IO;

namespace SessionDeck.Host;

/// <summary>Whether a serialized record differs from the last one written, remembering it when it does.</summary>
internal static class RecordGate
{
    public static bool Changed(string json, ref string? lastWritten)
    {
        if (string.Equals(json, lastWritten, StringComparison.Ordinal)) return false;
        lastWritten = json;
        return true;
    }

    /// <summary>
    /// Puts the record in place of the one on disk. The deck (and anything else) reads that file
    /// whenever it likes, and while a reader has it open the replace is refused; the reader is done
    /// in a moment, so it is tried again. False when it could not be written at all: the caller
    /// must then not count it as written, or the change is never seen.
    /// </summary>
    public static bool Write(string path, string json, int attempts = 8, int waitMs = 15)
    {
        string tmp = path + ".tmp";
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i + 1 < attempts) Thread.Sleep(waitMs);
            }
        }
        return false;
    }
}
