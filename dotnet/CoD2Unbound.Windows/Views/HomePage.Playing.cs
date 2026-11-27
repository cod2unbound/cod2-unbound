using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

/// The hero (HomeHero on the Mac): the eyebrow, the map in the stencil face, where and when, the pair of
/// buttons and their hint; and the toast for a game that closed unexpectedly.
public sealed partial class HomePage
{
    private const string PlayingHint = "The game is running. Settings you change apply next time.";
    private const string CloseGameHint = "Closes Call of Duty 2. The launcher stays open.";
    private const string CrashNotice = "The game closed unexpectedly. Your settings and loadouts are safe.";
    private string? artworkMap;
    private TextBlock? playingTime;
    private DateTimeOffset nextPlayingUpdate;
    private GameExit? shownExit;
    private GameExit? dismissedExit;
    private string? crashReport;

    private GameServer? HeroServer(GameSessionState game) => game.Running
        ? game.Activity?.ServerAddress is { } address ? session.ServerForAddress(address) : null
        : model.LastServer;

    private static string? KnownMap(GameServer? server) =>
        server is null || server.Map == "Unknown" ? null : MapCatalog.Key(server.Map);

    internal async Task RenderSessionAsync(GameSessionState game)
    {
        game = HomeFixture.Observe(game, session.Library.Recent.FirstOrDefault());
        var server = HeroServer(game);
        string? map = KnownMap(server);
        string lead = game.Running ? "In the field now" : server is null ? "Ready to play" : "Last played";
        HeroLead.Text = (map is not null && MapCatalog.Region(map) is { } region ? lead + " · " + region : lead).ToUpperInvariant();
        HeroTitle.Text = (map is null ? "CoD2 Unbound" : MapCatalog.DisplayName(map)).ToUpperInvariant();
        AutomationProperties.SetName(HeroTitle, map is null ? "CoD2 Unbound" : MapCatalog.DisplayName(map));
        RenderHeroDetail(game);
        RenderGameControls(game);
        RenderExit(game);
        Arrange();
        // A server joined by address with no match summary has no known map: the backdrop keeps the scene.
        string? artMap = map ?? KnownMap(model.LastServer);
        if (artMap == artworkMap)
            return;
        artworkMap = artMap;
        if (artMap is not null && await model.ReadArtAsync(artMap) is { } art)
            ApplyArtwork(art);
        else if (HeroArt.Source is WriteableBitmap)
        {
            HeroArt.Source = null;
            artworkLuminance = null;
            RenderBackdrop();
        }
    }

    /// The server in its own colours, its mode and when: "Played yesterday at 21:14", or how long the game
    /// has been running. Nothing played yet: the first-run sentence instead.
    private void RenderHeroDetail(GameSessionState game)
    {
        HeroMeta.Children.Clear();
        playingTime = null;
        var server = HeroServer(game);
        bool first = !game.Running && server is null;
        HeroMeta.Visibility = IsVisible(!first);
        HeroDetail.Visibility = IsVisible(first);
        if (first)
            return;
        if (server is not null)
        {
            var name = MetaText();
            AddGameName(name, server.Name, ActualTheme, shell.IsHighContrast);
            AddMeta(name);
            if (server.GameType != "—")
                AddMeta(MetaText(GameModes.Long(server.GameType)));
            if (!game.Running && model.Played(server.Address, includeTime: true) is { } played)
                AddMeta(MetaText("Played " + played));
        }
        else if (game.Activity?.DemoName is { } demo)
            AddMeta(MetaText("Demo: " + demo));
        if (game.Running)
        {
            playingTime = MetaText();
            AddMeta(playingTime);
            nextPlayingUpdate = default;
            RefreshPlayingTime(game, DateTimeOffset.UtcNow);
        }
        AutomationProperties.SetName(HeroMeta, string.Join(", ", HeroMeta.Children.OfType<TextBlock>().Select(text => text.Text)));
    }

    private TextBlock MetaText(string text = "") => new()
    {
        Text = text, Style = StyleFor("BodyTextBlockStyle"), Foreground = Ink("HeroInkBrush"), TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// A launcher brush in this page's appearance (the page may differ from the system's).
    private Brush Ink(string key) => ThemeBrush(key, ActualTheme, shell.IsHighContrast);

    private void AddMeta(TextBlock item)
    {
        if (HeroMeta.Children.Count > 0)
            HeroMeta.Children.Add(new Ellipse
            {
                Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Center,
                Fill = Ink("TertiaryBrush")
            });
        HeroMeta.Children.Add(item);
    }

    internal void RefreshPlayingTime(GameSessionState game, DateTimeOffset now)
    {
        game = HomeFixture.Observe(game, session.Library.Recent.FirstOrDefault());
        if (playingTime is null || now < nextPlayingUpdate)
            return;
        playingTime.Text = game.PlayingFor(now) ?? "";
        nextPlayingUpdate = now.AddSeconds(30);
    }

    internal string StartupLayout => LaunchButton.Visibility == Visibility.Visible ? "Returning" :
        OpenGameMenuButton.Visibility == Visibility.Visible ? "First" : "GameRunning";

    /// Launch and Find a Server for a returning player; Find a Server and Open Game Menu before the first
    /// game; Return to Game and Close Game while it runs. The hint beside them says the key.
    internal void RenderGameControls(GameSessionState game)
    {
        game = HomeFixture.Observe(game, session.Library.Recent.FirstOrDefault());
        string? reason = model.LaunchProblem;
        var last = model.LastServer;
        bool running = game.Running, returning = !running && last is not null, first = !running && last is null;
        LaunchButton.Visibility = IsVisible(returning);
        FindServerButton.Visibility = IsVisible(!running);
        FindServerButton.Style = first ? StyleFor("AccentButtonStyle") : null;
        OpenGameMenuButton.Visibility = IsVisible(first);
        ReturnGameButton.Visibility = CloseGameButton.Visibility = IsVisible(running);
        LaunchButton.IsEnabled = OpenGameMenuButton.IsEnabled = reason is null || running;
        FindServerButton.IsEnabled = !running;
        if (last is not null)
            AutomationProperties.SetName(LaunchButton, "Launch on " + QuakeColors.Plain(last.Name));
        CloseGameButton.IsEnabled = !game.Quitting;
        CloseGameLabel.Text = game.Quitting ? "Closing…" : "Close Game";
        AutomationProperties.SetName(CloseGameButton, game.Quitting ? "Closing…" : "Close game");
        ToolTipService.SetToolTip(CloseGameButton, CloseGameHint);
        AutomationProperties.SetHelpText(CloseGameButton, CloseGameHint);
        AutomationProperties.SetHelpText(ReturnGameButton, running ? PlayingHint : "");

        LaunchHint.Text = running ? PlayingHint : first ? "Ctrl+Shift+Enter" : "Ctrl+Enter launches and rejoins this server";
        AutomationProperties.SetName(LaunchHint, running ? PlayingHint : first ? "Control Shift Enter opens the game menu" :
            "Control Enter launches and rejoins this server");
        LaunchHint.Visibility = IsVisible(running || reason is null);
        AutomationProperties.SetHelpText(LaunchButton, reason ?? "");
        AutomationProperties.SetHelpText(OpenGameMenuButton, reason ?? "");
        ToolTipService.SetToolTip(LaunchButton, reason);
        ToolTipService.SetToolTip(OpenGameMenuButton, reason);
        RenderChips();
        RenderRecentState(reason);
    }

    // MARK: Toast (NoticeBanner on the Mac)

    private void RenderExit(GameSessionState game)
    {
        if (game.Running)
        {
            if (shownExit is not null && ToastReport.Visibility == Visibility.Visible)
                HideToast();
            return;
        }
        if (game.LastExit is { Unexpected: true } exit && exit != dismissedExit && exit != shownExit)
        {
            shownExit = exit;
            crashReport = exit.Report is { } report && File.Exists(report) ? report : null;
            ShowToast(CrashNotice, warning: true, crashReport);
        }
    }

    /// Shows a short message at the bottom of Home (a join that didn't start, the game that closed).
    internal void ShowToast(string message, bool warning, string? report = null)
    {
        Toast.Severity = warning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        Toast.Message = message;
        string fileName = System.IO.Path.GetFileName(report) ?? "";
        ToastReport.Content = fileName.StartsWith("engine-", StringComparison.Ordinal) || fileName.StartsWith("game-", StringComparison.Ordinal)
            ? "Show Game Log" : "Show Crash Report";
        ToastReport.Visibility = IsVisible(report is not null);
        Toast.IsOpen = true;
    }

    private void HideToast() => Toast.IsOpen = false;

    // The InfoBar's own close button: the notice stays dismissed for this exit.
    private void OnToastClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
            dismissedExit = shownExit;
    }

    private async void OnShowCrashReport(object sender, RoutedEventArgs args)
    {
        if (crashReport is not { } report)
            return;
        await shell.RunAsync(() =>
        {
            if ((File.GetAttributes(report) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("This report isn’t a regular file.");
            return OpenAsync(report);
        });
    }

    // MARK: Actions

    private async void OnLaunch(object sender, RoutedEventArgs args) => await LaunchAsync(model.LastServer?.Address);

    private Task LaunchAsync(string? address) => shell.RunAsync(async () =>
    {
        if (!model.CanLaunch)
            return;
        string? status = null;
        if (address is { } target)
            status = await shell.JoinServerAsync(new(target, null), UsageJoin.List, lookupUnknown: true);
        else
            await shell.RunGameStartAsync(model.Settings.Engine, model.LaunchAsync);
        session.Setup.ChooseData(session.Setup.DataPath);
        if (model.Game.Running)
            await RenderPlayHistoryAsync();
        else
            await RenderSessionAsync(model.Game);
        if (status is not null && !model.Game.Running)
            ShowToast(status, warning: true);
    });

    private async void OnFindServer(object sender, RoutedEventArgs args) => await shell.OpenServersAsync(ServerScope.All);

    private async void OnOpenGameMenu(object sender, RoutedEventArgs args) => await LaunchAsync(null);

    private void OnReturnGame(object sender, RoutedEventArgs args) => shell.ReturnToGame();

    private async void OnCloseGame(object sender, RoutedEventArgs args) => await shell.RunAsync(shell.CloseActiveGameAsync);
}
