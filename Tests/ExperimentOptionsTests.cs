using Xunit;

namespace SessionDeck.Tests;

public class ExperimentOptionsTests
{
    [Fact]
    public void A_log_path_enables_telemetry_without_changing_normal_app_behavior()
    {
        string[] names =
        {
            ExperimentOptions.DisableDesktopUiaVariable,
            ExperimentOptions.FreezeGridVariable,
            ExperimentOptions.LogPathVariable,
        };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(ExperimentOptions.DisableDesktopUiaVariable, null);
            Environment.SetEnvironmentVariable(ExperimentOptions.FreezeGridVariable, null);
            Environment.SetEnvironmentVariable(ExperimentOptions.LogPathVariable, @"C:\Temp\monitor.tsv");

            Assert.Equal("normal", ExperimentOptions.Mode);
            Assert.False(ExperimentOptions.IsolationActive);
            Assert.True(ExperimentOptions.TelemetryActive);
        }
        finally
        {
            foreach (var item in previous)
                Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }
}
