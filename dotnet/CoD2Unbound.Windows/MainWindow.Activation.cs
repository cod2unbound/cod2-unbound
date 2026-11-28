using System.Runtime.InteropServices;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private readonly Queue<string> activations = new();
    private bool initialized;
    private string? activeNotification;

    internal void ReceiveActivation(string[] arguments, bool bringForward)
    {
        if (closing)
            return;
        if (bringForward)
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            Activate();
            _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        foreach (string argument in arguments)
            if (!argument.StartsWith(WindowsUpdateNotifications.ActivationPrefix, StringComparison.Ordinal) ||
                (activeNotification != argument && !activations.Contains(argument)))
                activations.Enqueue(argument);
        ScheduleActivation();
    }

    private void ScheduleActivation()
    {
        if (!initialized || closing || activations.Count == 0)
            return;
        if (session.SetupActive)
        {
            bool rejectedRecording = false;
            for (int count = activations.Count; count > 0; count--)
            {
                string input = activations.Dequeue();
                if (IsRecordingPath(input))
                    rejectedRecording = true;
                else
                    activations.Enqueue(input);
            }
            if (rejectedRecording)
                ShowShellNotice("Finish setup, then open the recording again.", InfoBarSeverity.Warning);
        }
        else if (!busy)
            DispatcherQueue.TryEnqueue(OpenNextActivation);
    }

    private async void OpenNextActivation()
    {
        if (!busy && !closing && !session.SetupActive && activations.TryDequeue(out string? input))
            await RunAsync(() => OpenActivationAsync(input));
    }

    private async Task OpenActivationAsync(string input)
    {
        if (TaskbarJumpList.Parse(input) is { } task)
        {
            if (task.Command == ShellCommand.Connect)
                await ConnectFromShellAsync();
            else
                await LaunchFromShellAsync(task.Command == ShellCommand.Launch, task.Address);
            return;
        }

        if (input.StartsWith(WindowsUpdateNotifications.ActivationPrefix, StringComparison.Ordinal) &&
            input[WindowsUpdateNotifications.ActivationPrefix.Length..] is { } version && UpdateNotification.IsVersion(version))
        {
            activeNotification = input;
            try { await NavigateAsync(LauncherPlace.UpdateSettings); }
            finally { activeNotification = null; }
            return;
        }

        if (SetupLink.IsSetupScheme(input))
        {
            if (SetupLink.Parse(input) is not { } id)
                throw new FormatException("This link isn’t a player setup CoD2 Unbound can open.");
            await NavigateAsync(LauncherPlace.Configs);
            await ((ConfigsPage)ContentFrame.Content).OpenSetupLinkAsync(id);
            return;
        }

        if (input.StartsWith("cod2:", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("cod2x:", StringComparison.OrdinalIgnoreCase))
        {
            if (ServerLink.Parse(input) is not { } link)
            {
                session.ShowError(UsageProblem.ServerLink);
                throw new FormatException("This link isn’t a server address CoD2 Unbound can open.");
            }
            await NavigateAsync(LauncherPlace.Servers, ServerScope.All, refreshServers: false);
            await ((ServersPage)ContentFrame.Content).ConnectLinkAsync(link);
            return;
        }

        if (IsRecordingPath(input))
            await OpenRecordingAsync(input);
        else if (Path.IsPathFullyQualified(input) && WindowsInstallIdentity.IsConfigFile(input))
        {
            await NavigateAsync(LauncherPlace.Configs);
            await ((ConfigsPage)ContentFrame.Content).ReviewFileAsync(input);
        }
        else
            throw new IOException("Choose a .cfg config file or a .dm_1 demo recording.");
    }

    private static bool IsRecordingPath(string input) => Path.IsPathFullyQualified(input) &&
        WindowsInstallIdentity.IsDemoFile(input);

    private async Task OpenRecordingAsync(string input)
    {
        if (session.ClientIsRunning)
        {
            ShowShellNotice(MediaRow.PlayBlocked, InfoBarSeverity.Warning);
            return;
        }

        string path;
        try
        {
            var files = new WindowsMediaFiles(session.HomePath);
            path = await files.StageOpenedDemoAsync(input);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowShellNotice($"“{Path.GetFileName(input)}” couldn’t be added to your demos. {error.Message}", InfoBarSeverity.Error);
            return;
        }

        await NavigateAsync(LauncherPlace.Library);
        await ((LibraryPage)ContentFrame.Content).PlayOpenedDemoAsync(path);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
