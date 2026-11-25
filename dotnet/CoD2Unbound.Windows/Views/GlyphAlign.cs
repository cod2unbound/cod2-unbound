using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CoD2Unbound.Windows.Views;

/// <summary>
/// Glyph and label: the one shared way a symbol sits beside a word. In a horizontal StackPanel or a Grid whose
/// children include a FontIcon and a TextBlock, the symbol's ink centre is moved onto the label's cap-height
/// centre line, as SF Symbols sit on the Mac. It is worked out from font metrics and the panel's own laid-out
/// children, for any glyph and label size at any scale, so no page tunes a symbol by hand.
/// Where it is on:
/// <list type="bullet">
/// <item>every button drawn by the shared button templates (LauncherStyles: the content presenter carries
/// <c>views:GlyphAlign.IsEnabled="True"</c>, which aligns whatever StackPanel is the button's content);</item>
/// <item>every panel made by <c>PagePresentation.IconLabel</c>;</item>
/// <item>any other panel that sets <c>views:GlyphAlign.IsEnabled="True"</c> itself.</item>
/// </list>
/// </summary>
public static class GlyphAlign
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(GlyphAlign), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    // Segoe UI and Segoe UI Variable (Windows 10 and 11) share these, every weight: cap height 1434/2048.
    private const double CapHalfEm = 717.0 / 2048;
    // Line box top to baseline (winAscent 2210/2048) and the line's height (2724/2048), for the sidebar rows,
    // whose two boxes are centred by NavigationView's template rather than by a panel we can read.
    private const double TextAscentEm = 2210.0 / 2048;
    private const double TextLineEm = 2724.0 / 2048;
    // The bundled Fluent subsets: typo metrics (ascent 500, gap 45 of 500 units), so the line box is 1.09 em
    // and a glyph centred in its em draws its ink centre this far below the box's top. Font metrics put it at
    // 0.5 (gap below the line) to 0.545 (gap split); the drawn captures of 2026-11-24 put it at 0.52.
    private const double FluentLineEm = 545.0 / 500;
    private const double FluentInkCentreEm = 0.52;
    // Segoe Fluent Icons and Segoe MDL2 Assets: ascent = em, no gap; a centred glyph's ink centre at 0.5.
    private const double SystemLineEm = 1;
    private const double SystemInkCentreEm = 0.5;

    // Glyphs whose ink is not centred in their em: the ink centre's height above the em centre, in em
    // (fontTools bounds of the bundled subsets; for the system fonts the mean of the Windows 11 and 10 fonts).
    private static readonly Dictionary<string, double> FluentInkRaise = new()
    {
        [""] = 0.0155, [""] = 0.025, [""] = -0.0245, [""] = -0.016, [""] = 0.016,
        [""] = 0.025, [""] = 0.025, [""] = -0.0247, [""] = 0.0251,
    };
    private static readonly Dictionary<string, double> SystemInkRaise = new()
    {
        [""] = 0.027, [""] = 0.016, [""] = 0.031,
    };

#if UPDATE_FIXTURES
    private static int traced;
#endif

    /// <summary>
    /// The vertical move that puts a glyph's ink centre on a label's cap centre when NavigationView centres the
    /// glyph's box and the label's line box on the same line (the sidebar rows).
    /// </summary>
    internal static void AlignCentered(double textSize, FontIcon icon)
    {
        var (lineEm, inkEm) = Metrics(icon);
        double capFromCentre = (TextAscentEm - CapHalfEm - TextLineEm / 2) * textSize;
        double inkFromCentre = (inkEm - lineEm / 2) * icon.FontSize;
        double scale = icon.XamlRoot?.RasterizationScale ?? 1;
        // At 42 physical pixels these sidebar outlines rasterize one pixel above their optical centres.
        double rasterOffset = scale * icon.FontSize >= 42 && icon.Glyph is "\uF4F0" or "\uEEFB" or "\uF792"
            ? 1 / scale : 0;
        Move(icon, capFromCentre - inkFromCentre + rasterOffset);
    }

    private static (double LineEm, double InkCentreEm) Metrics(FontIcon icon)
    {
        bool fluent = icon.FontFamily?.Source?.Contains("FluentSystemIcons", StringComparison.Ordinal) == true;
        var raise = fluent ? FluentInkRaise : SystemInkRaise;
        double ink = (fluent ? FluentInkCentreEm : SystemInkCentreEm) - raise.GetValueOrDefault(icon.Glyph ?? "");
        double size = icon.FontSize * (icon.XamlRoot?.RasterizationScale ?? 1);
        // The small filled Play outline rounds high at 100% and draws on centre from its 150% size onward.
        if (fluent && icon.Glyph is "\uE9A2" && size <= 12)
            ink -= 0.07;
        // The 18-pixel plus draws high in the toolbar. At 16 pixels its shape already lands on centre.
        if (fluent && icon.Glyph is "\uF109" && size is >= 17.5 and < 18.5)
            ink -= 0.05;
        return (fluent ? FluentLineEm : SystemLineEm, ink);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
            return;
        switch (d)
        {
            case Panel panel:
                Watch(panel);
                break;
            case ContentPresenter presenter:
                // A button's content: align it when it is (or becomes) a StackPanel.
                presenter.RegisterPropertyChangedCallback(ContentPresenter.ContentProperty,
                    (sender, _) => WatchContent((ContentPresenter)sender));
                WatchContent(presenter);
                break;
        }
    }

    private static void WatchContent(ContentPresenter presenter)
    {
        if (presenter.Content is Panel panel)
            Watch(panel);
    }

    private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached(
        "Watched", typeof(bool), typeof(GlyphAlign), new PropertyMetadata(false));

    private static void Watch(Panel panel)
    {
        if ((bool)panel.GetValue(WatchedProperty))
            return;
        panel.SetValue(WatchedProperty, true);
        panel.SizeChanged += (_, _) => Align(panel);
        WatchScale(panel, () => Align(panel));
        // XAML can set the attached property before filling Children.
        panel.Loaded += (_, _) => WatchChildren(true);
        panel.Unloaded += (_, _) => WatchChildren(false);
        if (panel.IsLoaded)
            WatchChildren(true);

        void WatchChildren(bool loaded)
        {
            foreach (var child in panel.Children.OfType<FrameworkElement>())
                if (loaded)
                    child.SizeChanged += ChildSizeChanged;
                else
                    child.SizeChanged -= ChildSizeChanged;
        }

        void ChildSizeChanged(object sender, SizeChangedEventArgs args) => Align(panel);
    }

    private static void WatchScale(FrameworkElement element, Action align)
    {
        XamlRoot? root = null;
        double scale = 0;

        void Changed(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            if (scale == sender.RasterizationScale)
                return;
            scale = sender.RasterizationScale;
            align();
        }

        void Loaded()
        {
            if (!ReferenceEquals(root, element.XamlRoot))
            {
                if (root is not null)
                    root.Changed -= Changed;
                root = element.XamlRoot;
                if (root is not null)
                    root.Changed += Changed;
            }
            scale = root?.RasterizationScale ?? 1;
            align();
        }

        element.Loaded += (_, _) => Loaded();
        element.Unloaded += (_, _) =>
        {
            if (root is not null)
                root.Changed -= Changed;
            root = null;
        };
        if (element.IsLoaded)
            Loaded();
    }

    internal static void WatchCentered(FontIcon icon, Func<double> textSize) =>
        WatchScale(icon, () => AlignCentered(textSize(), icon));

    internal static void WatchBeside(FontIcon icon, TextBlock text)
    {
        // This pair belongs to different app-owned controls, such as a row's ping and its favorite button.
        // TransformToVisual includes the last move, so remove it before computing the original ink position.
        void AlignPair()
        {
            if (!icon.IsLoaded || !text.IsLoaded || icon.XamlRoot != text.XamlRoot)
                return;
            double iconY = icon.TransformToVisual(text).TransformPoint(default).Y;
            double previous = (icon.RenderTransform as TranslateTransform)?.Y ?? 0;
            AlignInk(icon, text, iconY - previous, 0);
        }
        icon.SizeChanged += (_, _) => AlignPair();
        icon.Unloaded += (_, _) => text.SizeChanged -= TextSizeChanged;
        icon.Loaded += (_, _) => text.SizeChanged += TextSizeChanged;
        if (icon.IsLoaded)
            text.SizeChanged += TextSizeChanged;
        WatchScale(icon, AlignPair);

        void TextSizeChanged(object sender, SizeChangedEventArgs args) => AlignPair();
    }

    private static void Align(Panel panel)
    {
        if (panel is StackPanel { Orientation: Orientation.Vertical } ||
            panel.Children.OfType<FontIcon>().FirstOrDefault(icon => icon.Visibility == Visibility.Visible) is not { } icon ||
            panel.Children.OfType<TextBlock>().FirstOrDefault(text => text.Visibility == Visibility.Visible) is not { } text)
            return;
        AlignInk(icon, text, icon.ActualOffset.Y, text.ActualOffset.Y);
    }

    private static void AlignInk(FontIcon icon, TextBlock text, double iconY, double textY)
    {
        if (icon.ActualHeight <= 0 || text.ActualHeight <= 0)
            return;
        var (lineEm, inkEm) = Metrics(icon);
        // The icon draws its line box centred in its own box; the label draws its first line from its top.
        double lineHeight = lineEm * icon.FontSize;
        double inkCentre = iconY + (icon.ActualHeight - lineHeight) / 2 + inkEm * icon.FontSize;
        // A realized row can have a line box before its text layout reports a baseline.
        double baseline = text.BaselineOffset > 0 ? text.BaselineOffset : TextAscentEm * text.FontSize;
        double capCentre = textY + baseline - CapHalfEm * text.FontSize;
        if (!Move(icon, capCentre - inkCentre))
            return;
#if UPDATE_FIXTURES
        if (traced++ < 120 && icon.Glyph is { Length: > 0 } glyph)
            StartupTrace.Mark($"glyph-align U+{char.ConvertToUtf32(glyph, 0):X4}/{icon.FontSize}" +
                $" '{text.Text}'/{text.FontSize} moved {((TranslateTransform)icon.RenderTransform).Y:0.00}" +
                $" (exact {capCentre - inkCentre:0.00}) scale {icon.XamlRoot?.RasterizationScale ?? 1}" +
                $" icon y {iconY:0.00} h {icon.ActualHeight:0.00} text y {textY:0.00}" +
                $" h {text.ActualHeight:0.00} baseline {text.BaselineOffset:0.00}");
#endif
    }

    private static bool Move(FontIcon icon, double offset)
    {
        // The icon and the label are both text, which Windows draws on whole device pixels at any scale; a
        // fractional move is lost or rounded unpredictably, so the move is a whole number of device pixels.
        double scale = icon.XamlRoot?.RasterizationScale ?? 1;
        double move = Math.Round(offset * scale, MidpointRounding.AwayFromZero) / scale;
        if (icon.RenderTransform is TranslateTransform translate)
        {
            if (translate.Y == move)
                return false;
            translate.Y = move;
        }
        else
        {
            icon.RenderTransform = new TranslateTransform { Y = move };
        }
        return true;
    }
}
