using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private const string ClosingGameNotice = "Closing Call of Duty 2…";
    private const string ReturnGameNotice = "The game window couldn’t be brought forward. Switch to it from the taskbar.";

    internal void ReturnToGame()
    {
        if (session.Game.State.ActiveGame is not { } target)
            return;
        if (session.ReturnToRunningGame(target))
        {
            if (Problem.Message == ReturnGameNotice)
                Problem.IsOpen = false;
            MinimizeForGame();
        }
        else if (!session.Game.State.Quitting)
            ShowShellNotice(ReturnGameNotice, InfoBarSeverity.Warning);
    }

    private void MinimizeForGame()
    {
        if (session.Settings.Working.HideWhilePlaying &&
            WindowsRunningGameJoin.MinimizeLauncher(WinRT.Interop.WindowNative.GetWindowHandle(this)))
            minimizedForGame = true;
    }

    internal async Task CloseActiveGameAsync()
    {
        if (session.Game.State.ActiveGame is { } target)
            await CloseGameAsync(target);
    }

    private async Task<bool> CloseGameAsync(GameProcessIdentity target)
    {
        ShowShellNotice(ClosingGameNotice, InfoBarSeverity.Informational);
        var wait = session.Game.QuitGameAsync(target);
        if (ContentFrame.Content is HomePage home)
            home.RenderGameControls(session.Game.State);
        bool closed;
        try { closed = await wait; }
        finally
        {
            if (Problem.Message == ClosingGameNotice)
                Problem.IsOpen = false;
            if (ContentFrame.Content is HomePage current)
                current.RenderGameControls(session.Game.State);
            await RefreshGameViewsAsync(session.Game.State, DateTimeOffset.UtcNow);
        }
        if (!closed)
            ShowShellNotice("The game didn't close. Switch to it and close it from its own menu.", InfoBarSeverity.Warning);
        return closed;
    }

    private async Task RefreshGameViewsAsync(GameSessionState state, DateTimeOffset now)
    {
        session.GameObserved(state);
        if (!state.Running && minimizedForGame)
        {
            minimizedForGame = false;
            StartupTrace.Mark($"main restore reason=game-exit settingsInFront={settingsWindow?.IsInFront == true}");
            WindowsRunningGameJoin.RestoreLauncher(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        if (lastGame is { } previous && previous.Running == state.Running && previous.LastExit == state.LastExit &&
            previous.Activity == state.Activity && previous.ActiveGame == state.ActiveGame)
        {
            if (ContentFrame.Content is HomePage current)
            {
                current.RefreshPlayingTime(state, now);
                if (previous.Quitting != state.Quitting)
                    current.RenderGameControls(state);
            }
            return;
        }
        bool ended = !state.Running && state.LastExit is not null;
        lastGame = state;
        RefreshShellGameActions();
        settingsWindow?.Page.RefreshDisplayRunning(state.Running);
        playerCard?.UpdateRunning(state.Running);
        if (ContentFrame.Content is HomePage home)
        {
            if (ended)
                await home.RefreshPlayHistoryAsync();
            else
                await home.RenderSessionAsync(state);
        }
        if (ContentFrame.Content is LibraryPage library)
            library.UpdateGameState(state.Running, ended);
        if (ContentFrame.Content is ServersPage servers)
            await servers.UpdateGameStateAsync();
        if (ContentFrame.Content is ConfigsPage configs)
            configs.UpdateGameState(ended);
    }

    private void ShowShellNotice(string message, InfoBarSeverity severity)
    {
        Problem.Severity = severity;
        Problem.IsClosable = severity != InfoBarSeverity.Error;
        Problem.Message = message;
        RefreshShellGameActions();
        Problem.IsOpen = true;
    }

    private void RefreshShellGameActions()
    {
        bool playing = session.Game.State.Running;
        Problem.ActionButton = playing ? Problem.ActionButton ?? NewReturnButton() : null;
        ReportCopied.ActionButton = playing ? ReportCopied.ActionButton ?? NewReturnButton() : null;
        // A report warning can already have a Show in File Explorer action.
        ReportWarning.Content = playing ? ReportWarning.Content ?? NewReturnButton() : null;
    }

    private Button NewReturnButton()
    {
        var button = new Button { Content = "Return to game" };
        button.Click += (_, _) => ReturnToGame();
        return button;
    }
}
