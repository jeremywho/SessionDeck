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
}
