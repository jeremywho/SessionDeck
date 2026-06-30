using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClaudeSessionMonitor;

/// <summary>
/// Context % -> themed brush by threshold (&gt;85 red, &gt;70 amber, else neutral) — matches the omc
/// status-line health thresholds (warning 70, critical 85). Resolves the brush
/// from the live application resources at convert time, so a theme/palette swap stays correct.
/// </summary>
internal sealed class PctToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int pct = value is int i ? i : 0;
        string key = pct > 85 ? "RedBrush" : pct > 70 ? "AmberBrush" : "BarBrush";
        return (Application.Current?.TryFindResource(key) as Brush) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
