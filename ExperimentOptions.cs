namespace SessionDeck;

/// <summary>
/// Narrow, opt-in switches used to take the recurring DataGrid work out of a run.
/// Telemetry is independently enabled by a log path, allowing an instrumented launch with otherwise
/// ordinary production behavior. All switches are environment-only.
/// </summary>
internal static class ExperimentOptions
{
    internal const string FreezeGridVariable = "SD_EXPERIMENT_FREEZE_GRID";
    internal const string LogPathVariable = "SD_EXPERIMENT_LOG";

    internal static bool FreezeGrid => Enabled(FreezeGridVariable);
    internal static string Mode => FreezeGrid ? "frozen-grid" : "normal";
    internal static bool IsolationActive => Mode != "normal";
    internal static string LogPath => Environment.GetEnvironmentVariable(LogPathVariable) ?? "";
    internal static bool TelemetryActive => LogPath.Length > 0;

    static bool Enabled(string name)
    {
        string value = Environment.GetEnvironmentVariable(name) ?? "";
        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
