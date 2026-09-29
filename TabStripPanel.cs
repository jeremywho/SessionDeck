using System.Windows;
using System.Windows.Controls;

namespace SessionDeck;

/// <summary>
/// A column's tab strip: the tab scroller (first child) followed by the + button (second child). The +
/// sits right after the last tab while the tabs fit, and stays at the right edge once they scroll.
/// </summary>
internal sealed class TabStripPanel : Panel
{
    internal static double TabsWidth(double available, double tabsWanted, double plusWidth) =>
        Math.Max(0, Math.Min(tabsWanted, available - plusWidth));

    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count < 2) return new Size();
        UIElement tabs = InternalChildren[0], plus = InternalChildren[1];
        plus.Measure(available);
        double room = double.IsInfinity(available.Width) ? available.Width : Math.Max(0, available.Width - plus.DesiredSize.Width);
        tabs.Measure(new Size(room, available.Height));
        return new Size(tabs.DesiredSize.Width + plus.DesiredSize.Width, Math.Max(tabs.DesiredSize.Height, plus.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (InternalChildren.Count < 2) return final;
        UIElement tabs = InternalChildren[0], plus = InternalChildren[1];
        double width = TabsWidth(final.Width, tabs.DesiredSize.Width, plus.DesiredSize.Width);
        tabs.Arrange(new Rect(0, 0, width, final.Height));
        plus.Arrange(new Rect(width, 0, plus.DesiredSize.Width, final.Height));
        return final;
    }
}
