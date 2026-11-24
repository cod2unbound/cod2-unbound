using CoD2Unbound.Core;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    internal async Task OpenSetupAgainAsync(LauncherPlace returnTo)
    {
        if (session.ClientIsRunning || session.SetupUpdating)
            return;
        await session.StartSetupAgainAsync();
        await NavigateAsync(LauncherPlace.Setup, cancelSetupTo: returnTo);
    }

    internal async Task<GameStartState?> RunGameStartAsync(GameEdition edition, Func<Task<GameStartState>> start)
    {
        if (playerCard is not null)
            await playerCard.FlushNameAsync();
        while (true)
        {
            LaunchRecovery problem;
            try
            {
                var result = await start();
                if (result is not (GameStartState.NeedsSetup or GameStartState.MissingData))
                    return result;
                problem = session.RecoverLaunch(edition, "Choose a complete game data folder before launching.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or LaunchPlanException
                or System.ComponentModel.Win32Exception)
            {
                problem = session.RecoverLaunch(edition, error.Message);
            }
            if (!await FixLaunchAsync(problem))
                return null;
        }
    }

    private async Task<bool> FixLaunchAsync(LaunchRecovery problem)
    {
        string? folderError = null;
        while (true)
        {
            var content = new StackPanel { Spacing = 10, Children = { Body(problem.Message) } };
            if (problem.Path is { Length: > 0 } path)
                content.Children.Add(Caption(path));
            if (folderError is not null)
                content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = folderError });
            var dialog = new ContentDialog
            {
                XamlRoot = DialogRoot, RequestedTheme = DialogTheme, Title = "The game couldn’t start",
                Content = content, DefaultButton = ContentDialogButton.Primary,
                PrimaryButtonText = problem.Kind switch
                {
                    LaunchRecoveryKind.Folder => "Choose a folder…",
                    LaunchRecoveryKind.MissingGame => "Open download page",
                    _ => "Try again"
                },
                SecondaryButtonText = "Show Log", CloseButtonText = "Close"
            };
            switch (await dialog.ShowAsync())
            {
                case ContentDialogResult.Secondary:
                    await ShowGameStartLogAsync();
                    break;
                case ContentDialogResult.Primary when problem.Kind == LaunchRecoveryKind.Folder:
                    if (await PickFolderAsync() is not { } folder)
                        break;
                    var chosen = await session.RepairGameDataAsync(folder);
                    if (chosen.State == SetupDataState.Found)
                        return true;
                    folderError = chosen.Problem ?? "Choose the Call of Duty 2 folder that contains “main”.";
                    break;
                case ContentDialogResult.Primary when problem.Kind == LaunchRecoveryKind.MissingGame:
                    await OpenAsync("https://cod2unbound.com/download");
                    return false;
                case ContentDialogResult.Primary:
                    return true;
                default:
                    return false;
            }
        }
    }

    private async Task ShowGameStartLogAsync()
    {
        string? log = null;
        try { log = await session.ReadLatestLogAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log = error.Message; }
        var text = new TextBox
        {
            Text = log ?? "There’s no game log for this launch yet.",
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 320
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(text, "Last game log");
        await new ContentDialog
        {
            XamlRoot = DialogRoot, RequestedTheme = DialogTheme,
            Title = "Game log", Content = text, CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }
}
