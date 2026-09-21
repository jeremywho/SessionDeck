using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SessionDeck;

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

/// <summary>
/// Server-reported usage severity -> themed brush. The server owns the thresholds, so unlike
/// <see cref="PctToBrushConverter"/> this maps a label rather than picking a cutoff; an unknown
/// severity falls back to the neutral color.
/// <para>ConverterParameter "text" brightens the "normal" case: the neutral bar color is dim by
/// design as a fill, but as a numeral it ends up dimmer than the label beside it — which reads
/// backwards, since the number is the point.</para>
/// </summary>
internal sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string neutral = (parameter as string) == "text" ? "FgBrush" : "BarBrush";
        string key = (value as string) switch
        {
            "critical" => "RedBrush",
            "warning" => "AmberBrush",
            _ => neutral,
        };
        return (Application.Current?.TryFindResource(key) as Brush) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Percent -> a star <see cref="GridLength"/>, so a two-column Grid draws a proportional fill
/// without anyone measuring a pixel. ConverterParameter "rest" yields the complement.
/// </summary>
internal sealed class PctToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double pct = value is int i ? Math.Clamp(i, 0, 100) : 0;
        if ((parameter as string) == "rest") pct = 100 - pct;
        return new GridLength(pct, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Provider -> its mark color, resolved from the live palette at convert time (like
/// <see cref="PctToBrushConverter"/>). The hues live in the theme dictionaries rather than here
/// because each theme needs its own tuning — the values that read on the dark pill wash out against
/// the light theme's near-white track — and because static launcher glyphs reference the same brushes
/// via DynamicResource, which re-themes them without going through this converter at all.
/// </summary>
internal sealed class ProviderColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is SessionProvider.Codex ? "CodexMarkBrush" : "ClaudeMarkBrush";
        return (Application.Current?.TryFindResource(key) as Brush) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Virtual-desktop index -> a subtle dot color (non-status hues). -1 => transparent (no dot).</summary>
internal sealed class DesktopColorConverter : IValueConverter
{
    static readonly Brush[] Palette =
    {
        Frozen(0x33, 0xC4, 0xC4),  // teal
        Frozen(0xA7, 0x7C, 0xE8),  // purple
        Frozen(0xE8, 0x7C, 0xB0),  // pink
        Frozen(0xD8, 0x6A, 0xD8),  // magenta
    };

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();   // free thread-safety + no per-instance change tracking
        return brush;
    }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int i = value is int n ? n : -1;
        return i < 0 ? Brushes.Transparent : Palette[i % Palette.Length];
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
