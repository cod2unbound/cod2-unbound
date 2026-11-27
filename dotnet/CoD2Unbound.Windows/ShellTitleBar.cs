using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;

namespace CoD2Unbound.Windows;

// Interactive content in the title bar, as Microsoft's title-bar guidance does it for a search box: the
// element sits in a host above the drag strip, and the window marks its rectangle as a passthrough region so
// pointer input reaches it instead of the drag region. One place computes every passthrough rectangle.
public sealed partial class MainWindow
{
    private RectInt32[] passthrough = [];

    /// <summary>
    /// Places <paramref name="element"/> in the title bar between the system insets. The shell clears it
    /// when navigation replaces the current page.
    /// </summary>
    internal void SetTitleBarContent(FrameworkElement? element)
    {
        if (TitleBarContent.Children.Count == 1 && ReferenceEquals(TitleBarContent.Children[0], element))
            return;
        TitleBarContent.Children.Clear();
        if (element is not null)
            TitleBarContent.Children.Add(element);
        UpdatePassthrough();
    }

    private void InitializeTitleBarContent()
    {
        TitleBarContent.LayoutUpdated += (_, _) => UpdatePassthrough();
        Root.SizeChanged += (_, _) => UpdatePassthrough();
        ContentFrame.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.ContentControl.ContentProperty,
            (_, _) => SetTitleBarContent(null));
    }

    private void UpdatePassthrough()
    {
        if (!useCustomTitleBar || flushedForClose || windowsSessionEnding ||
            Root.XamlRoot is not { } xamlRoot || AppWindow is not { TitleBar: { } titleBar } window)
            return;
        double scale = xamlRoot.RasterizationScale;
        var insets = new Thickness(titleBar.LeftInset / scale, 0, titleBar.RightInset / scale, 0);
        if (TitleBarContent.Margin != insets)
            TitleBarContent.Margin = insets;
        var rects = new List<RectInt32>();
        foreach (var child in TitleBarContent.Children.OfType<FrameworkElement>())
        {
            if (child.Visibility != Visibility.Visible || child.ActualWidth <= 0 || child.ActualHeight <= 0)
                continue;
            var bounds = child.TransformToVisual(Root).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
            rects.Add(new RectInt32(
                (int)Math.Floor(bounds.X * scale), (int)Math.Floor(bounds.Y * scale),
                (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale)));
        }
        if (rects.SequenceEqual(passthrough) ||
            InputNonClientPointerSource.GetForWindowId(window.Id) is not { } source)
            return;
        var next = rects.ToArray();
        source.SetRegionRects(NonClientRegionKind.Passthrough, next);
        passthrough = next;
    }
}
