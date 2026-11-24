using CoD2Unbound.Core;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        ClearRecent.Visibility = IsVisible(model.Scope == ServerScope.Recent && HostingContent.Visibility != Visibility.Visible);
        ClearRecent.IsEnabled = !session.Library.Recent.IsEmpty;
        EmptyServers.Visibility = IsVisible(empty);
        if (!empty)
            return;

        string query = model.Search.Trim();
        bool filtered = model.Filters.Count != 0;
        (string title, string message, string action) = model.Scope switch
        {
            _ when refreshing is not null => ("Finding servers", "Servers appear here as they answer.", ""),
            ServerScope.Recent when session.Library.Recent.IsEmpty =>
                ("Nothing played yet", "Servers you launch on appear here, ready to rejoin.", "Show all servers"),
            ServerScope.Favorites when session.Library.Favorites.IsEmpty =>
                ("No favorites yet", "Star a server to keep it here and in the sidebar.", "Show all servers"),
            _ when query.Length != 0 || filtered => ("No match",
                query.Length == 0 ? "No server matches these filters." :
                    filtered ? $"No server matches “{query}” with these filters." : $"No server matches “{query}”.",
                query.Length == 0 ? "Clear filters" : filtered ? "Clear search and filters" : "Clear search"),
            _ => ("No contact yet", "Ask the public master servers who’s online. CoD2 Unbound contacts them only when you refresh.",
                "Refresh server list")
        };
        EmptyHeading.Text = title;
        EmptyMessage.Text = message;
        EmptyAction.Content = action;
        EmptyAction.Visibility = IsVisible(action.Length != 0);
    }

    private async void OnEmptyAction(object sender, RoutedEventArgs args)
    {
        if (model.Scope == ServerScope.Recent && session.Library.Recent.IsEmpty ||
            model.Scope == ServerScope.Favorites && session.Library.Favorites.IsEmpty)
            Scopes.SelectedItem = Scopes.Items[0];
        else if (model.Filters.Count != 0 || !string.IsNullOrWhiteSpace(model.Search))
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
            FilterEdition.SelectedIndex = (int)filters.Version;
            string[] codes = ["dm", "tdm", "sd", "ctf", "hq"];
            var modes = codes.Select(code => new ComboBoxItem { Content = GameModes.Long(code), Tag = code }).ToList();
            modes.Insert(0, new ComboBoxItem { Content = "All" });
            FilterMode.ItemsSource = modes;
            FilterMode.SelectedItem = modes.FirstOrDefault(mode => Equals(mode.Tag, filters.Mode)) ?? modes[0];
            var limits = ServerFilters.PingLimits.Select(ping => new ComboBoxItem { Content = $"{ping} ms", Tag = ping }).ToList();
            limits.Insert(0, new ComboBoxItem { Content = "Any" });
            FilterPing.ItemsSource = limits;
            FilterPing.SelectedItem = limits.FirstOrDefault(limit => Equals(limit.Tag, filters.MaxPing)) ?? limits[0];
            HideEmpty.IsChecked = filters.HideEmpty;
            HideFull.IsChecked = filters.HideFull;
            HidePassworded.IsChecked = filters.HidePassworded;
            UpdateClearFilters();
        }
        finally { loadingFilters = false; }
    }

    private void UpdateClearFilters()
    {
        bool search = !string.IsNullOrWhiteSpace(model.Search);
        ClearFilters.Content = search ? model.Filters.Count != 0 ? "Clear search and filters" : "Clear search" : "Clear filters";
        ClearFilters.IsEnabled = search || model.Filters.Count != 0;
    }

    private async void OnFilterSelection(object sender, SelectionChangedEventArgs args) => await ApplyFiltersAsync();
    private async void OnFilterToggle(object sender, RoutedEventArgs args) => await ApplyFiltersAsync();
    private async void OnClearFilters(object sender, RoutedEventArgs args) => await ClearFiltersAsync();

    private async Task ApplyFiltersAsync()
    {
        if (!ready || loadingFilters)
            return;
        var filters = new ServerFilters((ServerEditionFilter)FilterEdition.SelectedIndex,
            (FilterMode.SelectedItem as ComboBoxItem)?.Tag as string, (FilterPing.SelectedItem as ComboBoxItem)?.Tag as int?,
            HideEmpty.IsChecked == true, HideFull.IsChecked == true, HidePassworded.IsChecked == true);
        await SetLiveFiltersAsync(filters);
    }

    private async Task ClearFiltersAsync()
    {
        SearchBox.Text = "";
        model.Search = "";
        var save = SetLiveFiltersAsync(new());
        OnFiltersOpening(FilterButton.Flyout, EventArgs.Empty);
        await save;
    }

    private async Task SetLiveFiltersAsync(ServerFilters filters)
    {
        refreshMessage = null;
        revealedJumpAddress = null;
        var save = model.SetFiltersAsync(filters);
        UpdateClearFilters();
        Render();
        await shell.ShowProblemAsync(() => save);
    }

    private MenuFlyout BuildRowMenu(ServerRow row)
    {
        var flyout = new MenuFlyout();
        var loadouts = new MenuFlyoutSubItem { Text = "Launch with loadout" };
        var launch = MenuCommand("Launch", () => LaunchRowAsync(row), "Enter");
        var favorite = MenuCommand(row.Favorite ? "Remove from favorites" : "Add to favorites",
            () => ToggleFavoriteAsync(row), "Ctrl+D");
        var copy = MenuCommand("Copy address", () => CopyRowAddressAsync(row), "Ctrl+Shift+C");
        flyout.Items.Add(launch);
        flyout.Items.Add(loadouts);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(favorite);
        flyout.Items.Add(copy);
        flyout.Items.Add(MenuCommand("Copy join link", () => CopyTextAsync(ServerAddress.JoinLink(row.Server.Address,
            row.Server.Fields.IsEmpty ? session.Settings.Working.Engine :
            row.Server.IsCod2x ? GameEdition.Cod2x : GameEdition.Original))));
        flyout.Items.Add(new MenuFlyoutSeparator());
        var refresh = MenuCommand("Refresh this server", () => RefreshRowAsync(row));
        flyout.Items.Add(refresh);
        flyout.Opening += async (_, _) =>
        {
            loadouts.Items.Clear();
            favorite.Text = row.Favorite ? "Remove from favorites" : "Add to favorites";
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
        if (sender is FrameworkElement { DataContext: ServerRow row } && !InsideButton(args.OriginalSource))
        {
            args.Handled = true;
            await LaunchRowAsync(row);
        }
    }

    private async void OnServerListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || KeyPressed(VirtualKey.Control) || KeyPressed(VirtualKey.Shift) ||
            KeyPressed(VirtualKey.Menu) || InsideButton(args.OriginalSource) ||
            (RowFrom(args.OriginalSource) ?? ServerList.SelectedItem as ServerRow) is not { } row)
            return;
        args.Handled = true;
        await LaunchRowAsync(row);
    }

    private static bool KeyPressed(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private ServerRow? FocusedRow() => RowFrom(FocusManager.GetFocusedElement(XamlRoot));

    private static ServerRow? RowFrom(object? source) =>
        source is DependencyObject element ? ((element as ListViewItem ?? ContainingListItem(element))?.Content as ServerRow) : null;

    private static bool InsideButton(object source)
    {
        for (var element = source as DependencyObject; element is not null && element is not ListViewItem;
            element = VisualTreeHelper.GetParent(element))
        {
            if (element is Button)
                return true;
        }
        return false;
    }

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
