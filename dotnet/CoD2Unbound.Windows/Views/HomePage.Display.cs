using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static CoD2Unbound.Windows.Views.PagePresentation;
using Size = global::Windows.Foundation.Size;

namespace CoD2Unbound.Windows.Views;

/// The chips under the hero (HomeChips on the Mac): the next game's loadout and its resolution, the display,
/// a chip when the loadout asks for a size this display can't show, one for each thing that needs fixing,
/// and What's New when the window has no room for its card.
public sealed partial class HomePage
{
    private readonly MenuFlyout loadoutMenu = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private bool showsResolution, showsData, showsKey;

    /// The chips' words and which of them show; the rows are fitted in FitChips.
    private void RenderChips()
    {
        var settings = model.Settings;
        var active = model.Loadouts.FirstOrDefault(profile => profile.IsActive);
        bool loadouts = !model.Loadouts.IsEmpty;
        string requested = active?.RequestedResolution ?? settings.Resolution;
        if (active is null)
        {
            LoadoutText.Text = "Loadout unavailable";
            AutomationProperties.SetName(LoadoutChip, "Loadout unavailable. Choose the loadout for your next game");
        }
        else
        {
            LoadoutText.Text = $"{active.DisplayName} loadout · {LauncherText.Resolution(requested)}";
            AutomationProperties.SetName(LoadoutChip,
                $"{active.DisplayName} loadout, {Spoken(requested)}. Choose the loadout for your next game");
        }

        string mode = settings.Fullscreen switch
        {
            "exclusive" => "Exclusive fullscreen", "borderless" => "Borderless", "windowed" => "Window", _ => "Fullscreen"
        };
        DisplayText.Text = loadouts ? mode : $"{LauncherText.Resolution(settings.Resolution)} · {mode}";
        AutomationProperties.SetName(DisplayChip, (loadouts ? $"Display mode, {mode.ToLowerInvariant()}" :
            $"Display, {Spoken(settings.Resolution)}, {mode.ToLowerInvariant()}") + ". Open Display settings");

        var display = WindowsDisplays.ForLoadout(WindowsDisplayInventory.ReadForGame(), settings.WindowsMonitor, active?.RequestedMonitor);
        var adjustment = active is null ? null : WindowsDisplays.Adjustment(requested, display);
        showsResolution = adjustment.HasValue;
        if (adjustment is { } fitting)
        {
            string used = SettingsPage.SizeText(fitting);
            ResolutionText.Text = $"This display can’t show {LauncherText.Resolution(requested)} ·";
            AutomationProperties.SetName(ResolutionChip, $"This display can’t show {Spoken(requested)}. " +
                $"The game will use {used.Replace(" × ", " by ", StringComparison.Ordinal)}. Choose a resolution in Configs");
            ToolTipService.SetToolTip(ResolutionChip, $"The game will use {used} on this display.");
        }

        bool running = model.Game.Running;
        showsData = session.Setup.Data.State != SetupDataState.Found;
        showsKey = !session.Setup.HasStoredKey;
        LoadoutChip.Visibility = IsVisible(loadouts);
        DataChip.IsEnabled = KeyChip.IsEnabled = ResolutionChip.IsEnabled = !running;
        AutomationProperties.SetHelpText(LoadoutChip, running ? ConfigProfileException.ReadOnlyWhilePlaying : "");
        FitChips(Math.Max(0, PageRoot.ActualWidth - ContentLayout.Padding.Left - ContentLayout.Padding.Right));

        static string Spoken(string size) => size.Replace("x", " by ", StringComparison.Ordinal);
    }

    /// One row when everything fits; otherwise loadout, display and What's New first, the rest below.
    private void FitChips(double width)
    {
        var first = new List<Button>();
        if (LoadoutChip.Visibility == Visibility.Visible)
            first.Add(LoadoutChip);
        first.Add(DisplayChip);
        var second = new List<Button>();
        if (showsResolution) second.Add(ResolutionChip);
        if (showsData) second.Add(DataChip);
        if (showsKey) second.Add(KeyChip);
        foreach (var chip in new[] { ResolutionChip, DataChip, KeyChip })
            chip.Visibility = IsVisible(second.Contains(chip));
        NewsChip.Visibility = IsVisible(stacked);
        var news = stacked ? new[] { NewsChip } : [];
        double total = first.Concat(second).Concat(news).Sum(Measure) + 10 * Math.Max(0, first.Count + second.Count + news.Length - 1);
        bool one = width <= 0 || total <= width;
        var top = (one ? first.Concat(second).Concat(news) : first.Concat(news)).ToList();
        List<Button> below = one ? [] : second;
        // Hidden chips wait at the end of the first row.
        top.AddRange(new[] { LoadoutChip, DisplayChip, ResolutionChip, DataChip, KeyChip, NewsChip }.Where(chip => !top.Contains(chip) && !below.Contains(chip)));
        if (!ChipRowOne.Children.SequenceEqual(top) || !ChipRowTwo.Children.SequenceEqual(below))
        {
            ChipRowOne.Children.Clear();
            ChipRowTwo.Children.Clear();
            foreach (var chip in top)
                ChipRowOne.Children.Add(chip);
            foreach (var chip in below)
                ChipRowTwo.Children.Add(chip);
        }
        ChipRowTwo.Visibility = IsVisible(below.Count > 0);

        static double Measure(Button chip)
        {
            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return chip.DesiredSize.Width;
        }
    }

    /// The menu under the loadout chip: which loadout the next game uses, then Edit … in Configs.
    /// While the game runs it keeps the loadout it started with, so the choices wait.
    private void OnLoadoutChip(object sender, RoutedEventArgs args)
    {
        if (model.Loadouts.IsEmpty)
            return;
        loadoutMenu.Items.Clear();
        loadoutMenu.Items.Add(new MenuFlyoutItem
        {
            Text = "Use for your next game", IsEnabled = false, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var settings = model.Settings;
        foreach (var profile in model.Loadouts)
        {
            string size = profile.RequestedResolution ?? settings.Resolution;
            var item = new RadioMenuFlyoutItem
            {
                Text = profile.DisplayName, GroupName = "HomeLoadout", IsChecked = profile.IsActive, IsEnabled = !model.Game.Running,
                KeyboardAcceleratorTextOverride = DisplayResolution.TryParse(size, out _) ? LauncherText.Resolution(size) : ""
            };
            string id = profile.Id;
            item.Click += async (_, _) => await UseLoadoutAsync(id);
            loadoutMenu.Items.Add(item);
        }
        loadoutMenu.Items.Add(new MenuFlyoutSeparator());
        var current = model.Loadouts.FirstOrDefault(profile => profile.IsActive) ?? model.Loadouts[0];
        var edit = new MenuFlyoutItem { Text = $"Edit {current.DisplayName} in Configs…", IsEnabled = !model.Game.Running };
        string currentId = current.Id;
        edit.Click += async (_, _) => await shell.RunAsync(() => shell.OpenLoadoutInConfigsAsync(currentId));
        loadoutMenu.Items.Add(edit);
        loadoutMenu.ShowAt(LoadoutChip);
    }

    private async Task UseLoadoutAsync(string id)
    {
        if (model.Game.Running || model.Loadouts.FirstOrDefault(profile => profile.IsActive)?.Id == id)
            return;
        await shell.RunAsync(async () =>
        {
            try { await model.SetLoadoutAsync(id); }
            finally { await RefreshAsync(); }
            await shell.RefreshPlayerAsync(loadoutChanged: true);
        });
    }

    /// The display chip opens Settings at Display, where the mode and size are chosen (as on the Mac).
    private async void OnDisplayChip(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        const int displayPane = 1;
        if (model.Settings.LastSettingsPane != displayPane)
            await session.UpdateSettingsAsync(model.Settings with { LastSettingsPane = displayPane });
        await shell.NavigateAsync(LauncherPlace.Settings);
    });

    private async void OnResolutionChip(object sender, RoutedEventArgs args) => await shell.RunAsync(shell.OpenResolutionAsync);

    private async void OnDataChip(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => shell.OpenSetupAgainAsync(LauncherPlace.Home));

    private async void OnKeyChip(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(async () => { await shell.EditCdKeyAsync(); });
}
