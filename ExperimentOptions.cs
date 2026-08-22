namespace ClaudeSessionMonitor;

/// <summary>
/// Narrow, opt-in switches used to separate the recurring DataGrid and Windows Terminal UIA paths.
/// Telemetry is independently enabled by a log path, allowing an instrumented launch with otherwise
/// ordinary production behavior. All switches are environment-only.
/// </summary>
internal static class ExperimentOptions
{
    internal const string DisableDesktopUiaVariable = "CSM_EXPERIMENT_DISABLE_DESKTOP_UIA";
    internal const string FreezeGridVariable = "CSM_EXPERIMENT_FREEZE_GRID";
    internal const string LogPathVariable = "CSM_EXPERIMENT_LOG";

    internal static bool DisableDesktopUia => Enabled(DisableDesktopUiaVariable);
    internal static bool FreezeGrid => Enabled(FreezeGridVariable);
    internal static string Mode => FreezeGrid ? "frozen-grid" :
        DisableDesktopUia ? "no-desktop-uia" :
        "normal";
    internal static bool IsolationActive => Mode != "normal";
    internal static string LogPath => Environment.GetEnvironmentVariable(LogPathVariable) ?? "";
    internal static bool TelemetryActive => LogPath.Length > 0;

    static bool Enabled(string name)
    {
        string value = Environment.GetEnvironmentVariable(name) ?? "";
        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
