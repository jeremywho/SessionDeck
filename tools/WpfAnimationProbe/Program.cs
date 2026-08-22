using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WpfAnimationProbe;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "low";
        if (mode is not ("static" or "low" or "uncapped")) mode = "low";

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new ProbeWindow(mode));
    }
}

internal sealed class ProbeWindow : Window
{
    readonly string _mode;

    internal ProbeWindow(string mode)
    {
        _mode = mode;
        Title = $"WPF Animation Probe — {mode}";
        Width = 637;
        Height = 1392;
        MinWidth = 460;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Background = new SolidColorBrush(Color.FromRgb(17, 19, 24));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        Content = BuildContent();

        Loaded += (_, _) =>
        {
            int tier = RenderCapability.Tier >> 16;
            string renderMode = (PresentationSource.FromVisual(this) as HwndSource)?.CompositionTarget.RenderMode.ToString() ?? "unknown";
            Title = $"WPF Animation Probe — {_mode} — tier {tier}, {renderMode}";
        };
    }

    UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(18) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(new TextBlock
        {
            Text = "Minimal WPF animation control",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        heading.Children.Add(new TextBlock
        {
            Text = $"Mode: {_mode}. No WPF-UI, Mica, effects, DataGrid, bindings, scanners, tray icon, or background threads.",
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(170, 177, 190)),
            TextWrapping = TextWrapping.Wrap,
        });
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var rows = new StackPanel();
        for (int i = 0; i < 15; i++) rows.Children.Add(BuildRow(i));
        root.Children.Add(rows);
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

        UIElement glyph = index switch
        {
            0 => BuildSpinner(),
            1 => BuildPulse(),
            _ => BuildCompleted(),
        };
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = index < 2 ? $"Active session {index + 1}" : $"Completed session {index + 1}",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14.5,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var context = new TextBlock
        {
            Text = $"{(index * 7 + 12) % 100}%",
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

        if (_mode != "static")
        {
            var animation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            if (_mode == "low") Timeline.SetDesiredFrameRate(animation, 10);
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
        if (_mode != "static")
        {
            var animation = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            if (_mode == "low") Timeline.SetDesiredFrameRate(animation, 10);
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
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return grid;
    }
}
