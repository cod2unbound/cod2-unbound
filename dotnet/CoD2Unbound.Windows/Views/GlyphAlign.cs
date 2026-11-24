using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CoD2Unbound.Windows.Views;

/// <summary>
/// Glyph and label: the one shared way a symbol sits beside a word. In a horizontal StackPanel whose children
/// include a FontIcon and, after it, a TextBlock, the symbol's ink centre is moved onto the label's cap-height
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
    internal static double CentredNudge(double textSize, FontIcon icon)
    {
        var (lineEm, inkEm) = Metrics(icon);
        double capFromCentre = (TextAscentEm - CapHalfEm - TextLineEm / 2) * textSize;
        double inkFromCentre = (inkEm - lineEm / 2) * icon.FontSize;
        return capFromCentre - inkFromCentre;
    }

    private static (double LineEm, double InkCentreEm) Metrics(FontIcon icon)
    {
        bool fluent = icon.FontFamily?.Source?.Contains("FluentSystemIcons", StringComparison.Ordinal) == true;
        var raise = fluent ? FluentInkRaise : SystemInkRaise;
        double ink = (fluent ? FluentInkCentreEm : SystemInkCentreEm) - raise.GetValueOrDefault(icon.Glyph ?? "");
        return (fluent ? FluentLineEm : SystemLineEm, ink);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
            return;
        switch (d)
        {
            case StackPanel panel:
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
        if (presenter.Content is StackPanel panel)
            Watch(panel);
    }

    private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached(
        "Watched", typeof(bool), typeof(GlyphAlign), new PropertyMetadata(false));

    private static void Watch(StackPanel panel)
    {
        if ((bool)panel.GetValue(WatchedProperty))
            return;
        panel.SetValue(WatchedProperty, true);
        panel.SizeChanged += (sender, _) => Align((StackPanel)sender);
        panel.Loaded += (sender, _) => Align((StackPanel)sender);
        // The pair's own size changes move the centres without resizing the panel (a label that changes weight).
        foreach (var child in panel.Children)
            if (child is FrameworkElement element)
                element.SizeChanged += (_, _) => Align(panel);
        if (panel.IsLoaded)
            Align(panel);
    }

    private static (FontIcon Icon, TextBlock Text)? Pair(StackPanel panel)
    {
        if (panel.Orientation != Orientation.Horizontal)
            return null;
        FontIcon? icon = null;
        foreach (var child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible)
                continue;
            if (icon is null && child is FontIcon found)
                icon = found;
            else if (icon is not null && child is TextBlock text)
                return (icon, text);
        }
        return null;
    }

    private static void Align(StackPanel panel)
    {
        if (Pair(panel) is not { } pair)
            return;
        var (icon, text) = pair;
        if (icon.ActualHeight <= 0 || text.ActualHeight <= 0)
            return;
        var (lineEm, inkEm) = Metrics(icon);
        // The icon draws its line box centred in its own box; the label draws its first line from its top.
        double lineHeight = lineEm * icon.FontSize;
        double inkCentre = icon.ActualOffset.Y + (icon.ActualHeight - lineHeight) / 2 + inkEm * icon.FontSize;
        double capCentre = text.ActualOffset.Y + text.BaselineOffset - CapHalfEm * text.FontSize;
        double move = Math.Round(capCentre - inkCentre, 2);
        // The behaviour owns the icon's render transform; a page that needs another transform turns it off.
        if (icon.RenderTransform is TranslateTransform translate)
        {
            if (translate.Y == move)
                return;
            translate.Y = move;
        }
        else
        {
            icon.RenderTransform = new TranslateTransform { Y = move };
        }
#if UPDATE_FIXTURES
        if (traced++ < 40)
            StartupTrace.Mark($"glyph-align {++moved} U+{(int)(icon.Glyph?.FirstOrDefault() ?? ' '):X4}/{icon.FontSize}" +
                $" '{text.Text}'/{text.FontSize} moved {move:0.00}");
#endif
    }

#if UPDATE_FIXTURES
    private static int moved;
#endif
}
