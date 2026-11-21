using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HomePage : Page
{
    private readonly MainWindow shell;
    private readonly HomeViewModel model;

    internal HomePage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        model = new(session);
        InitializeComponent();
    }

    private async void OnSettingsInvoked(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => shell.NavigateAsync(LauncherPlace.Settings));

    internal async Task RefreshAsync()
    {
        var data = model.RefreshData();
        HomeEdition.Text = model.Edition == GameEdition.Original ? "Original 1.3" : "CoD2x 1.4.6.8";
        HomeDataStatus.Text = DataSummary(data);
        LaunchReason.Text = data.State == SetupDataState.Found
            ? "Launching the game from Windows is unavailable in this build."
            : "Choose a complete game data folder before launching.";
        AutomationProperties.SetHelpText(LaunchButton, LaunchReason.Text);

        HomeLoadout.Text = "";
        HomeLoadout.Visibility = Visibility.Collapsed;
        string? loadoutLabel = null;
        if (await model.ReadLoadoutAsync() is { } loadout)
        {
            HomeLoadout.Text = "Loadout · " + loadout.DisplayName;
            loadoutLabel = HomeLoadout.Text;
            HomeLoadout.Visibility = Visibility.Visible;
        }
        shell.ShowPaneLoadout(loadoutLabel);
    }

}
