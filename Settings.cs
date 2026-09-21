using System.IO;
using System.Text.Json;

namespace SessionDeck;

/// <summary>User preferences persisted to %APPDATA%\SessionDeck\settings.json.</summary>
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
    public string CodexFlags { get; set; } = DefaultCodexFlags;             // appended to every launched/resumed codex — separate because the two CLIs share no flag spelling

    /// <summary>
    /// What a Codex session needs to be usable without babysitting it — Codex's equivalent of the
    /// <c>--dangerously-skip-permissions</c> people put in the Claude box. Applied once (see
    /// <see cref="FlagsVersion"/>); clearing the box afterwards sticks.
    /// </summary>
    public const string DefaultCodexFlags = "--dangerously-bypass-approvals-and-sandbox";

    /// <summary>Bumped when a flags default is introduced, so it's filled in exactly once on files
    /// written before it existed — and never re-added if you then clear the box on purpose.</summary>
    public int FlagsVersion { get; set; }
    public string DockPosition { get; set; } = "Free";                      // Free | LeftEdge | RightEdge | TopLeft | TopRight | BottomLeft | BottomRight
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double Zoom { get; set; } = 1.0;                                 // content zoom (Ctrl+wheel), 0.6–2.5
    public bool ShowInTaskbar { get; set; } = true;                         // false = tray-only (no taskbar button)
    public bool RunOnLogin { get; set; } = true;                            // HKCU Run registration (installed instance only)

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionDeck");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        Settings s;
        try
        {
            s = (File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath))
                    : null) ?? new Settings();
        }
        catch { s = new Settings(); }

        if (s.ApplyNewDefaults()) s.Save();
        return s;
    }

    /// <summary>
    /// Fill in settings introduced after this file was written. Returns whether anything changed.
    /// <para>Guarded by <see cref="FlagsVersion"/> rather than by "is it empty": a file written before
    /// the Codex box existed has it empty, and so does a file where you deliberately cleared it. Only
    /// the version tells those apart, and re-adding a flag someone removed would be obnoxious.</para>
    /// </summary>
    internal bool ApplyNewDefaults()
    {
        if (FlagsVersion >= 1) return false;
        FlagsVersion = 1;
        if (!string.IsNullOrWhiteSpace(CodexFlags)) return true;
        CodexFlags = DefaultCodexFlags;
        return true;
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
