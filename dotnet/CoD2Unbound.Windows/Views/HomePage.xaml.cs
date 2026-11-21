using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Color = global::Windows.UI.Color;
using Windows.Security.Cryptography;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HomePage : Page
{
    private readonly MainWindow shell;
    private readonly HomeViewModel model;
    private bool settingDisplay = true;
    private bool settingLoadout = true;
    private (double Low, double High)? artworkLuminance;

    internal HomePage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        model = new(session);
        InitializeComponent();
        ActualThemeChanged += (_, _) => RenderBackdrop();
        Loaded += (_, _) => RenderBackdrop();
    }

    internal async Task RefreshAsync()
    {
        await model.RefreshAsync();
        var last = model.LastServer;
        string? map = last is null || last.Map == "Unknown" ? null : MapCatalog.Key(last.Map);
        if (last is not null)
        {
            HeroLead.Text = string.Join(" · ", new[] { "Last played", map is null ? null : MapCatalog.Region(map) }.OfType<string>());
            if (map is not null)
                HeroTitle.Text = MapCatalog.DisplayName(map).ToUpperInvariant();
            HeroDetail.Text = string.Join(" · ", new[] { QuakeColors.Plain(last.Name),
                last.GameType != "—" ? GameModes.Long(last.GameType) : null, model.LastPlayed }.OfType<string>());
        }
        else
        {
            HeroDetail.Text = "You’re set up. Pick a server to join your first match, or open the game’s own menu.";
        }

        var settings = model.Settings;
        settingLoadout = true;
        string resolution = LauncherText.Resolution(settings.Resolution);
        var loadouts = model.Loadouts.Select(profile => new ComboBoxItem
        {
            Content = profile.DisplayName + " · " + resolution, Tag = profile.Id
        }).ToList();
        var active = model.Loadouts.FirstOrDefault(profile => profile.IsActive);
        if (active is null && loadouts.Count > 0)
            loadouts.Insert(0, new ComboBoxItem { Content = "Loadout unavailable" });
        LoadoutChoice.ItemsSource = loadouts;
        LoadoutChoice.SelectedItem = active is null ? loadouts.FirstOrDefault() :
            loadouts.First(choice => Equals(choice.Tag, active.Id));
        LoadoutChoice.Visibility = IsVisible(loadouts.Count > 0);
        settingLoadout = false;
        settingDisplay = true;
        string prefix = loadouts.Count == 0 ? resolution + " · " : "";
        DisplayChoice.ItemsSource = new[]
        {
            new ComboBoxItem { Content = prefix + "Borderless", Tag = "borderless" },
            new ComboBoxItem { Content = prefix + "Exclusive fullscreen", Tag = "exclusive" },
            new ComboBoxItem { Content = prefix + "Window", Tag = "windowed" }
        };
        DisplayChoice.SelectedIndex = settings.Fullscreen switch { "exclusive" => 1, "windowed" => 2, _ => 0 };
        settingDisplay = false;

        RenderGame(model.Game);
        RenderRecent();
        RenderMatch();
        RenderNews();
        if (map is not null && await model.ReadArtAsync(map) is { } art)
            await ApplyArtworkAsync(art);
    }

    internal void RenderGame(GameSessionState game)
    {
        string? reason = model.LaunchProblem;
        LaunchButton.IsEnabled = reason is null;
        LoadoutChoice.IsEnabled = !model.Loadouts.IsEmpty && !game.Running;
        OpenGameData.Visibility = IsVisible(reason == "Choose a complete game data folder before launching.");
        LaunchReason.Text = reason ?? (game.LastExit is { Unexpected: true } ? "The game closed unexpectedly. Your settings and loadouts are safe."
            : model.LastServer is null ? "Ctrl+Enter launches the game" : "Ctrl+Enter launches and rejoins this server");
        AutomationProperties.SetHelpText(LaunchButton, LaunchReason.Text);
    }

    private void RenderRecent()
    {
        RecentRows.Children.Clear();
        if (model.Recent.IsEmpty)
        {
            AddFact(RecentRows, "Nothing played yet", "Servers you join appear here, ready to rejoin.");
            return;
        }
        foreach (var server in model.Recent)
        {
            var row = new StackPanel { Spacing = 2 };
            var link = new HyperlinkButton { Content = QuakeColors.Plain(server.Name), Padding = new Thickness(0) };
            link.Click += async (_, _) => await shell.OpenServersAsync(ServerScope.Recent, server.Address);
            row.Children.Add(link);
            if (MapCatalog.Key(server.Map) is not null)
                row.Children.Add(Secondary(MapCatalog.DisplayName(server.Map) + " · " + GameModes.Long(server.GameType)));
            RecentRows.Children.Add(row);
        }
    }

    private void RenderMatch()
    {
        MatchRows.Children.Clear();
        if (model.LastMatch is { } match)
        {
            AddFact(MatchRows, MapCatalog.DisplayName(match.Map) + " · " + GameModes.Long(match.Mode),
                MatchFacts.Duration(match.DurationSeconds));
            if (match.PingAverageMS is { } ping)
                AddFact(MatchRows, "Ping", $"{ping:F0} ms");
            AddFact(MatchRows, "Connection", MatchFacts.ConnectionText(
                MatchFacts.Connection(match.PacketsReceived > 0 ? 1 : 0, match.PacketLossPercent)));
        }
        else if (!model.Settings.MatchSummariesEnabled)
            AddFact(MatchRows, "Match summaries are off", "Turn them on to see how each match ran. They stay on this PC.");
        else if (model.UnreadableMatches)
            AddFact(MatchRows, "Couldn’t read matches", "Your match summaries couldn’t be read. Try again after your next match.");
        else
            AddFact(MatchRows, "Not measured yet", "After your first match, its ping and connection show up here.");
    }

    private void RenderNews()
    {
        NewsRows.Children.Clear();
        foreach (var dispatch in model.News)
        {
            var item = new StackPanel { Spacing = 4 };
            item.Children.Add(Secondary(dispatch.Stamp));
            AddFact(item, dispatch.Title, dispatch.Summary);
            NewsRows.Children.Add(item);
        }
    }

    private async void OnShowNews(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        var rows = new StackPanel { Spacing = 12 };
        foreach (var dispatch in model.AllNews)
        {
            var item = new StackPanel { Spacing = 4 };
            item.Children.Add(Secondary(dispatch.Stamp));
            AddFact(item, dispatch.Title, dispatch.Summary);
            rows.Children.Add(item);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "What’s New", CloseButtonText = "Close",
            Content = new ScrollViewer { MaxHeight = 360, Content = rows }
        };
        await dialog.ShowAsync();
    });

    private static void AddFact(StackPanel panel, string title, string detail)
    {
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Secondary(detail));
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["SecondaryText"]
    };

    private void RenderBackdrop()
    {
        if (HeroArt.Source is not SoftwareBitmapSource)
        {
            string tone = ActualTheme == ElementTheme.Light ? "light" : "dark";
            HeroArt.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/hero-{tone}.svg"));
        }
        RenderArtScrim();
    }

    internal void RenderArtScrim()
    {
        HeroScrim.Fill = null;
        HeroFade.Fill = null;
        if (shell.IsHighContrast || PanelPalette.Background is not SolidColorBrush panel ||
            HeroLead.Foreground is not SolidColorBrush lead || HeroTitle.Foreground is not SolidColorBrush title)
            return;

        var fade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        var paper = panel.Color;
        fade.GradientStops.Add(new GradientStop { Color = WithAlpha(paper, 0), Offset = 0.65 });
        fade.GradientStops.Add(new GradientStop { Color = paper, Offset = 1 });
        HeroFade.Fill = fade;
        if (artworkLuminance is not { } brightness)
            return;

        bool dark = ActualTheme != ElementTheme.Light;
        var shade = dark ? Microsoft.UI.Colors.Black : paper;
        double first = Luminance(lead.Color);
        double second = Luminance(title.Color);
        double opacity = dark ? ScrimMath.DarkScrim(brightness.High, Math.Min(first, second)) :
            ScrimMath.LightWash(brightness.Low, Math.Max(first, second), Luminance(paper));
        var tint = WithAlpha(shade, opacity);
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        brush.GradientStops.Add(new GradientStop { Color = tint, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = tint, Offset = 0.64 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(shade, 0), Offset = 1 });
        HeroScrim.Fill = brush;
    }

    private static double Luminance(Color color) => ScrimMath.Luminance(color.R, color.G, color.B);

    private static Color WithAlpha(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B);

    private async Task ApplyArtworkAsync(MapPixels art)
    {
        artworkLuminance = ScrimMath.Percentiles(art.Rgba.AsSpan(), art.Width, art.Height, (0, 0, 1, 1), 0, 1);
        var bgra = art.Rgba.ToArray();
        for (int i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            bgra[i + 3] = 255;
        }
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(bgra),
            BitmapPixelFormat.Bgra8, art.Width, art.Height, BitmapAlphaMode.Premultiplied);
        var image = new SoftwareBitmapSource();
        await image.SetBitmapAsync(bitmap);
        HeroArt.Source = image;
        RenderArtScrim();
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        bool wide = args.NewSize.Width >= 1000;
        ContentLayout.Padding = wide ? new Thickness(48, 64, 48, 32) : new Thickness(28, 28, 28, 24);
        HeroTitle.FontSize = wide ? 112 : 84;
        ArtLayer.Height = wide ? 520 : 360;
        ContentLayout.MinHeight = args.NewSize.Height;
    }

    private async void OnLaunch(object sender, RoutedEventArgs args) => await LaunchAsync();

    private async void OnLaunchShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!LaunchButton.IsEnabled)
            return;
        args.Handled = true;
        await LaunchAsync();
    }

    private Task LaunchAsync() => shell.RunAsync(async () =>
    {
        if (!model.CanLaunch)
            return;
        await model.LaunchAsync(model.LastServer?.Address);
        RenderGame(model.Game);
    });

    private async void OnFindServer(object sender, RoutedEventArgs args) => await shell.OpenServersAsync(ServerScope.All);
    private async void OnRecent(object sender, RoutedEventArgs args) => await shell.OpenServersAsync(ServerScope.Recent);
    private async void OnOpenGameData(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => shell.NavigateAsync(LauncherPlace.Settings));

    private async void OnLoadoutChanged(object sender, SelectionChangedEventArgs args)
    {
        if (settingLoadout || LoadoutChoice.SelectedItem is not ComboBoxItem { Tag: string id })
            return;
        await shell.RunAsync(async () =>
        {
            try { await model.SetLoadoutAsync(id); }
            finally { await RefreshAsync(); }
            await shell.RefreshPlayerAsync();
        });
    }

    private async void OnDisplayChanged(object sender, SelectionChangedEventArgs args)
    {
        if (settingDisplay || DisplayChoice.SelectedItem is not ComboBoxItem { Tag: string mode })
            return;
        await shell.RunAsync(() => model.SetDisplayAsync(mode));
    }
}
