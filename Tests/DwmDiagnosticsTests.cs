using Xunit;

namespace SessionDeck.Tests;

public class DwmDiagnosticsTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    public void Diagnostic_switch_accepts_explicit_true_values(string value) =>
        Assert.True(DwmDiagnosticOptions.IsEnabled(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("anything-else")]
    public void Diagnostic_switch_defaults_off(string? value) =>
        Assert.False(DwmDiagnosticOptions.IsEnabled(value));

    [Fact]
    public void Healthy_short_spikes_do_not_trip_the_guard()
    {
        double[] core = [3, 8, 29, 4, 7, 18, 3, 5];
        double[] working = [152, 153, 157, 155, 154, 156, 155, 154];
        Assert.False(DwmDiagnostics.GuardWouldTripForTests(core, working, 152));
    }

    [Fact]
    public void Broken_sustained_one_core_signature_trips_the_guard()
    {
        double[] core = [88, 92, 97, 91, 95];
        double[] working = [175, 190, 220, 260, 290];
        Assert.True(DwmDiagnostics.GuardWouldTripForTests(core, working, 152));
    }

    [Fact]
    public void Broken_resident_growth_trips_even_before_cpu_window_fills()
    {
        double[] core = [12, 14, 15];
        double[] working = [340, 360, 370];
        Assert.True(DwmDiagnostics.GuardWouldTripForTests(core, working, 152));
    }
}
