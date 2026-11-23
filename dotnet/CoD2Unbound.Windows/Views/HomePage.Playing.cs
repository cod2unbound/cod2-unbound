using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Imaging;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HomePage
{
    private const string PlayingHint = "The game is running. Settings you change apply next time.";
    private const string CloseGameHint = "Closes Call of Duty 2. The launcher stays open.";
    private string? artworkMap;
    private Run? playingTime;
    private DateTimeOffset nextPlayingUpdate;

    private GameServer? HeroServer(GameSessionState game) => game.Running
        ? game.Activity?.ServerAddress is { } address ? session.ServerForAddress(address) : null
        : model.LastServer;

    internal async Task RenderSessionAsync(GameSessionState game)
    {
        var server = HeroServer(game);
        string? map = server is null || server.Map == "Unknown" ? null : MapCatalog.Key(server.Map);
        string lead = game.Running ? "IN THE FIELD NOW" : server is null ? "READY TO PLAY" : "LAST PLAYED";
        HeroLead.Text = map is not null && MapCatalog.Region(map) is { } region
            ? lead + " · " + region.ToUpperInvariant() : lead;
        HeroTitle.Text = map is null ? "COD2 UNBOUND" : MapCatalog.DisplayName(map).ToUpperInvariant();
        RenderHeroDetail(game);
        RenderGameControls(game);
        if (map == artworkMap)
            return;
        artworkMap = map;
        if (map is not null && await model.ReadArtAsync(map) is { } art)
            ApplyArtwork(art);
        else if (HeroArt.Source is WriteableBitmap)
        {
            HeroArt.Source = null;
            artworkLuminance = null;
            RenderBackdrop();
        }
    }

    private void RenderHeroDetail(GameSessionState game)
    {
        HeroDetail.Inlines.Clear();
        playingTime = null;
        if (HeroServer(game) is { } server)
        {
            AddGameName(HeroDetail, server.Name, ActualTheme, shell.IsHighContrast);
            if (server.GameType != "—")
                HeroDetail.Inlines.Add(new Run { Text = "   ·   " + GameModes.Long(server.GameType) });
            if (!game.Running && model.Played(server.Address, includeTime: true) is { } played)
                HeroDetail.Inlines.Add(new Run { Text = "   ·   Played " + played });
        }
        else if (game.Running && game.Activity?.DemoName is { } demo)
            HeroDetail.Inlines.Add(new Run { Text = $"Demo: {demo}" });
        else if (!game.Running)
            HeroDetail.Inlines.Add(new Run { Text = "You’re set up. Pick a server to join your first match, or open the game’s own menu." });

        if (game.Running)
        {
            if (HeroDetail.Inlines.Count > 0)
                HeroDetail.Inlines.Add(new Run { Text = "   ·   " });
            playingTime = new Run();
            HeroDetail.Inlines.Add(playingTime);
            nextPlayingUpdate = default;
            RefreshPlayingTime(game, DateTimeOffset.UtcNow);
        }
    }

    internal void RefreshPlayingTime(GameSessionState game, DateTimeOffset now)
    {
        if (playingTime is null || now < nextPlayingUpdate)
            return;
        playingTime.Text = game.PlayingFor(now) ?? "";
        nextPlayingUpdate = now.AddSeconds(30);
    }

    internal void RenderGameControls(GameSessionState game)
    {
        string? reason = model.LaunchProblem;
        var last = model.LastServer;
        LaunchButton.Visibility = IsVisible(!game.Running && last is not null);
        LaunchButton.IsEnabled = OpenGameMenuButton.IsEnabled = reason is null;
        if (last is not null)
        {
            string name = QuakeColors.Plain(last.Name);
            RejoinMenu.Text = "Launch and rejoin " + name;
            AutomationProperties.SetName(LaunchButton, "Launch and rejoin " + name);
        }
        FindServerButton.Visibility = OpenGameMenuButton.Visibility = IsVisible(!game.Running && last is null);
        FindServerButton.Style = StyleFor("LauncherAccentButton");
        FindServerButton.CornerRadius = new CornerRadius(20);
        ReturnGameButton.Visibility = CloseGameButton.Visibility = IsVisible(game.Running);
        CloseGameButton.IsEnabled = !game.Quitting;
        CloseGameLabel.Text = game.Quitting ? "Closing…" : "Close game";
        AutomationProperties.SetName(CloseGameButton, CloseGameLabel.Text);
        LoadoutChoice.IsEnabled = !model.Loadouts.IsEmpty && !game.Running;
        DisplayChoice.IsEnabled = !game.Running;
        OpenGameData.Visibility = IsVisible(session.Setup.Data.State != SetupDataState.Found);
        OpenCdKey.Visibility = IsVisible(!session.Setup.HasStoredKey);
        OpenGameData.IsEnabled = OpenCdKey.IsEnabled = !game.Running;
        LaunchReason.Text = game.Running ? "" : reason ??
            (game.LastExit is { Unexpected: true } ? "The game closed unexpectedly. Your settings and loadouts are safe." : "");
        LaunchReason.Visibility = IsVisible(LaunchReason.Text.Length > 0);
        LaunchHint.Text = game.Running ? PlayingHint : last is null ? "Ctrl+Shift+Enter" :
            "Ctrl+Enter launches and rejoins this server. Ctrl+Shift+Enter opens the game menu.";
        RenderGameHint(game.Running, shell.AppWindow.Size.Width / (XamlRoot?.RasterizationScale ?? 1) >= 1200);
        AutomationProperties.SetHelpText(LaunchButton, reason ?? "More ways to launch. Press Alt+Down to open the menu.");
        AutomationProperties.SetHelpText(LoadoutChoice, game.Running ? PlayingHint : "");
        AutomationProperties.SetHelpText(DisplayChoice, game.Running ? PlayingHint : "");
        foreach (var button in recentButtons)
        {
            button.IsEnabled = reason is null;
            AutomationProperties.SetHelpText(button, reason ?? "");
        }
    }

    private void RenderGameHint(bool running, bool wide)
    {
        LaunchHint.Visibility = IsVisible(wide && (running || model.CanLaunch));
        string? compactHint = wide ? null : LaunchHint.Text;
        ToolTipService.SetToolTip(ReturnGameButton, running ? compactHint : null);
        ToolTipService.SetToolTip(CloseGameButton, running && !wide ? CloseGameHint + "\n" + PlayingHint : CloseGameHint);
        AutomationProperties.SetHelpText(ReturnGameButton, running ? PlayingHint : "");
        AutomationProperties.SetHelpText(CloseGameButton, CloseGameHint);
        ToolTipService.SetToolTip(LaunchButton, running ? null : compactHint);
        ToolTipService.SetToolTip(OpenGameMenuButton, LauncherShortcuts.Keys(ShellCommand.OpenGameMenu));
    }

    private async void OnLaunchSplit(SplitButton sender, SplitButtonClickEventArgs args) =>
        await LaunchAsync(model.LastServer?.Address);

    private async void OnOpenGameMenu(object sender, RoutedEventArgs args) => await LaunchAsync(null);

    private async void OnConnectAddress(object sender, RoutedEventArgs args) => await shell.OpenConnectAsync();

    private void OnReturnGame(object sender, RoutedEventArgs args) => shell.ReturnToGame();

    private async void OnCloseGame(object sender, RoutedEventArgs args) => await shell.RunAsync(shell.CloseActiveGameAsync);
}
