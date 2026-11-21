using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HomePage : Page
{
    private readonly MainWindow shell;
    private readonly HomeViewModel model;
    private bool settingDisplay = true;

    internal HomePage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        model = new(session);
        InitializeComponent();
        ActualThemeChanged += (_, _) => RenderFallback();
        RenderFallback();
    }

    internal async Task RefreshAsync()
    {
        await model.RefreshAsync();
        var last = model.LastServer;
        string? map = last is null ? null : MapCatalog.Key(last.Map);
        if (last is not null)
        {
            HeroLead.Text = string.Join(" · ", new[] { "Last played", map is null ? null : MapCatalog.Region(map) }.OfType<string>());
            if (map is not null)
                HeroTitle.Text = MapCatalog.DisplayName(map).ToUpperInvariant();
            HeroDetail.Text = string.Join(" · ", new[] { QuakeColors.Plain(last.Name),
                last.GameType != "—" ? GameModes.Long(last.GameType) : null }.OfType<string>());
        }
        else
        {
            HeroDetail.Text = "You’re set up. Pick a server to join your first match, or open the game’s own menu.";
        }

        var settings = model.Settings;
        LoadoutChoice.ItemsSource = new[] { model.Loadout is { } loadout
            ? loadout.DisplayName + " · " + LauncherText.Resolution(settings.Resolution)
            : "Loadout unavailable · " + LauncherText.Resolution(settings.Resolution) };
        LoadoutChoice.SelectedIndex = 0;
        settingDisplay = true;
        DisplayChoice.SelectedIndex = settings.Fullscreen switch { "exclusive" => 1, "windowed" => 2, _ => 0 };
        settingDisplay = false;

        RenderGame(model.Game);
        RenderRecent();
        RenderMatch();
        RenderNews();
        if (map is not null && await model.ReadArtAsync(map) is { } art)
            ApplyArtwork(art);
    }

    internal void RenderGame(GameSessionState game)
    {
        string? reason = model.LaunchProblem;
        LaunchButton.IsEnabled = reason is null;
        OpenGameData.Visibility = IsVisible(reason == "Choose a complete game data folder before launching.");
        LaunchReason.Text = reason ?? (game.LastExit is { Unexpected: true } ? "Call of Duty 2 closed unexpectedly."
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

    private static void AddFact(StackPanel panel, string title, string detail)
    {
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Secondary(detail));
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["SecondaryText"]
    };

    private void RenderFallback()
    {
        if (HeroArt.Source is WriteableBitmap)
            return;
        string tone = ActualTheme == ElementTheme.Light ? "light" : "dark";
        HeroArt.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/hero-{tone}.svg"));
    }

    private void ApplyArtwork(MapPixels art)
    {
        var bitmap = new WriteableBitmap(art.Width, art.Height);
        var bgra = art.Rgba.ToArray();
        for (int i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            bgra[i + 3] = 255;
        }
        using var output = bitmap.PixelBuffer.AsStream();
        output.Write(bgra);
        bitmap.Invalidate();
        HeroArt.Source = bitmap;
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        bool wide = args.NewSize.Width >= 1000;
        ContentLayout.Padding = wide ? new Thickness(48, 64, 48, 32) : new Thickness(28, 28, 28, 24);
        HeroTitle.FontSize = wide ? 112 : 84;
        HeroArt.Height = wide ? 520 : 330;
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

    private async void OnDisplayChanged(object sender, SelectionChangedEventArgs args)
    {
        if (settingDisplay || DisplayChoice.SelectedItem is not ComboBoxItem { Tag: string mode })
            return;
        await shell.RunAsync(() => model.SetDisplayAsync(mode));
    }
}
