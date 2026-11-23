using System.Collections.Immutable;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
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
    private bool ready;
    private bool renderingRows;
    private ImmutableArray<GameServer>? liveRows;

    internal ServersPage(MainWindow shell, WindowsLauncherSession session, ServerScope scope, string? address)
    {
        this.shell = shell;
        this.session = session;
        model = new(session, scope);
        previews = new(map => Task.Run(() => session.ReadMapArtwork(map)));
        InitializeComponent();
        AutomationProperties.SetHeadingLevel(EmptyHeading, AutomationHeadingLevel.Level2);
        ServerList.AddHandler(KeyDownEvent, new KeyEventHandler(OnServerListKeyDown), true);
        ActualThemeChanged += (_, _) => Render();
        Scopes.SelectedItem = Scopes.Items[(int)scope];
        ready = true;
        RenderSort();
        Render(address);
    }

    private void Render(string? address = null)
    {
        address ??= (ServerList.SelectedItem as ServerRow)?.Server.Address;
        var rows = model.Visible(liveRows).Select(server => new ServerRow(server, model.IsFavorite(server.Address))).ToArray();
        if (revealedJumpAddress is { } revealed && !rows.Any(row => row.Server.Address == revealed))
            rows = [new ServerRow(session.ServerForAddress(revealed), model.IsFavorite(revealed)), .. rows];
        renderingRows = true;
        ServerList.ItemsSource = rows;
        ServerList.SelectedItem = rows.FirstOrDefault(row => row.Server.Address == address) ?? rows.FirstOrDefault();
        renderingRows = false;
        RenderEmpty(rows.Length == 0);
        int count = model.Filters.Count;
        string filters = count == 0 ? "Filters" : $"Filters ({count})";
        FilterButton.Content = IconLabel("Filter", filters);
        AutomationProperties.SetName(FilterButton, filters);
        if (refreshing is null)
            RefreshStatus.Text = WithCacheAge(rows.Length == 0 ? EmptyState() : $"{rows.Length} {(rows.Length == 1 ? "server" : "servers")}");
        RenderDetails();
    }

    private string WithCacheAge(string result)
    {
        if (model.CachedAt is not { } date)
            return result;
        var age = DateTimeOffset.UtcNow - date;
        if (age < TimeSpan.FromMinutes(1))
            return result + " · List updated just now";
        if (age < TimeSpan.FromHours(1))
            return result + $" · List updated {(int)age.TotalMinutes} min ago";
        return result + " · List updated " + LauncherText.StencilTime(date.ToLocalTime());
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
            ClearRecent.Visibility = Visibility.Collapsed;
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
        revealedJumpAddress = null;
        model.Scope = (ServerScope)index;
        Render();
    }

    private async void OnNewHostedServer(object sender, RoutedEventArgs args)
    {
        if (hosting is not null)
            await hosting.NewAsync();
    }

    internal void UpdateContrast() => Render();

    internal Task UpdateHostingStateAsync() => hosting?.UpdateRunStateAsync() ?? Task.CompletedTask;

    private void OnBrowserSizeChanged(object sender, SizeChangedEventArgs args) =>
        Details.Width = args.NewSize.Width >= 1000 ? 380 : Math.Clamp(args.NewSize.Width * 0.44, 225, 340);

    private void OnSelectionPillLoaded(object sender, RoutedEventArgs args) => TrackSelectionPill((Border)sender);

    private void OnServerRowLoaded(object sender, RoutedEventArgs args) => ApplyServerRow((Grid)sender);
    private void OnServerRowChanged(FrameworkElement sender, DataContextChangedEventArgs args) => ApplyServerRow((Grid)sender);

    private void ApplyServerRow(Grid row)
    {
        if (row.DataContext is not ServerRow entry || row.FindName("RowName") is not TextBlock name)
            return;
        var server = entry.Server;
        if (ContainingListItem(row) is { } item)
        {
            AutomationProperties.SetName(item, entry.Label);
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
                    Fill = ThemeBrush(bar <= level ? "PositiveBrush" : "ControlStrokeBrush", ActualTheme, false)
                });
            }
        }
        SetFavorite((Button)row.FindName("RowFavorite")!, server);
        _ = previews.FillAsync((Image)row.FindName("RowArt")!, server.Map);
    }

    private void SetFavorite(Button button, GameServer server)
    {
        bool favorite = model.IsFavorite(server.Address);
        button.Content = Fluent(favorite ? "StarFilled" : "Star", 16, filled: favorite);
        button.Foreground = ThemeBrush(favorite ? "FavoriteBrush" : "CaptionBrush", ActualTheme, shell.IsHighContrast);
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
        DetailFull.Visibility = IsVisible(facts.Occupancy == ServerOccupancy.Full);
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
            RefreshGlyph.Glyph = (string)Application.Current.Resources["FluentStop"];
            AutomationProperties.SetName(RefreshButton, "Stop refresh");
            ToolTipService.SetToolTip(RefreshButton, "Stop refresh");
            string? message = null;
            try
            {
                var progress = new Progress<ServerRefreshState>(state =>
                {
                    if (refreshing != cancellation)
                        return;
                    liveRows = state.Servers;
                    Render();
                    if (state.Refreshing)
                        RefreshStatus.Text = WithCacheAge(state.Asked == 0 ? "Asking the master servers for the list…" :
                            $"Checking {state.Asked} servers. {state.Finished} checked, {state.Answered} answered.");
                    else
                        message = state.Message;
                });
                await model.RefreshAsync(progress, cancellation.Token);
            }
            finally
            {
                refreshing = null;
                liveRows = null;
                RefreshGlyph.Glyph = (string)Application.Current.Resources["FluentRefresh"];
                AutomationProperties.SetName(RefreshButton, "Refresh server list");
                ToolTipService.SetToolTip(RefreshButton, "Refresh server list (Ctrl+R)");
                Render();
                if (message is not null)
                    RefreshStatus.Text = WithCacheAge(message);
            }
        });
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
