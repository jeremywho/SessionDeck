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
    public string ResumeFlags { get; set; } = DefaultClaudeFlags;           // "Claude flags": appended to every launched/resumed claude (JSON key kept for compat)
    public string CodexFlags { get; set; } = DefaultCodexFlags;             // appended to every launched/resumed codex — separate because the two CLIs share no flag spelling

    /// <summary>
    /// What a Codex session needs to be usable without babysitting it — Codex's equivalent of the
    /// <c>--dangerously-skip-permissions</c> people put in the Claude box. Applied once (see
    /// <see cref="FlagsVersion"/>); clearing the box afterwards sticks.
    /// </summary>
    public const string DefaultCodexFlags = "--dangerously-bypass-approvals-and-sandbox";
    public const string DefaultClaudeFlags = "--dangerously-skip-permissions";

    /// <summary>Bumped when a flags default is introduced, so it's filled in exactly once on files
    /// written before it existed — and never re-added if you then clear the box on purpose.</summary>
    public int FlagsVersion { get; set; }
    public string DockPosition { get; set; } = "Free";                      // Free | LeftEdge | RightEdge | TopLeft | TopRight | BottomLeft | BottomRight
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double Zoom { get; set; } = 1.0;                                 // content zoom (Ctrl+wheel), 0.6–2.5
    public bool ShowInTaskbar { get; set; } = true;                         // false = tray-only (no taskbar button)
    public string TerminalFont { get; set; } = "CaskaydiaCove NF";          // xterm font family; falls back down the stack in terminal.html
    public double TerminalFontSize { get; set; } = 12;                     // points, like Windows Terminal's font.size
    public int TerminalOpacity { get; set; } = 98;                          // 0-100, like Windows Terminal's `opacity`
    public string WindowBackdrop { get; set; } = "Acrylic";                 // Acrylic (blurred see-through, like Windows Terminal with acrylic) | Mica
    public string TerminalScheme { get; set; } = "Campbell";                // Campbell | One Half Dark | Deck
    public List<string> RecentFolders { get; set; } = new();                // most recent first, capped
    public string LastClaudeModel { get; set; } = "";
    public string LastClaudeEffort { get; set; } = "";
    public string LastCodexModel { get; set; } = "";
    public string LastCodexEffort { get; set; } = "";
    public bool AutoRestartOnUpdate { get; set; } = true;
    public List<SessionGroup> SessionGroups { get; set; } = new();          // named, collapsible sets of sessions in the list; members are session ids                    // restart idle deck sessions when their CLI has updated underneath them

    public void RememberFolder(string cwd)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, cwd, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, cwd);
        if (RecentFolders.Count > 12) RecentFolders.RemoveRange(12, RecentFolders.Count - 12);
    }
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
        if (FlagsVersion >= 2) return false;
        if (FlagsVersion < 1 && string.IsNullOrWhiteSpace(CodexFlags)) CodexFlags = DefaultCodexFlags;
        if (FlagsVersion < 2 && string.IsNullOrWhiteSpace(ResumeFlags)) ResumeFlags = DefaultClaudeFlags;
        FlagsVersion = 2;
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
