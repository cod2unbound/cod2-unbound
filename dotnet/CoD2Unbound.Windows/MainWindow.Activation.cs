using System.Runtime.InteropServices;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private readonly Queue<string> activations = new();
    private bool initialized;

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
            activations.Enqueue(argument);
        ScheduleActivation();
    }

    private void ScheduleActivation()
    {
        if (initialized && !busy && !closing && !session.SetupActive && activations.Count != 0)
            DispatcherQueue.TryEnqueue(OpenNextActivation);
    }

    private async void OpenNextActivation()
    {
        if (!busy && !closing && !session.SetupActive && activations.TryDequeue(out string? input))
            await RunAsync(() => OpenActivationAsync(input));
    }

    private async Task OpenActivationAsync(string input)
    {
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
            await NavigateAsync(LauncherPlace.Servers, refreshServers: false);
            await ((ServersPage)ContentFrame.Content).ConnectLinkAsync(link);
            return;
        }

        string extension = Path.GetExtension(input);
        if (!Path.IsPathFullyQualified(input) || extension.ToLowerInvariant() is not (".cfg" or ".dm_1"))
            throw new IOException("Choose a .cfg config file or a .dm_1 demo recording.");

        if (extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase))
        {
            await NavigateAsync(LauncherPlace.Configs);
            await ((ConfigsPage)ContentFrame.Content).ReviewFileAsync(input);
        }
        else
        {
            await NavigateAsync(LauncherPlace.Library);
            await ((LibraryPage)ContentFrame.Content).OfferDemoAsync(input);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
