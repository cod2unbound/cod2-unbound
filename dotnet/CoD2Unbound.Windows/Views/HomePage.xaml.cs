using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Color = global::Windows.UI.Color;
using Point = global::Windows.Foundation.Point;
using Rect = global::Windows.Foundation.Rect;
using Size = global::Windows.Foundation.Size;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

/// The Mac's Home (HomeView.swift) on Windows: the hero over the last map's art, the next game's loadout and
/// display chips, anything that needs fixing, and the three cards. Sizes are the Home board's at 1440 × 900.
public sealed partial class HomePage : Page
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly HomeViewModel model;
    private readonly MapPreviews previews;
    private (double Low, double High)? artworkLuminance;
    private Point origin;
    private bool stacked;
    private double cardHeight = 276;

    internal HomePage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        this.session = session;
        model = new(session);
        previews = new(model.ReadArtAsync);
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateTheme();
        Loaded += (_, _) => { UpdateTheme(); Arrange(); };
        Unloaded += (_, _) => shell.SetHomeCaptionPassthrough(null);
        LayoutUpdated += (_, _) => TrackOrigin();
    }

    internal async Task RefreshAsync()
    {
        await model.RefreshAsync();
        RefreshUpdate(session.Updates.State);
        RenderChips();
        RenderNews();
        await RenderPlayHistoryAsync();
        Arrange();
    }

    internal async Task RefreshPlayHistoryAsync()
    {
        await model.RefreshMatchesAsync();
        await RenderPlayHistoryAsync();
        Arrange();
    }

    private async Task RenderPlayHistoryAsync()
    {
        RenderRecent();
        RenderMatch();
        await RenderSessionAsync(model.Game);
    }

    internal void UpdateTheme()
    {
        RenderBackdrop();
        RenderHeroDetail(model.Game);
        RenderRecent();
        RenderMatch();
        RenderNews();
    }

    // MARK: Layout (HomeView.body)

    /// The page's place in the window. The Mac lays Home out in window coordinates: the hero 300 from the
    /// window's left edge, 138 from its top, and the art from the window's corner.
    private void TrackOrigin()
    {
        if (XamlRoot is null || !IsLoaded)
            return;
        var point = TransformToVisual(null).TransformPoint(new Point(0, 0));
        ReportPillBounds();
        if (Math.Abs(point.X - origin.X) < 0.5 && Math.Abs(point.Y - origin.Y) < 0.5)
            return;
        origin = point;
        Arrange();
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs args) => Arrange();

    private void Arrange()
    {
        double width = PageRoot.ActualWidth, height = PageRoot.ActualHeight;
        if (width <= 0 || height <= 0)
            return;
        double windowHeight = height + origin.Y;
        double lead = Math.Max(32, 300 - origin.X);
        double inner = Math.Max(0, width - lead - 32);
        bool narrow = inner < 980;
        double heroTop = Math.Max(16, Math.Min(138, Math.Max(84, windowHeight - 762)) - origin.Y);
        cardHeight = Math.Min(276, Math.Max(262, windowHeight - 624));

        Hero.Width = Math.Min(780, inner);
        Hero.Margin = new Thickness(0, heroTop, 0, 0);
        HeroTitleFit.MaxWidth = Math.Min(780, inner);
        ContentLayout.Padding = new Thickness(lead, 0, 32, 0);
        Cards.Height = cardHeight;
        if (narrow != stacked)
        {
            stacked = narrow;
            RenderChips();
        }
        ArrangeCards();
        FitChips(inner);

        Hero.Measure(new Size(Hero.Width, double.PositiveInfinity));
        Lower.Measure(new Size(inner, double.PositiveInfinity));
        ContentLayout.Height = Math.Max(height, heroTop + Hero.DesiredSize.Height + 24 + Lower.DesiredSize.Height + 28);

        ArtLayer.Height = Math.Max(0, 620 - origin.Y);
        ArtLayer.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, ArtLayer.Height) };
        ArtCanvas.Width = width + origin.X;
        ArtCanvas.Margin = new Thickness(-origin.X, -origin.Y, 0, 0);

        // The update pill sits 16 from the window's top, its end 18 from the caption buttons' 138.
        double rightGap = Math.Max(0, (XamlRoot?.Size.Width ?? width + origin.X) - origin.X - width);
        UpdatePill.Margin = new Thickness(0, Math.Max(8, 16 - origin.Y), Math.Max(18, 156 - rightGap), 0);
        FitNews();
        ReportPillBounds();
    }

    /// One row of cards at every size. Recent servers listed: Jump back in takes the width its rows need
    /// (508 : 298 : 266 at 1440 wide); otherwise 1.22 : 0.9 : 1. Too narrow for three, What's New leaves
    /// the row and becomes a chip.
    private void ArrangeCards()
    {
        double[] shares = stacked ? [1.22, 0.9, 0] : !model.Recent.IsEmpty ? [508, 298, 266] : [1.22, 0.9, 1];
        RecentColumn.Width = new GridLength(shares[0], GridUnitType.Star);
        MatchColumn.Width = new GridLength(shares[1], GridUnitType.Star);
        NewsColumn.Width = stacked ? new GridLength(0) : new GridLength(shares[2], GridUnitType.Star);
        NewsCard.Visibility = IsVisible(!stacked);
        Cards.ColumnSpacing = 18;
        MatchCard.Margin = stacked ? new Thickness(0, 0, -18, 0) : new Thickness();
    }

    // MARK: Backdrop (HeroBackdrop)

    private void RenderBackdrop()
    {
        if (HeroArt.Source is not WriteableBitmap)
        {
            string tone = ActualTheme == ElementTheme.Light ? "light" : "dark";
            HeroArt.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/hero-{tone}.svg"));
        }
        RenderArtScrim();
    }

    /// The scrims that keep the hero's text legible. Over a loading screen they are sized for the dimmest
    /// text in dark (the eyebrow and the hint) and the palest in light, held across the text column to
    /// 74% of the width; over the drawn scene light gets a wash on the left and dark a vignette.
    private void RenderArtScrim()
    {
        HeroScrim.Fill = null;
        HeroVignette.Fill = null;
        HeroFade.Fill = null;
        if (shell.IsHighContrast || PanelPalette.Background is not SolidColorBrush ground)
            return;
        bool dark = ActualTheme != ElementTheme.Light;
        var paper = ground.Color;

        var fade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        fade.GradientStops.Add(new GradientStop { Color = WithAlpha(paper, 0), Offset = dark ? 0.62 : 0.6 });
        fade.GradientStops.Add(new GradientStop { Color = paper, Offset = 1 });
        HeroFade.Fill = fade;

        if (dark)
        {
            var vignette = new RadialGradientBrush
            {
                Center = new(0.62, 0.42), GradientOrigin = new(0.62, 0.42), RadiusX = 0.78, RadiusY = 0.78,
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };
            vignette.GradientStops.Add(new GradientStop { Color = WithAlpha(Microsoft.UI.Colors.Black, 0), Offset = 0.55 });
            vignette.GradientStops.Add(new GradientStop { Color = WithAlpha(Microsoft.UI.Colors.Black, 0.62), Offset = 1 });
            HeroVignette.Fill = vignette;
        }

        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        if (artworkLuminance is { } brightness)
        {
            double text = LimitingLuminance(dark);
            var shade = dark ? Microsoft.UI.Colors.Black : paper;
            double opacity = dark ? ScrimMath.DarkScrim(brightness.High, text) :
                ScrimMath.LightWash(brightness.Low, text, ScrimMath.Luminance((byte)0xef, (byte)0xe8, (byte)0xd8));
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(shade, opacity), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(shade, opacity), Offset = 0.74 });
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(shade, dark ? 0.15 : 0.1), Offset = 0.95 });
        }
        else if (!dark)
        {
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(paper, 0.85), Offset = 0.14 });
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(paper, 0), Offset = 0.6 });
        }
        else
            return;
        HeroScrim.Fill = brush;
    }

    /// Light text needs the darkest background for its dimmest colour; dark text the lightest for its palest
    /// (title, meta, eyebrow and hint: HeroInk on the Mac).
    private double LimitingLuminance(bool dark)
    {
        double[] inks = [.. new[] { "HeroInkBrush", "HeroAccentBrush", "HeroHintBrush" }
            .Select(key => ThemeBrush(key, dark ? ElementTheme.Dark : ElementTheme.Light, false))
            .OfType<SolidColorBrush>().Select(brush => Luminance(brush.Color))];
        return inks.Length == 0 ? (dark ? 0.5 : 0.1) : dark ? inks.Min() : inks.Max();
    }

    private static double Luminance(Color color) => ScrimMath.Luminance(color.R, color.G, color.B);

    private static Color WithAlpha(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), color.R, color.G, color.B);

    private void ApplyArtwork(MapPixels art)
    {
        artworkLuminance = ScrimMath.Percentiles(art.Rgba.AsSpan(), art.Width, art.Height, (0, 0, 1, 1), 0.01, 1);
        HeroArt.Source = MapBitmap(art);
        RenderArtScrim();
    }
}
