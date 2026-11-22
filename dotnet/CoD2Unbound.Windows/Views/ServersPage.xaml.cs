using System.Collections.Immutable;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Imaging;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ServersPage : Page
{
    private readonly MainWindow shell;
    private readonly ServersViewModel model;
    private readonly WindowsLauncherSession session;
    private readonly MapPreviews previews;
    private readonly Lazy<ConfigReadyCheck> readiness = new(() =>
        new(ConfigCatalog.LoadBundled(), ConfigRules.LoadBundled(), TimeProvider.System));
    private HostingPage? hosting;
    private GameServer? currentReadyServer;
    private string? currentMap;
    private CancellationTokenSource? refreshing;
    private bool dialogOpen;
    private bool ready;

    internal ServersPage(MainWindow shell, WindowsLauncherSession session, ServerScope scope, string? address)
    {
        this.shell = shell;
        this.session = session;
        model = new(session, scope);
        previews = new(map => Task.Run(() => session.ReadMapArtwork(map)));
        InitializeComponent();
        ActualThemeChanged += (_, _) => Render();
        Scopes.SelectedItem = Scopes.Items[(int)scope];
        ready = true;
        RenderSort();
        Render(address);
    }

    private void Render(string? address = null, ImmutableArray<GameServer>? live = null)
    {
        address ??= (ServerList.SelectedItem as ServerRow)?.Server.Address;
        var rows = model.Visible(live).Select(server => new ServerRow(server)).ToArray();
        ServerList.ItemsSource = rows;
        ServerList.SelectedItem = rows.FirstOrDefault(row => row.Server.Address == address) ?? rows.FirstOrDefault();
        int count = model.Filters.Count;
        string filters = count == 0 ? "Filters" : $"Filters ({count})";
        FilterButton.Content = IconLabel("Filter", filters);
        AutomationProperties.SetName(FilterButton, filters);
        if (refreshing is null)
            RefreshStatus.Text = rows.Length == 0 ? EmptyState() : CountWithAge(rows.Length);
        RenderDetails();
    }

    private string CountWithAge(int count)
    {
        string result = $"{count} {(count == 1 ? "server" : "servers")}";
        if (model.CachedAt is not { } date)
            return result;
        var age = DateTimeOffset.UtcNow - date;
        if (age < TimeSpan.FromMinutes(1))
            return result + " · just now";
        if (age < TimeSpan.FromHours(1))
            return result + $" · {(int)age.TotalMinutes} min ago";
        return result + " · " + LauncherText.StencilTime(date.ToLocalTime());
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

    private async void OnScopeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int index = sender.Items.IndexOf(sender.SelectedItem);
        if (!ready || index < 0)
            return;
        bool showHosting = index == sender.Items.Count - 1;
        // New server takes Alt+N while Hosting is active in the shared Servers view.
        RecentScope.AccessKey = showHosting ? "" : "N";
        BrowserContent.Visibility = BrowserActions.Visibility = FilterButton.Visibility =
            SearchBox.Visibility = BrowserSubtitle.Visibility = IsVisible(!showHosting);
        HostingContent.Visibility = NewHost.Visibility = IsVisible(showHosting);
        if (showHosting)
        {
            session.Opened(UsagePage.Hosting);
            await shell.ShowProblemAsync(async () =>
            {
                hosting ??= new HostingPage(shell, session);
                HostingContent.Content = hosting;
                await hosting.RefreshAsync();
            });
            return;
        }
        model.Scope = (ServerScope)index;
        Render();
    }

    private async void OnNewHostedServer(object sender, RoutedEventArgs args)
    {
        if (hosting is not null)
            await hosting.NewAsync();
    }

    internal void UpdateContrast() => Render();

    internal void UpdateHostingState() => hosting?.UpdateRunState();

    private void OnBrowserSizeChanged(object sender, SizeChangedEventArgs args) =>
        Details.Width = args.NewSize.Width >= 1000 ? 320 : 300;

    private void OnSelectionPillLoaded(object sender, RoutedEventArgs args) => TrackSelectionPill((Border)sender);

    private void OnServerRowLoaded(object sender, RoutedEventArgs args)
    {
        var row = (Grid)sender;
        ApplyServerRow(row);
        TrackListSelection(row, selected => ApplyServerNameBrush(row, selected));
    }
    private void OnServerRowChanged(FrameworkElement sender, DataContextChangedEventArgs args) => ApplyServerRow((Grid)sender);

    private void ApplyServerRow(Grid row)
    {
        if (row.DataContext is not ServerRow entry || row.FindName("RowName") is not TextBlock name)
            return;
        var server = entry.Server;
        var facts = ServerFacts.From(server);
        bool contrast = shell.IsHighContrast;
        name.Inlines.Clear();
        AddGameName(name, server.Name, ActualTheme, contrast);
        ApplyServerNameBrush(row, ContainingListItem(row)?.IsSelected == true);
        var tag = (Border)row.FindName("RowEdition")!;
        tag.Style = StyleFor(server.IsCod2x ? "Cod2xTag" : "EditionTag");
        var tagText = (TextBlock)row.FindName("RowEditionText")!;
        var caption = (TextBlock)row.FindName("RowMapMode")!;
        SetRowTextBrush(tagText, server.IsCod2x ? "HeroAccentBrush" : "BodyBrush", contrast);
        SetRowTextBrush(caption, "CaptionBrush", contrast);
        var players = (TextBlock)row.FindName("RowPlayers")!;
        players.Inlines.Clear();
        var used = new Run { Text = server.MaxPlayers > 0 ? server.PlayerCount.ToString() : "—",
            FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        if (!contrast)
            used.Foreground = ThemeBrush(facts.Occupancy == ServerOccupancy.Full ? "NegativeBrush" : "BodyBrush", ActualTheme, false);
        players.Inlines.Add(used);
        if (server.MaxPlayers > 0)
        {
            var capacity = new Run { Text = $" / {server.MaxPlayers}", FontSize = 12 };
            if (!contrast)
                capacity.Foreground = ThemeBrush("TertiaryBrush", ActualTheme, false);
            players.Inlines.Add(capacity);
        }
        var signal = (StackPanel)row.FindName("RowSignal")!;
        signal.Children.Clear();
        int level = server.Ping < 0 ? 0 : facts.PingLevel;
        for (int bar = 1; bar <= 4; bar++)
            signal.Children.Add(new Rectangle { Width = 3, Height = 2 + bar * 2, VerticalAlignment = VerticalAlignment.Bottom,
                Fill = ThemeBrush(bar <= level ? "PositiveBrush" : "ControlStrokeBrush", ActualTheme, shell.IsHighContrast) });
        SetFavorite((Button)row.FindName("RowFavorite")!, server);
        _ = previews.FillAsync((Image)row.FindName("RowArt")!, server.Map);
    }

    private void ApplyServerNameBrush(Grid row, bool selected)
    {
        if (row.FindName("RowName") is not TextBlock name)
            return;
        if (shell.IsHighContrast && selected)
            name.Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush", ActualTheme, true);
        else
            name.ClearValue(TextBlock.ForegroundProperty);
    }

    private void SetFavorite(Button button, GameServer server)
    {
        bool favorite = model.IsFavorite(server.Address);
        button.Content = Fluent(favorite ? "StarFilled" : "Star", 16, filled: favorite);
        button.Foreground = ThemeBrush(favorite || ReferenceEquals(button, FavoriteButton) ? "FavoriteBrush" : "CaptionBrush", ActualTheme, shell.IsHighContrast);
        string action = favorite ? "Remove from Favorites" : "Add to Favorites";
        AutomationProperties.SetName(button, ReferenceEquals(button, FavoriteButton) ? action : $"{action}: {QuakeColors.Plain(server.Name)}");
        ToolTipService.SetToolTip(button, action);
    }

    private async void OnCopyAddress(object sender, RoutedEventArgs args) => await shell.ShowProblemAsync(() =>
    {
        if (ServerList.SelectedItem is ServerRow row)
        {
            var data = new DataPackage();
            data.SetText(row.Server.Address);
            Clipboard.SetContent(data);
        }
        return Task.CompletedTask;
    });

    private void OnSearch(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!ready)
            return;
        model.Search = SearchBox.Text;
        Render();
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        sender.Focus(FocusState.Programmatic);

    private void RenderSort()
    {
        foreach (var button in new[] { SortName, SortPlayers, SortPing })
        {
            string sort = (string)button.Tag;
            string label = sort == nameof(ServerSort.Name) ? "Server" : sort;
            bool active = sort == model.Sort.ToString();
            button.Content = !active ? label : new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                Children = { new TextBlock { Text = label }, Fluent(model.Ascending ? "ChevronUp" : "ChevronDown", 12) }
            };
            AutomationProperties.SetName(button, active ? $"{label}, sorted {(model.Ascending ? "ascending" : "descending")}" : $"Sort by {label}");
        }
    }

    private void OnSort(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<ServerSort>(value, out var sort))
        {
            model.SortBy(sort);
            RenderSort();
            Render();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args) => RenderDetails();

    private void RenderDetails()
    {
        if (ServerList.SelectedItem is not ServerRow row)
        {
            currentReadyServer = null;
            Details.Visibility = Visibility.Collapsed;
            return;
        }
        Details.Visibility = Visibility.Visible;
        var server = row.Server;
        var facts = ServerFacts.From(server);
        DetailName.Inlines.Clear();
        AddGameName(DetailName, server.Name, ActualTheme, shell.IsHighContrast);
        DetailMap.Text = (row.Map + " · " + GameModes.Long(server.GameType)).ToUpperInvariant();
        RenderMapArtwork(server.Map);
        DetailAddress.Text = server.Address;
        DetailVersion.Text = facts.VersionLabel;
        DetailMod.Text = string.IsNullOrWhiteSpace(server.Mod) ? "None" : server.Mod;
        DetailAccess.Text = facts.Access;
        DetailRoster.Text = "Roster · " + row.Players;
        Roster.Children.Clear();
        foreach (var player in server.Players.OrderByDescending(player => player.Score).Take(32))
        {
            var columns = Columns(8, new GridLength(1, GridUnitType.Star), new GridLength(40), new GridLength(40));
            columns.MinHeight = 22;
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            AddGameName(name, player.Name, ActualTheme, shell.IsHighContrast);
            Put(columns, 0, name);
            Put(columns, 1, new TextBlock { Text = player.Score.ToString(), TextAlignment = TextAlignment.Right,
                Style = StyleFor("BodyStrongTextBlockStyle") });
            var ping = Body(player.Ping.ToString());
            ping.TextAlignment = TextAlignment.Right;
            Put(columns, 2, ping);
            Roster.Children.Add(columns);
        }
        SetFavorite(FavoriteButton, server);
        _ = ShowReadinessAsync(server);
        UpdateJoinButton();
    }

    private void UpdateJoinButton()
    {
        bool isRefreshing = refreshing is not null;
        JoinButton.IsEnabled = !isRefreshing && !model.GameRunning;
        string label = isRefreshing ? "Refreshing…" : "Launch";
        JoinButtonLabel.Text = label;
        AutomationProperties.SetName(JoinButton, label);
    }

    private async Task ShowReadinessAsync(GameServer server)
    {
        if (ReferenceEquals(currentReadyServer, server))
            return;
        currentReadyServer = server;
        DetailReady.Visibility = Visibility.Collapsed;
        if (server.ObservedAt is not { } date || DateTimeOffset.UtcNow - date > TimeSpan.FromMinutes(1))
            return;

        await shell.ShowProblemAsync(async () =>
        {
            var active = await session.ReadLoadoutAsync();
            if (active is null)
                return;
            var mode = server.IsCod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock;
            var dialect = server.IsCod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock;
            var store = new ConfigProfileStore(new WindowsConfigProfiles(session.HomePath, 0), dialect, () => session.ClientIsRunning);
            var report = await Task.Run(() => readiness.Value.Evaluate(server, store.Read(active.Id), mode));
            if (ReferenceEquals(currentReadyServer, server) && report.Status == ConfigReadyStatus.MatchesKnownRules)
            {
                DetailReady.Text = $"Your {active.DisplayName} loadout is ready for this server";
                DetailReady.Visibility = Visibility.Visible;
            }
        });
    }

    private async void RenderMapArtwork(string map)
    {
        if (currentMap == map)
            return;
        currentMap = map;
        string tone = ActualTheme == ElementTheme.Light ? "light" : "dark";
        DetailArt.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/hero-{tone}.svg"));
        if (await previews.GetAsync(map) is { } image && currentMap == map)
            DetailArt.Source = image;
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

    private async void OnFavorite(object sender, RoutedEventArgs args)
    {
        if (ServerList.SelectedItem is ServerRow row)
            await ToggleFavoriteAsync(row);
    }

    private async void OnRowFavorite(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: ServerRow row })
            await ToggleFavoriteAsync(row);
    }

    private Task ToggleFavoriteAsync(ServerRow row) => shell.RunAsync(async () =>
    {
        await model.FavoriteAsync(row.Server);
        shell.RefreshFavorites();
        Render();
    });

    private async void OnRefresh(object sender, RoutedEventArgs args) => await RefreshAsync();

    internal async Task RefreshAsync()
    {
        if (HostingContent.Visibility == Visibility.Visible)
            return;
        if (refreshing is { } active)
        {
            active.Cancel();
            return;
        }
        await shell.RunAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            refreshing = cancellation;
            UpdateJoinButton();
            RefreshGlyph.Glyph = "\uE71A";
            AutomationProperties.SetName(RefreshButton, "Stop refresh");
            ToolTipService.SetToolTip(RefreshButton, "Stop refresh");
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
                RefreshGlyph.Glyph = "\uE72C";
                AutomationProperties.SetName(RefreshButton, "Refresh server list");
                ToolTipService.SetToolTip(RefreshButton, "Refresh server list (Ctrl+R)");
                Render();
                if (message is not null)
                    RefreshStatus.Text = message;
            }
        });
    }

    private async void OnConnect(object sender, RoutedEventArgs args) => await ConnectAsync();

    private Task ConnectAsync(string? address = null, UsageJoin source = UsageJoin.TypedAddress) =>
        shell.RunAsync(() => ShowConnectAsync(address, source));

    internal Task ConnectLinkAsync(ServerLink link) => ShowConnectAsync(link.Target.Address, UsageJoin.Link, link);

    private async Task ShowConnectAsync(string? address, UsageJoin source, ServerLink? link = null)
    {
        if (dialogOpen)
            return;
        dialogOpen = true;
        try
        {
            var dialog = new ConnectDialog(address, link) { XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Target is { } target)
                await JoinAsync(target, source, link?.Edition);
        }
        finally { dialogOpen = false; }
    }

    private async void OnJoin(object sender, RoutedEventArgs args)
    {
        if (refreshing is not null || ServerList.SelectedItem is not ServerRow row)
            return;
        var source = model.Scope == ServerScope.Favorites ? UsageJoin.Favorite : UsageJoin.List;
        if (row.Server.PasswordRequired)
            await ConnectAsync(row.Server.Address, source);
        else
            await shell.RunAsync(() => JoinAsync(new(row.Server.Address, null), source));
    }

    private async Task JoinAsync(ServerAddress address, UsageJoin source, GameEdition? requestedEdition = null)
    {
        if (model.GameRunning)
        {
            RefreshStatus.Text = "Call of Duty 2 is already running.";
            return;
        }
        var decision = requestedEdition is { } requested
            ? new ServerAdmissionResult(ServerAdmissionAction.Launch, requested,
                session.HasClient(requested) ? null : $"Reinstall CoD2 Unbound to get {ConnectDialog.EditionName(requested)}.")
            : await model.CheckAsync(address);
        if (decision.Problem is { } problem)
        {
            RefreshStatus.Text = problem;
            session.ShowError(decision.Action == ServerAdmissionAction.MissingCod2x ||
                requestedEdition is not null ? UsageProblem.ServerVersion : UsageProblem.Join);
            return;
        }
        if (decision.Action == ServerAdmissionAction.ConfirmVersion)
        {
            string version = ConnectDialog.EditionName(decision.Edition);
            var switchEdition = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "Switch game version?", Content = $"This server runs {version}.",
                PrimaryButtonText = "Launch", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
            };
            if (await switchEdition.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        RefreshStatus.Text = (await model.JoinAsync(address, decision.Edition, source)) switch
        {
            GameStartState.Started => "Launching Call of Duty 2…",
            GameStartState.NeedsSetup or GameStartState.MissingData => "Choose a complete game data folder before launching.",
            _ => "Call of Duty 2 is already running."
        };
        RenderDetails();
    }

    internal void UpdateGameState(GameSessionState state)
    {
        RenderDetails();
        UpdateHostingState();
    }

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
