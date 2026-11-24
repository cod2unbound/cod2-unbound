using CoD2Unbound.Core;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ServersPage
{
    private bool loadingFilters;

    private void RenderEmpty(bool empty)
    {
        ClearRecent.Visibility = IsVisible(model.Scope == ServerScope.Recent && ShowsServerBrowser);
        ClearRecent.IsEnabled = !session.Library.Recent.IsEmpty;
        EmptyServers.Visibility = IsVisible(empty);
        ColumnHeaders.Visibility = IsVisible(!empty);
        EmptyServers.Margin = new Thickness(16, IsRefreshing || mastersDidNotAnswer ? 60 : 110, 16, 0);
        if (!empty)
            return;

        string query = model.Search.Trim();
        bool filtered = model.Filters.Count != 0;
        (string title, string message, string action) = model.Scope switch
        {
            _ when refreshing is not null => ("Finding servers", "Servers appear here as they answer.", ""),
            ServerScope.Recent when session.Library.Recent.IsEmpty =>
                ("Nothing played yet", "Servers you launch on appear here, ready to rejoin.", "Show All Servers"),
            ServerScope.Favorites when session.Library.Favorites.IsEmpty =>
                ("No favorites yet", "Star a server to keep it here and in the sidebar.", "Show All Servers"),
            _ when model.ScopedCount() == 0 => ("No contact yet",
                "Ask the public master servers who’s online. CoD2 Unbound contacts them only when you refresh.", "Refresh Server List"),
            _ => ("No match", query.Length == 0 ? "No server matches these filters." :
                filtered ? $"No server matches “{query}” with these filters." : $"No server matches “{query}”.",
                query.Length == 0 ? "Clear Filters" : filtered ? "Clear Search and Filters" : "Clear Search")
        };
        EmptyHeading.Text = title.ToUpperInvariant();
        AutomationProperties.SetName(EmptyHeading, title);
        EmptyMessage.Text = message;
        EmptyAction.Content = action;
        EmptyAction.Style = StyleFor(title == "No contact yet" ? "LauncherAccentButton" : "MediumButton");
        EmptyAction.Visibility = IsVisible(action.Length != 0);
    }

    private async void OnEmptyAction(object sender, RoutedEventArgs args)
    {
        if (model.Scope == ServerScope.Recent && session.Library.Recent.IsEmpty ||
            model.Scope == ServerScope.Favorites && session.Library.Favorites.IsEmpty)
            Scopes.SelectedItem = Scopes.Items[0];
        else if (model.ScopedCount() != 0 && (model.Filters.Count != 0 || !string.IsNullOrWhiteSpace(model.Search)))
            await ClearFiltersAsync();
        else
            await RefreshAsync();
    }

    private async void OnClearRecent(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        await session.ClearRecentServersAsync();
        Render();
        EmptyAction.Focus(FocusState.Programmatic);
    });

    private void OnFiltersOpening(object sender, object args)
    {
        loadingFilters = true;
        try
        {
            var filters = model.Filters;
            FilterEdition.SelectedItem = FilterEdition.Items[(int)filters.Version];
            string[] codes = ["dm", "tdm", "sd", "ctf", "hq"];
            var modes = codes.Select(code => new ComboBoxItem { Content = GameModes.Long(code), Tag = code }).ToList();
            modes.Insert(0, new ComboBoxItem { Content = "Any mode" });
            FilterMode.ItemsSource = modes;
            FilterMode.SelectedItem = modes.FirstOrDefault(mode => Equals(mode.Tag, filters.Mode)) ?? modes[0];
            var limits = ServerFilters.PingLimits.Select(ping => new ComboBoxItem { Content = $"Under {ping} ms", Tag = ping }).ToList();
            limits.Insert(0, new ComboBoxItem { Content = "Any ping" });
            FilterPing.ItemsSource = limits;
            FilterPing.SelectedItem = limits.FirstOrDefault(limit => Equals(limit.Tag, filters.MaxPing)) ?? limits[0];
            HideEmpty.IsOn = filters.HideEmpty;
            HideFull.IsOn = filters.HideFull;
            HidePassworded.IsOn = filters.HidePassworded;
            UpdateClearFilters();
        }
        finally { loadingFilters = false; }
    }

    private void UpdateClearFilters() => ClearFilters.IsEnabled = model.Filters.Count != 0;

    private async void OnEditionFilterSelection(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        await ApplyFiltersAsync();

    private async void OnFilterSelection(object sender, SelectionChangedEventArgs args) => await ApplyFiltersAsync();
    private async void OnFilterToggle(object sender, RoutedEventArgs args) => await ApplyFiltersAsync();
    private async void OnClearFilters(object sender, RoutedEventArgs args) =>
        await SetLiveFiltersAsync(new(), reloadFlyout: true);

    private async Task ApplyFiltersAsync()
    {
        if (!ready || loadingFilters)
            return;
        int edition = FilterEdition.Items.IndexOf(FilterEdition.SelectedItem);
        if (edition < 0)
            return;
        var filters = new ServerFilters((ServerEditionFilter)edition,
            (FilterMode.SelectedItem as ComboBoxItem)?.Tag as string, (FilterPing.SelectedItem as ComboBoxItem)?.Tag as int?,
            HideEmpty.IsOn, HideFull.IsOn, HidePassworded.IsOn);
        await SetLiveFiltersAsync(filters);
    }

    private async Task ClearFiltersAsync()
    {
        SearchBox.Text = "";
        model.Search = "";
        await SetLiveFiltersAsync(new(), reloadFlyout: true);
    }

    private async Task SetLiveFiltersAsync(ServerFilters filters, bool reloadFlyout = false)
    {
        revealedJumpAddress = null;
        var save = model.SetFiltersAsync(filters);
        if (reloadFlyout)
            OnFiltersOpening(FilterButton.Flyout, EventArgs.Empty);
        UpdateClearFilters();
        Render();
        await shell.ShowProblemAsync(() => save);
    }

    private MenuFlyout BuildRowMenu(ServerRow row)
    {
        var flyout = new MenuFlyout();
        var loadouts = new MenuFlyoutSubItem { Text = "Launch with Loadout" };
        var launch = MenuCommand("Launch", () => LaunchRowAsync(row), "Enter");
        var favorite = MenuCommand(row.Favorite ? "Remove from Favorites" : "Add to Favorites",
            () => ToggleFavoriteAsync(row), "Ctrl+D");
        var copy = MenuCommand("Copy Address", () => CopyRowAddressAsync(row), "Ctrl+Shift+C");
        flyout.Items.Add(launch);
        flyout.Items.Add(loadouts);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(favorite);
        flyout.Items.Add(copy);
        flyout.Items.Add(MenuCommand("Copy Join Link", () => CopyTextAsync(ServerAddress.JoinLink(row.Server.Address,
            row.Server.Fields.IsEmpty ? session.Settings.Working.Engine :
            row.Server.IsCod2x ? GameEdition.Cod2x : GameEdition.Original))));
        flyout.Items.Add(new MenuFlyoutSeparator());
        var refresh = MenuCommand("Refresh This Server", () => RefreshRowAsync(row));
        flyout.Items.Add(refresh);
        flyout.Opening += async (_, _) =>
        {
            loadouts.Items.Clear();
            favorite.Text = row.Favorite ? "Remove from Favorites" : "Add to Favorites";
            refresh.IsEnabled = refreshing is null;
            loadouts.IsEnabled = !session.ClientIsRunning && refreshing is null;
            launch.IsEnabled = refreshing is null;
            if (!loadouts.IsEnabled)
                return;
            var loading = new MenuFlyoutItem { Text = "Loading…", IsEnabled = false };
            loadouts.Items.Add(loading);
            await shell.ShowProblemAsync(async () =>
            {
                var choices = await session.ReadLoadoutChoicesAsync();
                loadouts.Items.Clear();
                loadouts.IsEnabled = !choices.IsEmpty;
                foreach (var profile in choices)
                    loadouts.Items.Add(MenuCommand(profile.IsActive ? $"{profile.DisplayName} (in use)" : profile.DisplayName,
                        () => LaunchRowAsync(row, profile.Id)));
            });
        };
        return flyout;
    }

    private static MenuFlyoutItem MenuCommand(string title, Func<Task> action, string? shortcut = null)
    {
        var item = new MenuFlyoutItem { Text = title };
        if (shortcut is not null)
        {
            item.KeyboardAcceleratorTextOverride = shortcut;
            AutomationProperties.SetAcceleratorKey(item, shortcut);
        }
        item.Click += async (_, _) => await action();
        return item;
    }

    private async void OnServerRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is Grid { DataContext: ServerRow row } grid &&
            (grid.FindName("RowFavorite") is not Button favorite || !IsInside(args.GetPosition(favorite), favorite)))
        {
            args.Handled = true;
            await LaunchRowAsync(row);
        }
    }

    private async void OnServerListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || KeyPressed(VirtualKey.Control) || KeyPressed(VirtualKey.Shift) ||
            KeyPressed(VirtualKey.Menu) || FocusManager.GetFocusedElement(XamlRoot) is Button ||
            (FocusedRow() ?? ServerList.SelectedItem as ServerRow) is not { } row)
            return;
        args.Handled = true;
        await LaunchRowAsync(row);
    }

    private static bool KeyPressed(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private ServerRow? FocusedRow() => RowFrom(FocusManager.GetFocusedElement(XamlRoot));

    private static ServerRow? RowFrom(object? source) => source switch
    {
        ListViewItem { Content: ServerRow row } => row,
        Button { Tag: ServerRow row } => row,
        _ => null
    };

    private async void OnCopyAddress(object sender, RoutedEventArgs args)
    {
        if (ServerList.SelectedItem is ServerRow row)
            await CopyRowAddressAsync(row);
    }

    private Task CopyRowAddressAsync(ServerRow row) => CopyTextAsync(row.Server.Address);

    private Task CopyTextAsync(string? text) => shell.ShowProblemAsync(() =>
    {
        if (text is not null)
        {
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
        }
        return Task.CompletedTask;
    });

    private Task RefreshRowAsync(ServerRow row) => shell.RunAsync(async () =>
    {
        if (refreshing is not null)
            return;
        var found = await session.RefreshServerAsync(row.Server.Address);
        Render();
        if (found is not null)
            shell.RefreshFavorites();
        RefreshStatus.Text = found is null
            ? $"{row.Name} didn’t answer. It may be offline or busy."
            : $"{QuakeColors.Plain(found.Name)} answered. {found.PlayerCount} of {found.MaxPlayers} players, {found.Ping} ms.";
    });

    private Task LaunchRowAsync(ServerRow row, string? id = null) => shell.RunAsync(async () =>
    {
        if (refreshing is not null || (id is not null && session.ClientIsRunning))
            return;
        ServerList.SelectedItem = row;
        var source = model.Scope == ServerScope.Favorites ? UsageJoin.Favorite : UsageJoin.List;
        var check = CurrentReadyCheck(row.Server.Address);
        if (id is not null && check is not null)
        {
            check.UseLoadout(id, ReadyMode);
            readyChosenName = check.ChosenProfile?.DisplayName;
            RenderReadyCheck();
            UpdateJoinButton();
        }
        var target = new ServerAddress(LaunchLink.CanonicalAddress(row.Server.Address), null);
        string? status = await shell.JoinServerAsync(target, source, known: row.Server, readyCheck: check, loadoutId: id);
        if (status is not null)
            RefreshStatus.Text = status;
        RenderDetails();
    });
}
