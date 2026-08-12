using System.IO;

namespace ClaudeSessionMonitor;

/// <summary>Low-volume diagnostics for slow recurring work. Kept separate from the error log.</summary>
internal static class PerformanceLog
{
    internal static readonly string FilePath = Path.Combine(Path.GetTempPath(), "claude-session-monitor-performance.log");
    const long MaxBytes = 1024 * 1024;
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaxBytes)
                    File.WriteAllText(FilePath, "");
                File.AppendAllText(FilePath, $"[{DateTime.Now:o}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
