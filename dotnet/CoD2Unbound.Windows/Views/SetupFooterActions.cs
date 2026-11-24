using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CoD2Unbound.Windows.Views;

/// <summary>The trailing actions take the width of their widest visible button.</summary>
public sealed class SetupFooterActions : Panel
{
    private const double Spacing = 8;
    private double actionWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var constraint = new Size(double.PositiveInfinity, availableSize.Height);
        actionWidth = 0;
        double height = 0;
        int visibleCount = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            child.Measure(constraint);
            actionWidth = Math.Max(actionWidth, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
            visibleCount++;
        }

        double width = actionWidth * visibleCount + Spacing * Math.Max(0, visibleCount - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double left = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            child.Arrange(new Rect(left, 0, actionWidth, finalSize.Height));
            left += actionWidth + Spacing;
        }

        return finalSize;
    }
}
