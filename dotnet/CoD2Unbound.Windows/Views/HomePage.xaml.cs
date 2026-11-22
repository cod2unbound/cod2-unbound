using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Color = global::Windows.UI.Color;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HomePage : Page
{
    private readonly MainWindow shell;
    private readonly HomeViewModel model;
    private readonly MapPreviews previews;
    private readonly List<Button> recentButtons = [];
    private bool settingLoadout = true;
    private (double Low, double High)? artworkLuminance;

    internal HomePage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        model = new(session);
        previews = new(model.ReadArtAsync);
        InitializeComponent();
        ActualThemeChanged += (_, _) => { RenderBackdrop(); RenderHeroDetail(); };
        Loaded += (_, _) => RenderBackdrop();
    }

    internal async Task RefreshAsync()
    {
        await model.RefreshAsync();
        var last = model.LastServer;
        string? map = last is null || last.Map == "Unknown" ? null : MapCatalog.Key(last.Map);
        if (last is not null)
        {
            HeroLead.Text = string.Join(" · ", new[] { "LAST PLAYED", map is null ? null : MapCatalog.Region(map)?.ToUpperInvariant() }.OfType<string>());
            if (map is not null)
                HeroTitle.Text = MapCatalog.DisplayName(map).ToUpperInvariant();
        }
        RenderHeroDetail();

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
        RenderDisplay();

        RenderRecent();
        RenderGame(model.Game);
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
        LaunchReason.Text = reason ?? (game.LastExit is { Unexpected: true } ? "The game closed unexpectedly. Your settings and loadouts are safe." : "");
        LaunchReason.Visibility = IsVisible(LaunchReason.Text.Length > 0);
        LaunchHint.Text = model.LastServer is null ? "Ctrl+Enter launches the game" : "Ctrl+Enter launches and rejoins this server";
        LaunchHint.Visibility = IsVisible(reason is null);
        AutomationProperties.SetHelpText(LaunchButton, reason ?? LaunchHint.Text);
        foreach (var button in recentButtons)
            button.IsEnabled = reason is null;
    }

    private void RenderHeroDetail()
    {
        HeroDetail.Inlines.Clear();
        if (model.LastServer is not { } last)
        {
            HeroDetail.Text = "You’re set up. Pick a server to join your first match, or open the game’s own menu.";
            return;
        }
        AddGameName(HeroDetail, last.Name, shell.IsHighContrast);
        if (last.GameType != "—")
            HeroDetail.Inlines.Add(new Run { Text = "   ·   " + GameModes.Long(last.GameType) });
        if (model.Played(last.Address, includeTime: true) is { } played)
            HeroDetail.Inlines.Add(new Run { Text = "   ·   Played " + played });
    }

    private void RenderRecent()
    {
        RecentRows.Children.Clear();
        recentButtons.Clear();
        if (model.Recent.IsEmpty)
        {
            RecentRows.Children.Add(EmptyState("History", "Nothing played yet", "Servers you join appear here, ready to rejoin."));
            return;
        }
        foreach (var server in model.Recent)
        {
            var row = Columns(12, new GridLength(64), new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto);
            row.Padding = new Thickness(8, 6, 8, 6);
            Put(row, 0, previews.Tile(server.Map, 64, 44));
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new TextBlock { Text = QuakeColors.Plain(server.Name), Style = StyleFor("BodyStrongTextBlockStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis });
            var description = Caption($"{MapCatalog.DisplayName(server.Map)} · {GameModes.Long(server.GameType)} · " +
                (server.IsCod2x ? "CoD2x" : "Original 1.3"));
            description.TextWrapping = TextWrapping.NoWrap;
            description.TextTrimming = TextTrimming.CharacterEllipsis;
            lines.Children.Add(description);
            Put(row, 1, lines);
            var stamp = MonoCaption(model.Played(server.Address, includeTime: false) ?? "");
            stamp.VerticalAlignment = VerticalAlignment.Center;
            Put(row, 2, stamp);
            var launch = new Button { Style = StyleFor("SmallAction"), VerticalAlignment = VerticalAlignment.Center,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Fluent("Play", 16, filled: true), new TextBlock { Text = "Launch" } } } };
            AutomationProperties.SetName(launch, $"Launch {QuakeColors.Plain(server.Name)}");
            launch.Click += async (_, _) => await LaunchAsync(server.Address);
            recentButtons.Add(launch);
            Put(row, 3, launch);
            RecentRows.Children.Add(row);
        }
    }

    private void RenderMatch()
    {
        MatchRows.Children.Clear();
        AllMatches.Visibility = IsVisible(!model.Matches.IsEmpty);
        if (model.Matches.LastOrDefault() is { } match)
        {
            AddMatch(MatchRows, match);
            var check = new Button { Content = "Open Check-up", HorizontalAlignment = HorizontalAlignment.Right };
            check.Click += async (_, _) => await shell.OpenCheckupAsync();
            MatchRows.Children.Add(check);
        }
        else if (!model.Settings.MatchSummariesEnabled)
            MatchRows.Children.Add(EmptyState("HeartPulse", "Match summaries are off", "Turn them on to see how each match ran. They stay on this PC."));
        else if (model.UnreadableMatches)
            MatchRows.Children.Add(EmptyState("HeartPulse", "Couldn’t read matches", "Try again after your next match."));
        else
            MatchRows.Children.Add(EmptyState("HeartPulse", "Not measured yet", "After your first match, its ping and connection show up here."));
    }

    private void AddMatch(StackPanel target, MatchSummary match)
    {
        var heading = Columns(12, new GridLength(64), new GridLength(1, GridUnitType.Star));
        Put(heading, 0, previews.Tile(match.Map, 64, 44));
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = $"{MapCatalog.DisplayName(match.Map)} · {GameModes.Long(match.Mode)}",
            Style = StyleFor("BodyStrongTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis });
        string duration = MatchFacts.Duration(match.DurationSeconds).ToUpperInvariant();
        if (MatchFacts.Date(match.EndedAt) is { } date)
            duration = MatchFacts.Played(date, TimeProvider.System.GetUtcNow(), TimeZoneInfo.Local, includeTime: false) + " · " + duration;
        title.Children.Add(MonoCaption(duration));
        Put(heading, 1, title);
        target.Children.Add(heading);
        if (match.PingAverageMS is { } ping)
        {
            target.Children.Add(new Border { Height = 1, Style = StyleFor("CardDivider") });
            MatchFact("Ping", Strong($"{ping:F0} ms"));
        }
        var connection = MatchFacts.Connection(match.PacketsReceived > 0 ? 1 : 0, match.PacketLossPercent);
        string label = MatchFacts.ConnectionText(connection);
        if (connection == MatchConnection.Steady)
        {
            var positive = IconLabel("CheckCircle", label, 14);
            ((FontIcon)positive.Children[0]).Style = StyleFor("PositiveIcon");
            ((TextBlock)positive.Children[1]).Style = StyleFor("PositiveCaption");
            MatchFact("Connection", positive);
        }
        else
            MatchFact("Connection", Strong(label));

        static TextBlock Strong(string value) => new() { Text = value, Style = StyleFor("BodyStrongTextBlockStyle") };

        void MatchFact(string title, FrameworkElement value)
        {
            var row = Columns(12, new GridLength(1, GridUnitType.Star), GridLength.Auto);
            Put(row, 0, Body(title));
            Put(row, 1, value);
            target.Children.Add(row);
        }
    }

    private async void OnAllMatches(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var match in model.Matches.Reverse())
        {
            var row = new StackPanel { Spacing = 6 };
            AddMatch(row, match);
            list.Children.Add(new Border { Style = StyleFor("FieldCard"), Child = row });
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "All matches", CloseButtonText = "Close",
            Content = new ScrollViewer { MaxHeight = 360, Content = list }
        };
        await dialog.ShowAsync();
    });

    private void RenderNews()
    {
        NewsRows.Children.Clear();
        NewsChecked.Text = model.NewsChecked is { } checkedAt ? $"Checked {checkedAt}." : "";
        NewsChecked.Visibility = IsVisible(NewsChecked.Text.Length > 0);
        foreach (var dispatch in model.News)
        {
            var item = new StackPanel { Spacing = 4 };
            item.Children.Add(new TextBlock { Text = dispatch.Stamp, Style = StyleFor("MonoCaption") });
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
            item.Children.Add(new TextBlock { Text = dispatch.Stamp, Style = StyleFor("MonoCaption") });
            AddFact(item, dispatch.Title, dispatch.Summary);
            rows.Children.Add(item);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "What’s new", CloseButtonText = "Close",
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
        HeroArt.Source = await MapBitmapAsync(art);
        RenderArtScrim();
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        bool wide = shell.AppWindow.Size.Width / (XamlRoot?.RasterizationScale ?? 1) >= 1200;
        bool compactCards = args.NewSize.Width < 1000;
        ContentLayout.Padding = wide ? new Thickness(48, 72, 48, 32) : new Thickness(40, 40, 40, 24);
        HeroTitle.FontSize = wide ? 132 : 112;
        HeroTitle.LineHeight = 0.86 * HeroTitle.FontSize;
        ArtLayer.Height = wide ? 560 : 420;
        NewsColumn.Width = compactCards ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Cards.ColumnSpacing = compactCards ? 0 : 12;
        Cards.RowSpacing = compactCards ? 20 : 0;
        RecentCard.Margin = compactCards ? new Thickness(0, 0, 12, 0) : new Thickness();
        Grid.SetColumn(NewsCard, compactCards ? 0 : 2);
        Grid.SetColumnSpan(NewsCard, compactCards ? 2 : 1);
        Grid.SetRow(NewsCard, compactCards ? 1 : 0);
        RecentCard.MinHeight = MatchCard.MinHeight = compactCards ? 226 : 0;
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

    private Task LaunchAsync(string? address = null) => shell.RunAsync(async () =>
    {
        if (!model.CanLaunch)
            return;
        await model.LaunchAsync(address ?? model.LastServer?.Address);
        RenderGame(model.Game);
    });

    private async void OnFindServer(object sender, RoutedEventArgs args) => await shell.OpenServersAsync(ServerScope.All);
    private async void OnRecent(object sender, RoutedEventArgs args) => await shell.OpenServersAsync(ServerScope.Recent);
    private async void OnOpenGameData(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => shell.NavigateAsync(LauncherPlace.GameDataSettings));

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

    private void RenderDisplay()
    {
        var settings = model.Settings;
        var selected = settings.Fullscreen switch { "exclusive" => ExclusiveMode, "windowed" => WindowMode, _ => BorderlessMode };
        selected.IsChecked = true;
        string prefix = model.Loadouts.IsEmpty ? LauncherText.Resolution(settings.Resolution) + " · " : "";
        DisplayChoice.Content = IconLabel("Desktop", prefix + selected.Text);
        AutomationProperties.SetName(DisplayChoice, "Display mode: " + selected.Text);
    }

    private async void OnDisplayChanged(object sender, RoutedEventArgs args)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string mode })
            await shell.RunAsync(async () =>
            {
                try { await model.SetDisplayAsync(mode); }
                finally { RenderDisplay(); }
            });
    }
}
