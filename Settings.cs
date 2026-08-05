using System.IO;
using System.Text.Json;

namespace ClaudeSessionMonitor;

/// <summary>User preferences persisted to %APPDATA%\ClaudeSessionMonitor\settings.json.</summary>
internal sealed class Settings
{
    public string Theme { get; set; } = "Dark";                       // "Dark" | "Light"
    public bool AlwaysOnTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public Dictionary<string, bool> ColumnVisible { get; set; } = new();    // column key -> visible
    public Dictionary<string, int> ColumnOrder { get; set; } = new();       // column key -> display index
    public Dictionary<string, double> ColumnWidth { get; set; } = new();    // column key -> pixel width
    public int LayoutVersion { get; set; }                                  // bumped when the window layout changes (resets stale window size)
    public string ResumeFlags { get; set; } = "";                           // "Claude flags": appended to every launched/resumed claude (JSON key kept for compat)
    public string CodexFlags { get; set; } = "";                            // appended to every resumed codex — separate because the two CLIs share no flag spelling
    public string DockPosition { get; set; } = "Free";                      // Free | LeftEdge | RightEdge | TopLeft | TopRight | BottomLeft | BottomRight
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double Zoom { get; set; } = 1.0;                                 // content zoom (Ctrl+wheel), 0.6–2.5
    public bool ShowInTaskbar { get; set; } = true;                         // false = tray-only (no taskbar button)
    public bool RunOnLogin { get; set; } = true;                            // HKCU Run registration (installed instance only)

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSessionMonitor");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
