using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ServersPage : Page
{
    private readonly MainWindow shell;
    private readonly ServersViewModel model;
    private readonly WindowsLauncherSession session;
    private readonly MapPreviews previews;
    private HostingPage? hosting;
    private string? currentMap;
    private CancellationTokenSource? refreshing;
    private readonly ObservableCollection<ServerRow> rows = [];
    private readonly RefreshProgress progress = new();
    private ServerRefreshState? shownRefresh;
    private bool mastersDidNotAnswer;
    private string? filterLabel;
    private ImmutableArray<ServerPlayer> shownRoster = [];
    private bool ready;
    private bool renderingRows;
    private bool compactRows;

    internal ServersPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        this.session = session;
        model = new(session, ServerScope.All);
        previews = new(map => Task.Run(() => session.ReadMapArtwork(map)));
        InitializeComponent();
        AutomationProperties.SetHeadingLevel(EmptyHeading, AutomationHeadingLevel.Level2);
        ServerList.AddHandler(KeyDownEvent, new KeyEventHandler(OnServerListKeyDown), true);
        ServerList.ItemsSource = rows;
        // Moving a server to its sorted position should not animate it as a new arrival.
        ServerList.ItemContainerTransitions = new TransitionCollection();
        ActualThemeChanged += (_, _) => UpdateContrast();
        Loaded += (_, _) =>
        {
            UpdateContrast();
            UpdateRefreshDisplay();
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRefreshRendering;
        Scopes.SelectedItem = Scopes.Items[(int)ServerScope.All];
        ready = true;
        RenderSort();
    }

    internal void Open(ServerScope? scope, string? address, bool refreshOnOpen)
    {
        if (scope is { } target)
            Scopes.SelectedItem = Scopes.Items[(int)target];
        if (address is not null)
            revealedJumpAddress = null;
        Render(address);
        if (ShowsServerBrowser && refreshOnOpen && refreshing is null &&
            session.Browser.ShouldRefreshOnOpen(session.Settings.Working.RefreshServersOnOpen, session.Library))
            _ = RefreshAsync();
    }

    private void Render(string? address = null)
    {
        var selected = ServerList.SelectedItem as ServerRow;
        address ??= selected?.Server.Address;
        var focus = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        var focusedRow = RowFrom(focus);
        var next = model.Visible(shownRefresh?.Servers).DistinctBy(server => server.Address)
            .Select(server => new ServerRow(server, model.IsFavorite(server.Address))).ToArray();
        if (revealedJumpAddress is { } revealed && !next.Any(row => row.Server.Address == revealed))
            next = [new ServerRow(session.ServerForAddress(revealed), model.IsFavorite(revealed)), .. next];
        var updated = new List<ServerRow>();
        renderingRows = true;
        rows.Reconcile(next, row => row.Server.Address, (row, incoming) =>
        {
            if (row.Update(incoming))
                updated.Add(row);
            return row;
        });
        var target = rows.FirstOrDefault(row => row.Server.Address == address) ?? rows.FirstOrDefault();
        if (!ReferenceEquals(ServerList.SelectedItem, target))
            ServerList.SelectedItem = target;
        renderingRows = false;
        foreach (var row in updated)
            if (ServerList.ContainerFromItem(row) is ListViewItem { ContentTemplateRoot: Grid grid })
                ApplyServerRow(grid, assignMenu: false);
        RenderEmpty(rows.Count == 0);
        int count = model.Filters.Count;
        FilterBadge.Visibility = IsVisible(count > 0);
        FilterCount.Text = count.ToString();
        string filters = count == 0 ? "Filters" : $"Filters ({count})";
        if (filterLabel != filters)
        {
            filterLabel = filters;
            AutomationProperties.SetName(FilterButton, filters);
            UpdateToolbarOverflow(ServerToolbar.ActualWidth);
        }
        UpdateRefreshStatus();
        if (!ReferenceEquals(selected, target) || (target is not null && updated.Contains(target)))
            RenderDetails();
        if (focusedRow is not null && !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), focus))
            RestoreRowFocus(rows.Contains(focusedRow) ? focusedRow : target, focus is Button { Name: "RowFavorite" });
    }

    private void RestoreRowFocus(ServerRow? target, bool star)
    {
        if (target is null)
        {
            EmptyAction.Focus(FocusState.Programmatic);
            return;
        }
        var fallback = FocusManager.GetFocusedElement(XamlRoot);
        ServerList.ScrollIntoView(target);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded || !ShowsServerBrowser || !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), fallback) ||
                ServerList.ContainerFromItem(target) is not ListViewItem container)
                return;
            var button = star && container.ContentTemplateRoot is Grid grid ? grid.FindName("RowFavorite") as Button : null;
            ((Control?)button ?? container).Focus(FocusState.Programmatic);
        });
    }

    private string CacheAge(DateTimeOffset date)
    {
        var age = DateTimeOffset.UtcNow - date;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";
        return "from " + LauncherText.StencilTime(date.ToLocalTime());
    }

    private async void OnScopeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int index = sender.Items.IndexOf(sender.SelectedItem);
        if (!ready || index < 0)
            return;
        bool showHosting = index == sender.Items.Count - 1;
        // New server takes Alt+N while Hosting is active in the shared Servers view.
        RecentScope.AccessKey = showHosting ? "" : "N";
        BrowserContent.Visibility = BrowserActions.Visibility = BrowserTools.Visibility = IsVisible(!showHosting);
        HostingLayout.Visibility = HostingActions.Visibility = IsVisible(showHosting);
        ServersContours.Visibility = IsVisible(!showHosting);
        UpdateToolbarOverflow(ServerToolbar.ActualWidth);
        if (showHosting)
        {
            ClearRecent.Visibility = Visibility.Collapsed;
            CompositionTarget.Rendering -= OnRefreshRendering;
            session.Opened(UsagePage.Hosting);
            await shell.ShowProblemAsync(async () =>
            {
                hosting ??= new HostingPage(shell, session, ExportHost, HostingExportStatus);
                if (!ReferenceEquals(HostingContent.Content, hosting))
                    HostingContent.Content = hosting;
                await hosting.RefreshAsync();
            });
            return;
        }
        revealedJumpAddress = null;
        model.Scope = (ServerScope)index;
        UpdateRefreshDisplay();
    }

    private async void OnNewHostedServer(object sender, RoutedEventArgs args)
    {
        if (hosting is not null)
            await shell.RunAsync(hosting.NewAsync);
    }

    internal void UpdateContrast()
    {
        foreach (var row in rows)
            if (ServerList.ContainerFromItem(row) is ListViewItem { ContentTemplateRoot: Grid grid })
                ApplyServerRow(grid, assignMenu: false);
        shownRoster = default;
        RenderDetails();
    }

    internal Task UpdateHostingStateAsync() => hosting?.UpdateRunStateAsync() ?? Task.CompletedTask;

    private void OnBrowserSizeChanged(object sender, SizeChangedEventArgs args)
    {
        DetailArtwork.Height = args.NewSize.Height < 830 ? 106 : 150;
    }

    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs args) => UpdateToolbarOverflow(args.NewSize.Width);

    private void UpdateToolbarOverflow(double width)
    {
        // Budgets are in the page toolbar space. Shrink search before either trailing group leaves.
        const double minimumSearch = 160;
        const double allCommands = 822;
        const double statusAndOverflow = 686;
        const double overflowOnly = 522;
        double badge = FilterBadge.Visibility == Visibility.Visible ? 22 : 0;
        bool hideStatus = width < statusAndOverflow + minimumSearch + badge;
        bool hideConnect = width < allCommands + minimumSearch + badge;
        double otherControls = !hideConnect ? allCommands : !hideStatus ? statusAndOverflow : overflowOnly;
        SearchBox.Width = Math.Clamp(width - otherControls - badge, minimumSearch, 280);
        RefreshPill.Visibility = IsVisible(!hideStatus);
        OverflowCount.Visibility = OverflowRefresh.Visibility = IsVisible(hideStatus);
        ConnectButton.Visibility = IsVisible(!hideConnect);
        ToolbarOverflow.Visibility = IsVisible(hideConnect);
    }

    private void OnServerRowLoaded(object sender, RoutedEventArgs args) => ApplyServerRow((Grid)sender);
    private void OnServerRowChanged(FrameworkElement sender, DataContextChangedEventArgs args) => ApplyServerRow((Grid)sender);

    private int RowColumnSpacing => compactRows ? 8 : 16;
    private GridLength RowMapWidth => new(compactRows ? 64 : 80);
    private GridLength RowPlayersWidth => new(compactRows ? 48 : 80);
    private GridLength RowPingWidth => new(compactRows ? 72 : 84);

    private void OnServerListSizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Protect the name column when the details panel and sidebar leave a narrow list.
        bool compact = args.NewSize.Width is > 0 and < 550;
        if (compact == compactRows)
            return;
        compactRows = compact;
        ColumnHeaders.ColumnSpacing = RowColumnSpacing;
        ColumnHeaders.ColumnDefinitions[0].Width = RowMapWidth;
        ColumnHeaders.ColumnDefinitions[2].Width = RowPlayersWidth;
        ColumnHeaders.ColumnDefinitions[3].Width = RowPingWidth;
        foreach (var entry in rows)
            if (ServerList.ContainerFromItem(entry) is ListViewItem { ContentTemplateRoot: Grid row })
                ApplyServerRowLayout(row);
    }

    private void ApplyServerRowLayout(Grid row)
    {
        if (row.FindName("RowIdentity") is not Grid identity)
            return;
        row.ColumnSpacing = identity.ColumnSpacing = RowColumnSpacing;
        identity.ColumnDefinitions[0].Width = RowMapWidth;
        row.ColumnDefinitions[1].Width = RowPlayersWidth;
        row.ColumnDefinitions[2].Width = RowPingWidth;
    }

    private void ApplyServerRow(Grid row, bool assignMenu = true)
    {
        if (row.DataContext is not ServerRow entry || row.FindName("RowName") is not TextBlock name)
            return;
        ApplyServerRowLayout(row);
        var server = entry.Server;
        if (ServerList.ContainerFromItem(entry) is ListViewItem item)
        {
            AutomationProperties.SetName(item, entry.Label);
            if (assignMenu)
                item.ContextFlyout = BuildRowMenu(entry);
        }
        var facts = ServerFacts.From(server);
        bool contrast = shell.IsHighContrast;
        name.Inlines.Clear();
        if (contrast)
            name.Text = QuakeColors.Plain(server.Name);
        else
            AddGameName(name, server.Name, ActualTheme, false);
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
            used.Foreground = ThemeBrush(facts.Occupancy switch
            {
                ServerOccupancy.Full => "NegativeBrush",
                _ when server.PlayerCount == 0 => "TertiaryBrush",
                _ => "numeral"
            }, ActualTheme, false);
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
        // The numeric ping uses the list item's native contrast foreground.
        // Keep it as the signal indicator while Windows controls the palette.
        signal.Visibility = contrast ? Visibility.Collapsed : Visibility.Visible;
        if (!contrast)
        {
            int level = server.Ping < 0 ? 0 : facts.PingLevel;
            for (int bar = 1; bar <= 4; bar++)
            {
                signal.Children.Add(new Rectangle
                {
                    Width = 3,
                    Height = 2 + bar * 2,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Fill = ThemeBrush(bar > level ? "ControlStrokeBrush" : level < 3 ? "caution" : "PositiveBrush", ActualTheme, false)
                });
            }
        }
        SetFavorite((Button)row.FindName("RowFavorite")!, server, (TextBlock)row.FindName("RowPing")!);
        var artwork = (Image)row.FindName("RowArt")!;
        if (!Equals(artwork.Tag, server.Map))
            _ = previews.FillAsync(artwork, server.Map);
    }

    private void SetFavorite(Button button, GameServer server, TextBlock? label = null)
    {
        bool favorite = model.IsFavorite(server.Address);
        string symbol = favorite ? "StarFilled" : "Star";
        if (button.Content is not FontIcon previous || previous.Glyph != (string)Application.Current.Resources["Fluent" + symbol])
        {
            var icon = Fluent(symbol, 16, filled: favorite);
            button.Content = icon;
            if (label is not null)
                GlyphAlign.WatchBeside(icon, label);
        }
        if (shell.IsHighContrast)
            button.ClearValue(Control.ForegroundProperty);
        else
            button.Foreground = ThemeBrush(favorite ? "FavoriteBrush" : "CaptionBrush", ActualTheme, false);
        string action = favorite ? "Remove from favorites" : "Add to favorites";
        AutomationProperties.SetName(button, ReferenceEquals(button, FavoriteButton) ? action : $"{action}: {QuakeColors.Plain(server.Name)}");
        ToolTipService.SetToolTip(button, action);
    }

    private void OnSearch(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!ready)
            return;
        revealedJumpAddress = null;
        model.Search = SearchBox.Text;
        UpdateClearFilters();
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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!renderingRows)
            RenderDetails();
    }

    private void RenderDetails()
    {
        if (ServerList.SelectedItem is not ServerRow row)
        {
            ClearReadyCheck();
            SelectedDetails.Visibility = Visibility.Collapsed;
            NoDetails.Visibility = Visibility.Visible;
            return;
        }
        SelectedDetails.Visibility = Visibility.Visible;
        NoDetails.Visibility = Visibility.Collapsed;
        var server = row.Server;
        var facts = ServerFacts.From(server);
        DetailName.Inlines.Clear();
        AddGameName(DetailName, server.Name, ActualTheme, shell.IsHighContrast);
        DetailMap.Text = (row.Map + " · " + GameModes.Long(server.GameType)).ToUpperInvariant();
        RenderMapArtwork(server.Map);
        DetailAddress.Text = server.Address;
        DetailVersion.Text = facts.VersionLabel;
        DetailMod.Text = string.IsNullOrWhiteSpace(server.Mod) ? "None" : server.Mod;
        DetailAccess.Text = server.PasswordRequired ? "Password" : "Open";
        DetailFull.Visibility = IsVisible(facts.Occupancy == ServerOccupancy.Full);
        DetailRoster.Text = server.MaxPlayers > 0 ? $"ROSTER · {server.PlayerCount} of {server.MaxPlayers}" : "ROSTER";
        if (shownRoster.IsDefault || !shownRoster.SequenceEqual(server.Players))
        {
            shownRoster = server.Players;
            Roster.Children.Clear();
            foreach (var player in server.Players.OrderByDescending(player => player.Score).Take(32))
            {
                var columns = Columns(8, new GridLength(1, GridUnitType.Star), new GridLength(40), new GridLength(40));
                columns.MinHeight = 26;
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
        }
        SetFavorite(FavoriteButton, server);
        UpdateReadyCheck(server);
        UpdateJoinButton();
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
        if (!ShowsServerBrowser)
            return;
        if (refreshing is { } active)
        {
            active.Cancel();
            return;
        }
        using var cancellation = new CancellationTokenSource();
        refreshing = cancellation;
        UpdateRefreshDisplay();
        shell.ClearProblem();
        try
        {
            await shell.ShowProblemAsync(() => model.RefreshAsync(progress, cancellation.Token));
        }
        finally
        {
            var completed = progress.TakeLatest() ?? shownRefresh;
            if (!cancellation.IsCancellationRequested && completed is { Refreshing: false })
                mastersDidNotAnswer = completed.MastersDidNotAnswer;
            refreshing = null;
            shownRefresh = null;
            UpdateRefreshDisplay();
        }
    }

    internal void StopRefresh()
    {
        CompositionTarget.Rendering -= OnRefreshRendering;
        refreshing?.Cancel();
    }

    private void UpdateRefreshDisplay()
    {
        CompositionTarget.Rendering -= OnRefreshRendering;
        if (!IsLoaded || !ShowsServerBrowser)
            return;
        bool active = refreshing is not null;
        if (active)
        {
            shownRefresh = progress.TakeLatest() ?? shownRefresh;
            CompositionTarget.Rendering += OnRefreshRendering;
        }
        RefreshGlyph.Glyph = (string)Application.Current.Resources[active ? "FluentStop" : "FluentRefresh"];
        AutomationProperties.SetName(RefreshButton, active ? "Stop refresh" : "Refresh server list");
        ToolTipService.SetToolTip(RefreshButton, active ? "Stop refreshing" : "Refresh server list (Ctrl+R)");
        OverflowRefresh.Text = active ? "Stop Refreshing" : "Refresh Server List";
        Render();
        UpdateJoinButton();
    }

    private void OnRefreshRendering(object? sender, object args)
    {
        if (ShowsServerBrowser && progress.TakeLatest() is { } state)
        {
            shownRefresh = state;
            Render();
        }
    }

    private void UpdateRefreshStatus()
    {
        bool active = refreshing is not null;
        var state = shownRefresh;
        int total = model.ScopedCount(state?.Servers);
        bool filtered = model.Filters.Count > 0 || !string.IsNullOrWhiteSpace(model.Search);
        string noun = total == 1 ? "server" : "servers";
        string status = active ? state is { Asked: > 0 } ? $"{state.Answered} of {state.Asked} answered" : "Asking for the list…"
            : filtered ? $"{rows.Count} of {total} {noun}"
            : model.CachedAt is { } date ? $"{total} {noun} · {CacheAge(date)}"
            : total == 0 ? "Not refreshed yet" : $"{total} {noun}";
        RefreshStatus.Text = status;
        OverflowCount.Text = status;
        AutomationProperties.SetHelpText(ToolbarOverflow, status);
        RefreshBanner.Visibility = IsVisible(active);
        if (active)
        {
            RefreshMessage.Text = state is { Asked: > 0 }
                ? $"Checked {state.Finished} of {state.Asked} servers. {state.Answered} answered."
                : "Asking the master servers for the list…";
            RefreshProgressBar.IsIndeterminate = state is not { Asked: > 0 };
            RefreshProgressBar.Value = state is { Asked: > 0 } ? 100d * state.Finished / state.Asked : 0;
        }
        OfflineBanner.IsOpen = !active && mastersDidNotAnswer;
        OfflineBanner.Visibility = IsVisible(OfflineBanner.IsOpen);
        if (OfflineBanner.IsOpen)
            OfflineBanner.Message = model.CachedAt is { } cached
                ? $"The master servers didn’t answer. You’re seeing the list from {LauncherText.StencilTime(cached.ToLocalTime())}."
                : "The master servers didn’t answer. Check your internet connection, then try again.";
        int filters = model.Filters.Count;
        FilterSummary.Text = (filters == 0 ? "No filters on" : $"{filters} {(filters == 1 ? "filter" : "filters")} on")
            + $" · {rows.Count} of {total} {noun} shown";
    }

    private sealed class RefreshProgress : IProgress<ServerRefreshState>
    {
        private ServerRefreshState? latest;
        public void Report(ServerRefreshState value) => Interlocked.Exchange(ref latest, value);
        internal ServerRefreshState? TakeLatest() => Interlocked.Exchange(ref latest, null);
    }

    private async void OnConnect(object sender, RoutedEventArgs args) => await ConnectAsync();

    private Task ConnectAsync() => shell.RunAsync(() => ShowConnectAsync());

    internal Task ConnectLinkAsync(ServerLink link) => ShowConnectAsync(link);

    private async Task ShowConnectAsync(ServerLink? link = null)
    {
        var dialog = new ConnectDialog(session.ServerQuery, session.Library, shell.IsHighContrast, link)
        {
            XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Target is not { } target)
            return;
        if (dialog.AddFavorite)
        {
            await session.AddFavoriteAsync(target.Address);
            shell.RefreshFavorites();
        }
        if (dialog.Observation is { } server)
            await session.RememberServerAsync(server);
        var source = dialog.RequestedEdition is null ? UsageJoin.TypedAddress : UsageJoin.Link;
        string? status = await shell.JoinServerAsync(target, source, dialog.RequestedEdition, dialog.Observation, alreadyLookedUp: true);
        Render(target.Address);
        if (status is not null)
            RefreshStatus.Text = status;
    }

    private async void OnJoin(object sender, RoutedEventArgs args)
    {
        if (ServerList.SelectedItem is ServerRow row)
            await LaunchRowAsync(row);
    }

    internal Task UpdateGameStateAsync()
    {
        RenderDetails();
        return UpdateHostingStateAsync();
    }

}
