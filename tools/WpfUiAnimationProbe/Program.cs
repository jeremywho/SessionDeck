using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Path = System.Windows.Shapes.Path;
using TextBlock = System.Windows.Controls.TextBlock;

namespace WpfUiAnimationProbe;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "none-low";
        string[] valid = ["none-static", "none-low", "mica-static", "mica-low"];
        if (!valid.Contains(mode)) mode = "none-low";

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        app.Run(new ProbeWindow(mode));
    }
}

internal sealed class ProbeWindow : FluentWindow
{
    readonly string _mode;
    bool Animated => _mode.EndsWith("-low", StringComparison.Ordinal);

    internal ProbeWindow(string mode)
    {
        _mode = mode;
        Title = $"WPF-UI Probe — {mode}";
        Width = 637;
        Height = 1392;
        MinWidth = 460;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = mode.StartsWith("mica-", StringComparison.Ordinal)
            ? WindowBackdropType.Mica
            : WindowBackdropType.None;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        Content = BuildContent();

        Loaded += (_, _) =>
        {
            int tier = RenderCapability.Tier >> 16;
            string renderMode = (PresentationSource.FromVisual(this) as HwndSource)?.CompositionTarget.RenderMode.ToString() ?? "unknown";
            Title = $"WPF-UI Probe — {_mode} — tier {tier}, {renderMode}";
        };
    }

    UIElement BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(new TitleBar { Title = "WPF-UI composition control", Height = 36 });

        var surface = new Border { Background = new SolidColorBrush(Color.FromRgb(29, 30, 33)) };
        Grid.SetRow(surface, 1);
        root.Children.Add(surface);

        var rows = new StackPanel { Margin = new Thickness(18, 8, 18, 0) };
        surface.Child = rows;
        rows.Children.Add(new TextBlock
        {
            Text = $"Mode: {_mode}. FluentWindow + TitleBar only; no DataGrid, effects, bindings, scanners, tray, or background threads.",
            Foreground = new SolidColorBrush(Color.FromRgb(170, 177, 190)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        for (int i = 0; i < 15; i++) rows.Children.Add(BuildRow(i));
        return root;
    }

    UIElement BuildRow(int index)
    {
        var border = new Border
        {
            Height = 62,
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 44, 49)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        UIElement glyph = index switch { 0 => BuildSpinner(), 1 => BuildPulse(), _ => BuildCompleted() };
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(glyph);
        var name = new TextBlock
        {
            Text = index < 2 ? $"Active session {index + 1}" : $"Completed session {index + 1}",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14.5,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var context = new TextBlock
        {
            Text = $"{(index * 7 + 12) % 100}%",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };
        Grid.SetColumn(context, 2);
        grid.Children.Add(context);
        border.Child = grid;
        return border;
    }

    UIElement BuildSpinner()
    {
        var rotate = new RotateTransform();
        var spinner = new Grid
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = rotate,
            CacheMode = new BitmapCache(),
        };
        spinner.Children.Add(new Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(72, 145, 255)),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 9,2 A 7,7 0 1 1 2.9,5.4"),
        });
        if (Animated)
        {
            var animation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(animation, 10);
            rotate.BeginAnimation(RotateTransform.AngleProperty, animation);
        }
        return spinner;
    }

    UIElement BuildPulse()
    {
        var dot = new Ellipse
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stroke = new SolidColorBrush(Color.FromRgb(235, 170, 55)),
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            CacheMode = new BitmapCache(),
        };
        if (Animated)
        {
            var animation = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(animation, 10);
            dot.BeginAnimation(OpacityProperty, animation);
        }
        return dot;
    }

    static UIElement BuildCompleted()
    {
        var grid = new Grid
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(55, 180, 105)) });
        grid.Children.Add(new TextBlock
        {
            Text = "✓",
            Foreground = Brushes.White,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return grid;
    }
}
