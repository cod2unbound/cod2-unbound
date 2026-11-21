using System.Collections.Immutable;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ServersPage : Page
{
    private readonly MainWindow shell;
    private readonly ServersViewModel model;
    private CancellationTokenSource? refreshing;
    private bool dialogOpen;
    private bool ready;

    internal ServersPage(MainWindow shell, WindowsLauncherSession session, ServerScope scope, string? address)
    {
        this.shell = shell;
        model = new(session, scope);
        InitializeComponent();
        Scopes.SelectedIndex = (int)scope;
        ready = true;
        Render(address);
    }

    private void Render(string? address = null, ImmutableArray<GameServer>? live = null)
    {
        address ??= (ServerList.SelectedItem as ServerRow)?.Server.Address;
        var rows = model.Visible(live).Select(server => new ServerRow(server)).ToArray();
        ServerList.ItemsSource = rows;
        ServerList.SelectedItem = rows.FirstOrDefault(row => row.Server.Address == address) ?? rows.FirstOrDefault();
        int count = model.Filters.Count;
        FilterButton.Content = count == 0 ? "Filters" : $"Filters ({count})";
        if (refreshing is null)
            RefreshStatus.Text = rows.Length == 0 ? EmptyState() : $"{rows.Length} {(rows.Length == 1 ? "server" : "servers")}";
        RenderDetails();
    }

    private string EmptyState()
    {
        if (!string.IsNullOrWhiteSpace(model.Search) || model.Filters.Count > 0)
            return "No match";
        return model.Scope switch
        {
            ServerScope.Favorites => "No favorites yet",
            ServerScope.Recent => "Nothing played yet",
            _ => "No contact yet"
        };
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready || Scopes.SelectedIndex < 0)
            return;
        model.Scope = (ServerScope)Scopes.SelectedIndex;
        Render();
    }

    private void OnSearch(object sender, TextChangedEventArgs args)
    {
        if (!ready)
            return;
        model.Search = SearchBox.Text;
        Render();
    }

    private void OnSort(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<ServerSort>(value, out var sort))
        {
            model.SortBy(sort);
            Render();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args) => RenderDetails();

    private void RenderDetails()
    {
        if (ServerList.SelectedItem is not ServerRow row)
        {
            Details.Visibility = Visibility.Collapsed;
            return;
        }
        Details.Visibility = Visibility.Visible;
        var server = row.Server;
        var facts = ServerFacts.From(server);
        DetailName.Text = row.Name;
        DetailMap.Text = row.Map + " · " + GameModes.Long(server.GameType);
        DetailAddress.Text = server.Address;
        DetailFacts.Text = string.Join(" · ", new[] { facts.VersionLabel, server.Mod, facts.Access }.Where(value => value.Length > 0));
        DetailRoster.Text = "Roster · " + row.Players;
        Roster.Children.Clear();
        foreach (var player in server.Players.OrderByDescending(player => player.Score).Take(32))
            Roster.Children.Add(new TextBlock { Text = $"{QuakeColors.Plain(player.Name)} · {player.Score} · {player.Ping} ms",
                TextTrimming = TextTrimming.CharacterEllipsis });
        FavoriteButton.Content = model.IsFavorite(server.Address) ? "Remove from Favorites" : "Add to Favorites";
        JoinButton.IsEnabled = !model.GameRunning;
    }

    private void OnFiltersOpening(object sender, object args)
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
    }

    private async void OnApplyFilters(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        var filters = new ServerFilters((ServerEditionFilter)FilterEdition.SelectedIndex,
            (FilterMode.SelectedItem as ComboBoxItem)?.Tag as string, (FilterPing.SelectedItem as ComboBoxItem)?.Tag as int?,
            HideEmpty.IsChecked == true, HideFull.IsChecked == true, HidePassworded.IsChecked == true);
        await model.SetFiltersAsync(filters);
        FilterButton.Flyout.Hide();
        Render();
    });

    private async void OnFavorite(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        if (ServerList.SelectedItem is not ServerRow row)
            return;
        await model.FavoriteAsync(row.Server);
        shell.RefreshFavorites();
        Render();
    });

    private async void OnRefresh(object sender, RoutedEventArgs args) => await RefreshAsync();

    internal async Task RefreshAsync()
    {
        if (refreshing is { } active)
        {
            active.Cancel();
            return;
        }
        await shell.RunAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            refreshing = cancellation;
            RefreshButton.Content = "Stop";
            string? message = null;
            try
            {
                var progress = new Progress<ServerRefreshState>(state =>
                {
                    if (refreshing != cancellation)
                        return;
                    Render(live: state.Servers);
                    RefreshStatus.Text = message = state.Message ?? "Refreshing the server list";
                });
                await model.RefreshAsync(progress, cancellation.Token);
            }
            finally
            {
                refreshing = null;
                RefreshButton.Content = "Refresh";
                Render();
                if (message is not null)
                    RefreshStatus.Text = message;
            }
        });
    }

    private async void OnConnect(object sender, RoutedEventArgs args) => await ConnectAsync();

    private Task ConnectAsync(string? address = null) => shell.RunAsync(async () =>
    {
        if (dialogOpen)
            return;
        dialogOpen = true;
        try
        {
            var dialog = new ConnectDialog(address) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Target is { } target)
                await JoinAsync(target);
        }
        finally { dialogOpen = false; }
    });

    private async void OnJoin(object sender, RoutedEventArgs args)
    {
        if (ServerList.SelectedItem is not ServerRow row)
            return;
        if (row.Server.PasswordRequired)
            await ConnectAsync(row.Server.Address);
        else
            await shell.RunAsync(() => JoinAsync(new(row.Server.Address, null)));
    }

    private async Task JoinAsync(ServerAddress address)
    {
        if (model.GameRunning)
        {
            RefreshStatus.Text = "Call of Duty 2 is already running.";
            return;
        }
        var decision = await model.CheckAsync(address);
        if (decision.Problem is { } problem)
        {
            RefreshStatus.Text = problem;
            return;
        }
        if (decision.Action == ServerAdmissionAction.ConfirmVersion)
        {
            string version = decision.Edition == GameEdition.Cod2x ? "CoD2x 1.4.6.8" : "Original 1.3";
            var switchEdition = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Switch game version?", Content = $"This server runs {version}.",
                PrimaryButtonText = "Launch", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
            };
            if (await switchEdition.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        RefreshStatus.Text = (await model.JoinAsync(address, decision.Edition)) switch
        {
            GameStartState.Started => "Launching Call of Duty 2…",
            GameStartState.NeedsSetup or GameStartState.MissingData => "Choose a complete game data folder before launching.",
            _ => "Call of Duty 2 is already running."
        };
        RenderDetails();
    }

    internal void UpdateGameState(GameSessionState state) => RenderDetails();

    private async void OnRefreshShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RefreshAsync();
    }

    private void OnSearchShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = SearchBox.Focus(FocusState.Keyboard);

    private async void OnConnectShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ConnectAsync();
    }
}
