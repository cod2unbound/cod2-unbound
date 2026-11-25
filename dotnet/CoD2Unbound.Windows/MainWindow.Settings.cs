using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private SettingsWindow? settingsWindow;

    private void OnSettingsEntry(object sender, RoutedEventArgs args) => OpenSettingsWindow(null);

    /// Opens the one Settings window, or brings it forward on the given pane (a pane's visible name).
    internal void OpenSettingsWindow(string? pane)
    {
        if (closing || flushedForClose || !initialized || session.ReviewingSetup)
            return;
        CloseJumpTo(restoreFocus: false);
        if (settingsWindow is { } open)
        {
            if (!open.AppWindow.IsVisible)
                open.AppWindow.Show();
            open.Show(pane);
            return;
        }
        var window = new SettingsWindow(this, session, Root.RequestedTheme, pane);
        settingsWindow = window;
        window.Closed += async (_, _) =>
        {
            if (ReferenceEquals(settingsWindow, window))
                settingsWindow = null;
            // Every change saves itself; closing the window writes anything still waiting, as the Mac does.
            if (session.Settings is { } settings)
                await ShowProblemAsync(() => settings.FlushAsync());
        };
        window.Activate();
    }

    internal Task OpenSettingsPaneAsync(string pane)
    {
        OpenSettingsWindow(pane);
        return Task.CompletedTask;
    }
}
