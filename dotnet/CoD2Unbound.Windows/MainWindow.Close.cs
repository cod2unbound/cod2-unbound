using System.Collections.Immutable;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private WindowsSessionEnd? sessionEnd;
    private bool windowsSessionEnding;

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (flushedForClose || windowsSessionEnding || !loaded)
            return;
        args.Cancel = true;
        if (closing)
            return;
        if (busy)
        {
            closeRequested = true;
            return;
        }
        await CompleteCloseAsync();
    }

    private async Task CompleteCloseAsync()
    {
        closing = true;
        try
        {
            // Let the canceled app-window close finish before a clean close re-enters it.
            await Task.Yield();
            await ShowProblemAsync(ConfirmCloseAsync);
        }
        finally { closing = false; }
    }

    private async Task ConfirmCloseAsync()
    {
        var plan = new LauncherQuit();
        ImmutableArray<GameProcessIdentity> confirmedGames = [];
        if (session.Settings is not null)
            await session.Game.ReconcileAsync();
        while (!windowsSessionEnding)
        {
            var games = session.Game.State.Games;
            var servers = session.Hosting.ActiveNames;
            switch (plan.Next(!games.IsEmpty, !servers.IsEmpty))
            {
                case QuitPrompt.Game:
                    var answer = await AskCloseAsync("Call of Duty 2 is still running",
                        "Closing CoD2 Unbound leaves the game open.", "Close Launcher Only", "Close Both");
                    if (answer == ContentDialogResult.None)
                        return;
                    confirmedGames = games;
                    plan.AnswerGame(answer == ContentDialogResult.Primary ? GameQuitAnswer.LauncherOnly : GameQuitAnswer.Both);
                    break;
                case QuitPrompt.Servers:
                    var prompt = HostedClosePrompt.For(servers);
                    if (await AskCloseAsync(prompt.Title, prompt.Message, prompt.Action) != ContentDialogResult.Primary)
                        return;
                    plan.AnswerServers();
                    break;
                case QuitPrompt.StopGame:
                    if (games.Any(game => !confirmedGames.Contains(game)))
                    {
                        plan.Cancel();
                        break;
                    }
                    foreach (var game in games)
                        if (!await CloseGameAsync(game))
                            return;
                    break;
                case QuitPrompt.StopServers:
                case QuitPrompt.Exit:
                    if (session.Settings is { } settings)
                        await settings.FlushAsync();
                    await session.Hosting.ShutdownAsync();
                    flushedForClose = true;
                    Close();
                    return;
            }
        }
    }

    private async Task<ContentDialogResult> AskCloseAsync(string title, string message, string primary, string secondary = "")
    {
        WindowsRunningGameJoin.RestoreLauncher(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Activate();
        var dialog = new ContentDialog
        {
            XamlRoot = DialogRoot, RequestedTheme = DialogTheme, Title = title, Content = Body(message),
            PrimaryButtonText = primary, SecondaryButtonText = secondary, CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync();
    }

    private void EndWindowsSession()
    {
        windowsSessionEnding = true;
        closeRequested = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // Windows can end the session as soon as this callback returns. Give owned servers
        // and local settings a bounded opportunity to finish without opening any dialog.
        var token = deadline.Token;
        var settings = session.Settings;
        try
        {
            Task.Run(() => Task.WhenAll(session.Hosting.ShutdownForSessionEndAsync(token),
                settings?.FlushAsync(token) ?? Task.CompletedTask), token).WaitAsync(token).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception) { }
    }
}
