using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HomePage
{
    private void RenderAdjustedResolution(ConfigProfile? profile)
    {
        var settings = model.Settings;
        string requested = profile?.RequestedResolution ?? settings.Resolution;
        var display = WindowsDisplays.ForLoadout(WindowsDisplayInventory.ReadForGame(), settings.WindowsMonitor, profile?.RequestedMonitor);
        var adjustment = WindowsDisplays.Adjustment(requested, display);
        AdjustedResolution.Visibility = IsVisible(adjustment.HasValue && profile is not null);
        if (adjustment is not { } fitting)
            return;
        AdjustedResolution.Content = $"This display can't show {LauncherText.Resolution(requested)} · Choose…";
        ToolTipService.SetToolTip(AdjustedResolution, $"The game will use {SettingsPage.SizeText(fitting)}.");
    }

    private async void OnChooseResolution(object sender, RoutedEventArgs args) => await shell.RunAsync(shell.OpenResolutionAsync);
}
