using System.Collections.Immutable;
using System.Globalization;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private readonly MapPreviews setupArt;
    private string discoverQuery = "";
    private string savedQuery = "";
    private string? discoverGroup;
    private ImmutableArray<ConfigDirectoryEntry> groupEntries = [];
    private int featuredIndex;
    private double catalogueDpi;
    private readonly SemaphoreSlim shelfFetches = new(3);
    private readonly Dictionary<(string Id, string Hash, double Dpi), Task<double?>> shelfDistances = [];

    private async Task ShowCatalogueAsync(bool saved, bool retry = false)
    {
        refreshSharing = null;
        int expectedPlace = saved ? 1 : 0;
        if (!retry)
            model.Opened(saved ? UsagePage.Saved : UsagePage.Setups);
        Status.IsOpen = false;
        Loadouts.SelectedItem = null;
        Detail.Children.Clear();
        ResetDetailScroll();
        var title = Heading(saved ? "Saved" : "Player Setups");
        var subtitle = Caption("");
        subtitle.Visibility = Visibility.Collapsed;
        var identity = new StackPanel { Spacing = 4, Children = { title, subtitle } };
        var shareHeader = saved ? null : PlayerSetupsHeading(identity);
        var back = new Button { Style = StyleFor("QuietButton"), Content = IconLabel("ChevronLeft", "Player Setups", 14),
            Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(back, "Back to Player Setups");
        Detail.Children.Add(back);
        Detail.Children.Add(shareHeader is null ? identity : shareHeader);
        var search = CatalogueSearch(saved ? savedQuery : discoverQuery);
        var count = Caption("Getting the catalogue…");
        count.VerticalAlignment = VerticalAlignment.Center;
        count.TextWrapping = TextWrapping.NoWrap;
        count.TextTrimming = TextTrimming.CharacterEllipsis;
        var refresh = new Button { Content = Fluent("Refresh", 20), Style = StyleFor("IconAction"), IsEnabled = false };
        AutomationProperties.SetName(refresh, "Refresh player setups");
        ToolTipService.SetToolTip(refresh, "Refresh player setups");
        var counts = Buttons(count, refresh);
        var statusPill = new Border { Style = StyleFor("ToolbarSurface"), Padding = new Thickness(14, 0, 4, 0),
            Child = counts };
        ShowCatalogueToolbar(search, statusPill);

        var results = new StackPanel { Spacing = 20 };
        var loading = new StackPanel { Spacing = 20 };
        loading.Children.Add(Progress("Getting the catalogue…"));
        var skeleton = CheckCard(new Grid());
        skeleton.Height = 230;
        AutomationProperties.SetAccessibilityView(skeleton, AccessibilityView.Raw);
        loading.Children.Add(skeleton);
        Detail.Children.Add(loading);
        Detail.Children.Add(results);
        await model.Discover.LoadAsync(retry);
        if (DiscoverPlaces.SelectedIndex != expectedPlace || !Detail.Children.Contains(results))
            return;
        model.RememberVerifiedCatalogue();
        UpdateSavedCount();
        Detail.Children.Remove(loading);
        refresh.IsEnabled = true;
        if (model.Discover.State.Snapshot is null)
        {
            var unavailable = model.Discover.State;
            model.Error(UsageProblem.Directory);
            count.Text = "Catalogue unavailable";
            bool retryable = unavailable.Phase != ConfigDiscoverPhase.Unavailable;
            refresh.IsEnabled = retryable;
            refresh.Click += async (_, _) => await shell.ShowProblemAsync(() => ShowCatalogueAsync(saved, retry: true));
            var stateCard = EmptyState("PeopleCommunity",
                retryable ? "The catalogue couldn’t be reached" : "No player setups in this build",
                "Player setups come from the catalogue on GitHub. Your own loadouts, Add a Config and Check a Config still work.");
            if (retryable)
                stateCard.Children.Add(Action("Try Again", () => ShowCatalogueAsync(saved, retry: true)));
            results.Children.Add(stateCard);
            AddCataloguePromos(results);
            AddCatalogueFooter(results);
            return;
        }

        var state = model.Discover.State;
        catalogueDpi = model.DpiFor(model.Studio.ReadActive());
        var entries = saved ? state.Saved.Select(item => item.Entry).OfType<ConfigDirectoryEntry>().ToImmutableArray() : model.Discover.Entries;
        count.Text = CatalogueCount(entries.Length, state.Fetched);
        var refreshStatus = CheckNotice(InfoBarSeverity.Informational, "", "");
        var retryAction = new Button { Content = "Try Again" };
        refreshStatus.ActionButton = retryAction;
        refreshStatus.Visibility = IsVisible(!saved && state.Problem is not null);
        refreshStatus.Message = OfflineNotice(state.Fetched);
        Detail.Children.Insert(2, refreshStatus);
        ConfigDirectoryPopularity? figures = model.Discover.Popularity is { IsCompletedSuccessfully: true } ready
            ? ready.Result : null;
        Func<ImmutableArray<ConfigDirectoryEntry>, ImmutableArray<ConfigDirectoryEntry>>? groupFilter = null;
        bool checking = false;
        back.Click += (_, _) =>
        {
            discoverGroup = null;
            discoverQuery = "";
            search.Text = "";
            Fill();
            ResetDetailScroll();
        };
        refresh.Click += async (_, _) => await shell.ShowProblemAsync(RefreshCatalogueAsync);
        retryAction.Click += async (_, _) => await shell.ShowProblemAsync(RefreshCatalogueAsync);
        search.TextChanged += (_, args) =>
        {
            if (saved)
                savedQuery = search.Text;
            else
            {
                discoverQuery = search.Text;
                discoverGroup = null;
            }
            Fill();
        };
        Fill();
        if (!saved && model.Discover.Popularity is { IsCompleted: false } pending)
            _ = ShowFiguresAsync(pending);

        async Task ShowFiguresAsync(Task<ConfigDirectoryPopularity?> pending)
        {
            var latest = await pending;
            if (!ReferenceEquals(model.Discover.Popularity, pending))
                return;
            bool changed = latest?.PeriodDays != figures?.PeriodDays ||
                (latest is not null && figures is not null &&
                    groupEntries.Any(entry => latest.Setups.GetValueOrDefault(entry.Id) != figures.Setups.GetValueOrDefault(entry.Id)));
            figures = latest;
            if (changed && Detail.Children.Contains(results) && discoverGroup is not null)
                Fill();
        }

        async Task RefreshCatalogueAsync()
        {
            if (checking)
                return;
            checking = true;
            string previousCount = count.Text;
            count.Text = "Checking…";
            refresh.IsEnabled = retryAction.IsEnabled = false;
            try
            {
                await model.Discover.LoadAsync(retry: true);
                model.RememberVerifiedCatalogue();
                if (!IsLoaded || !Detail.Children.Contains(results))
                    return;
                UpdateSavedCount();
                var next = model.Discover.State;
                if (next.Snapshot is null)
                {
                    model.Error(UsageProblem.Directory);
                    refreshStatus.Visibility = Visibility.Visible;
                    return;
                }
                refreshStatus.Visibility = IsVisible(!saved && next.Problem is not null);
                refreshStatus.Message = OfflineNotice(next.Fetched);
                if (next.Snapshot.Index.Revision != state.Snapshot?.Index.Revision)
                {
                    entries = saved ? next.Saved.Select(item => item.Entry).OfType<ConfigDirectoryEntry>().ToImmutableArray()
                        : next.Snapshot.Index.Entries;
                    if (discoverGroup is not null && groupFilter is not null)
                        groupEntries = groupFilter(entries);
                    shelfDistances.Clear();
                    state = next;
                    Fill();
                }
                count.Text = CatalogueCount(entries.Length, next.Fetched);
                if (!saved && discoverGroup is not null && model.Discover.Popularity is { } pending)
                    _ = ShowFiguresAsync(pending);
            }
            finally
            {
                checking = false;
                refresh.IsEnabled = retryAction.IsEnabled = true;
                if (count.Text == "Checking…")
                    count.Text = previousCount;
            }
        }

        void Fill()
        {
            results.Children.Clear();
            string query = search.Text.Trim();
            bool browsing = !saved && (discoverGroup is not null || query.Length != 0);
            back.Visibility = IsVisible(browsing);
            if (shareHeader?.Children[1] is FrameworkElement share)
                share.Visibility = IsVisible(!browsing);
            subtitle.Visibility = IsVisible(browsing || saved && !entries.IsEmpty);
            if (saved)
                subtitle.Text = SetupCount(entries.Length);
            else if (!browsing)
                title.Text = "Player Setups";

            if (!saved && discoverGroup is { } group)
            {
                title.Text = group;
                var caption = subtitle;
                caption.VerticalAlignment = VerticalAlignment.Bottom;
                caption.TextWrapping = TextWrapping.Wrap;
                var gridHeading = Columns(12, new GridLength(1, GridUnitType.Star), GridLength.Auto);
                var filterChoices = ConfigDiscover.StyleShelves(entries, CultureInfo.CurrentCulture);
                var filters = filterChoices.Select(shelf => shelf.Filter).Prepend(ConfigSetupFilter.All).ToArray();
                if (filters.Length > 1)
                {
                    string[] names = filters.Select(StyleName).ToArray();
                    int selected = Math.Max(0, Array.IndexOf(names, group));
                    Put(gridHeading, 0, Tabs(names, selected, index =>
                        OpenGroup(names[index], current => ConfigDiscover.Setups(current, filters[index], "", CultureInfo.CurrentCulture))));
                }
                else
                    Put(gridHeading, 0, new Border());
                results.Children.Add(gridHeading);
                var cards = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
                results.Children.Add(cards);
                var orders = ConfigDiscover.AvailableOrders(figures);
                var order = orders.Contains(shell.SetupOrder) ? shell.SetupOrder : ConfigSetupOrder.FeaturedFirst;
                if (orders.Length > 1)
                {
                    var sort = new ComboBox { MinWidth = 152 };
                    var label = new TextBlock { Text = "Order", VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetLabeledBy(sort, label);
                    AutomationProperties.SetName(sort, "Sort by");
                    AutomationProperties.SetAutomationId(sort, "PlayerSetupsSort");
                    ToolTipService.SetToolTip(sort, "Sort the setups");
                    sort.ItemsSource = orders.Select(value => value switch
                    {
                        ConfigSetupOrder.MostImported => "Most imported",
                        ConfigSetupOrder.MostViewed => "Most viewed",
                        _ => "Featured first"
                    }).ToArray();
                    sort.SelectedIndex = orders.IndexOf(order);
                    UpdateSortStatus();
                    sort.SelectionChanged += (_, _) =>
                    {
                        if (sort.SelectedIndex < 0)
                            return;
                        shell.SetupOrder = order = orders[sort.SelectedIndex];
                        UpdateSortStatus();
                        FillGrid();
                    };
                    var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Bottom, Children = { label, sort } };
                    Put(gridHeading, 1, sortRow);
                    MoveBelowWhenNarrow(gridHeading, sortRow, 720);

                    void UpdateSortStatus() =>
                        AutomationProperties.SetItemStatus(sort, (string)sort.SelectedItem);
                }
                FillGrid();

                void FillGrid()
                {
                    bool ranked = order != ConfigSetupOrder.FeaturedFirst && figures is not null;
                    string count = SetupCount(entries.Length);
                    if (groupEntries.Length != entries.Length)
                        count = $"{groupEntries.Length} of {count}";
                    caption.Text = ranked
                        ? $"{count}, the most {(order == ConfigSetupOrder.MostImported ? "imported" : "viewed")} in the last {figures!.Period} first"
                        : SetupCount(groupEntries.Length);
                    var ordered = ConfigDiscover.OrderSetups(groupEntries, order, figures, CultureInfo.CurrentCulture);
                    cards.Content = SetupGrid(ordered, inSaved: false, ranked ? FigureLine : null);
                }

                string FigureLine(ConfigDirectoryEntry entry)
                {
                    long? count = figures?.Setups.GetValueOrDefault(entry.Id)?.Figure(order);
                    return count is null ? $"As published by {entry.Provenance.PublishedBy}"
                        : $"{(order == ConfigSetupOrder.MostImported ? "Imported" : "Viewed")} {count} times in the last {figures!.Period}";
                }
                AddCatalogueFooter(results);
                return;
            }

            if (saved || query.Length > 0)
            {
                var matching = ConfigDiscover.Setups(entries, ConfigSetupFilter.All, query, CultureInfo.CurrentCulture);
                if (!saved)
                {
                    title.Text = "Results";
                    subtitle.Text = matching.IsEmpty ? $"None of {SetupCount(entries.Length)}" :
                        $"{matching.Length} of {SetupCount(entries.Length)} {(matching.Length == 1 ? "matches" : "match")} “{query}”";
                }
                if (!matching.IsEmpty)
                    results.Children.Add(SetupGrid(matching, saved));
                else
                {
                    var empty = EmptyState(query.Length > 0 ? "Search" : "Bookmark",
                        saved && entries.IsEmpty ? "Nothing saved yet" : $"No setup matches “{query}”",
                        saved && entries.IsEmpty ? "Setups you save for later appear here." :
                            $"Try a team, an era or where it was published. Search looks through {SetupCount(entries.Length)}.");
                    empty.Children.Add(query.Length > 0
                        ? SmallButton("Clear Search", () => search.Text = "")
                        : SmallButton("Browse Player Setups", () => DiscoverPlaces.SelectedIndex = 0));
                    results.Children.Add(empty);
                }
                if (saved && query.Length == 0)
                    foreach (var missing in state.Saved.Where(item => item.Entry is null))
                        results.Children.Add(SettingRow("Unavailable setup", null,
                            Action("Remove from Saved", async () =>
                            {
                                await model.Discover.SetSavedAsync(missing.Id, false);
                                await ShowCatalogueAsync(saved: true);
                            })));
                if (!saved)
                    AddCatalogueFooter(results);
                return;
            }

            var heroes = ConfigDiscover.Heroes(entries);
            if (!heroes.IsEmpty)
                results.Children.Add(FeaturedHero(heroes));

            var shelves = ConfigDiscover.StyleShelves(entries, CultureInfo.CurrentCulture);
            foreach (var shelf in shelves)
            {
                string heading = StyleName(shelf.Filter);
                string note = shelf.Filter switch
                {
                    ConfigSetupFilter.Classic => "From the game’s first years, around 2005 to 2008.",
                    ConfigSetupFilter.Cod2x => "Made for CoD2x.",
                    _ => "From the game’s later years."
                };
                results.Children.Add(ShelfHeading(heading, note, $"See All {shelf.Total}",
                    () => OpenGroup(heading, current => ConfigDiscover.Setups(current, shelf.Filter, "", CultureInfo.CurrentCulture))));
                results.Children.Add(SetupShelf(shelf.Entries, saved));
            }

            var teams = ConfigDiscover.Teams(entries, CultureInfo.CurrentCulture);
            if (!teams.IsEmpty)
            {
                results.Children.Add(ShelfHeading("Teams", "Every setup published under a team’s name."));
                var teamCards = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal };
                teamCards.SizeChanged += (_, args) =>
                {
                    double width = args.NewSize.Width;
                    if (width > 0)
                        teamCards.ItemWidth = Math.Floor(width / Math.Max(1, (int)(width / 200)));
                };
                foreach (var team in teams)
                {
                    var contents = Columns(6, new GridLength(1, GridUnitType.Star), GridLength.Auto);
                    Put(contents, 0, new TextBlock { Text = team.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                    Put(contents, 1, new TextBlock { Text = $"· {team.Count}" });
                    var button = new Button { Content = contents, Margin = new Thickness(0, 0, 8, 8),
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    AutomationProperties.SetName(button, $"{team.Name} · {team.Count}");
                    ToolTipService.SetToolTip(button, team.Name);
                    button.Click += (_, _) => OpenGroup(team.Name, current => [.. current.Where(entry => entry.Team == team.Name)]);
                    teamCards.Children.Add(button);
                }
                results.Children.Add(teamCards);
            }

            if (shelves.IsEmpty && entries.Select(entry => entry.Era).Distinct().Count() > 1)
                foreach (var era in ConfigDiscover.EraShelves(entries))
                {
                    results.Children.Add(ShelfHeading(era.Era, SetupCount(era.Entries.Length), $"See all {era.Entries.Length}",
                        () => OpenGroup(era.Era, current => [.. current.Where(entry => entry.Era == era.Era)])));
                    results.Children.Add(SetupShelf(era.Entries.Take(10), saved));
                }

            if (!entries.IsEmpty)
                results.Children.Add(ShelfHeading("All setups", SetupCount(entries.Length), "See All", () =>
                    OpenGroup("All setups", current => current)));
            else
                results.Children.Add(EmptyState("Search", "No setups yet", "The catalogue doesn’t list any player setups yet. You can still bring in a config of your own."));
            AddCataloguePromos(results);
            AddCatalogueFooter(results);
        }

        void OpenGroup(string label, Func<ImmutableArray<ConfigDirectoryEntry>, ImmutableArray<ConfigDirectoryEntry>> filter)
        {
            discoverGroup = label;
            groupFilter = filter;
            var ids = filter(entries).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            groupEntries = [.. entries.Where(entry => ids.Contains(entry.Id))];
            Fill();
            ResetDetailScroll();
        }

    }

    private static string StyleName(ConfigSetupFilter style) => style switch
    {
        ConfigSetupFilter.Classic => "Classic era",
        ConfigSetupFilter.Cod2x => "CoD2x era",
        ConfigSetupFilter.Modern => "Modern",
        _ => "All setups"
    };

    private static string CatalogueCount(int count, DateTimeOffset? date) => SetupCount(count) +
        (date is { } fetched ? $" · updated {CatalogueDate(fetched)}" : "");

    private static string OfflineNotice(DateTimeOffset? date) => "Couldn’t reach the catalogue just now. Showing the copy saved" +
        (date is { } saved ? $" on {saved.LocalDateTime:d MMMM}." : " on this PC.");

    private AutoSuggestBox CatalogueSearch(string query)
    {
        var search = new AutoSuggestBox
        {
            PlaceholderText = "Search players, teams or styles", Text = query,
            AccessKey = "F", QueryIcon = Fluent("Search", 20), VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(search, search.PlaceholderText);
        catalogueFind = search;
        return search;
    }

    private void ShowCatalogueToolbar(AutoSuggestBox search, FrameworkElement? status)
    {
        ConfigToolbar.Children.Clear();
        var tools = Buttons(search);
        tools.HorizontalAlignment = HorizontalAlignment.Right;
        if (status is not null)
            tools.Children.Add(status);
        SizeChangedEventHandler resize = (_, _) => Fit();
        ConfigToolbar.SizeChanged += resize;
        tools.Unloaded += (_, _) => ConfigToolbar.SizeChanged -= resize;
        Fit();
        Put(ConfigToolbar, 1, tools);

        void Fit()
        {
            double available = ConfigToolbar.ActualWidth;
            search.Width = Math.Clamp(available - (status is null ? 0 : 228), 160, 280);
            if (status is Border { Child: StackPanel row } && row.Children[0] is TextBlock count)
                count.MaxWidth = available < 540 ? 106 : 168;
        }
    }

    private void AddCataloguePromos(Panel parent)
    {
        var addButton = new Button { Content = "Add a Config", IsEnabled = CanEdit };
        addButton.Flyout = MakeAddConfigMenu();
        var checkButton = SmallButton("Check a Config", () => DiscoverPlaces.SelectedIndex = 2);
        var add = Promo("ArrowDownload", "Your own config", "Bring the config you already play with",
            "From a file, a link, your Call of Duty 2 install or the clipboard. You review every line before it becomes a loadout.", addButton);
        var check = Promo("ShieldCheckmark", "Before a match", "Check a config against a server’s rules",
            "See which lines the rules would refuse and fix them, without adding anything to your loadouts.", checkButton);
        parent.Children.Add(CheckPair(add, check, 700));

        static Border Promo(string icon, string eyebrow, string title, string description, Button action)
        {
            var words = new StackPanel { Spacing = 8, Children =
                { IconLabel(icon, eyebrow, 18), PanelHeading(title), Body(description), action } };
            var card = CheckCard(words);
            card.VerticalAlignment = VerticalAlignment.Stretch;
            return card;
        }
    }

    private static void AddCatalogueFooter(Panel parent)
    {
        parent.Children.Add(Caption("Setups are collected from public pages and credited to where they were published. Passwords and personal details are removed."));
        parent.Children.Add(Buttons(
            new HyperlinkButton { Content = "Catalogue on GitHub", NavigateUri = ConfigDirectory.RepositoryUrl },
            new HyperlinkButton { Content = "Ask to remove or correct a setup", NavigateUri = ConfigDirectory.RemovalUrl }));
    }

    private Grid FeaturedHero(ImmutableArray<ConfigDirectoryEntry> entries)
    {
        var picture = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            IsTabStop = false
        };
        var featured = new Grid { Children = { picture } };
        featuredIndex = Math.Clamp(featuredIndex, 0, entries.Length - 1);
        bool hasPager = entries.Length > 1;
        ShowFeature();
        if (!hasPager)
            return featured;

        var pager = new PipsPager
        {
            NumberOfPages = entries.Length, MaxVisiblePips = 5, SelectedPageIndex = featuredIndex,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6)
        };
        AutomationProperties.SetName(pager, "Featured setups");
        pager.SelectedIndexChanged += (sender, _) =>
        {
            featuredIndex = sender.SelectedPageIndex;
            ShowFeature();
        };

        var arrows = Buttons(Arrow("ChevronLeft", "Previous featured setup", -1),
            Arrow("ChevronRight", "Next featured setup", 1));
        arrows.HorizontalAlignment = HorizontalAlignment.Right;
        arrows.VerticalAlignment = VerticalAlignment.Top;
        arrows.Margin = new Thickness(12);
        featured.Children.Add(arrows);
        featured.Children.Add(new Border { Style = StyleFor("SetupArtBackplate"), Child = pager,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom });
        return featured;

        Button Arrow(string icon, string label, int offset)
        {
            var button = new Button { Content = Fluent(icon, 16), Width = 32, Height = 32, Padding = new Thickness(0) };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            button.Click += (_, _) => pager.SelectedPageIndex = (featuredIndex + offset + entries.Length) % entries.Length;
            return button;
        }

        void ShowFeature()
        {
            var entry = entries[featuredIndex];
            var view = Action("See this setup", () => ShowSetupAsync(entry, false));
            view.Style = StyleFor("AccentButtonStyle");
            picture.Content = SetupHero(entry, $"Featured setup · {entry.Era}", view,
                Bookmark(entry), featured: true, withPips: hasPager);
        }
    }

    private FrameworkElement SetupShelf(IEnumerable<ConfigDirectoryEntry> entries, bool inSaved)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var entry in entries)
        {
            var card = SetupCard(entry, inSaved);
            card.Width = 248;
            row.Children.Add(card);
        }
        return new ScrollViewer
        {
            Content = row, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private static void MoveBelowWhenNarrow(Grid header, FrameworkElement right, double threshold)
    {
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.SizeChanged += (_, args) =>
        {
            bool narrow = args.NewSize.Width < threshold;
            Grid.SetColumnSpan((FrameworkElement)header.Children[0], narrow ? 2 : 1);
            Grid.SetRow(right, narrow ? 1 : 0);
            Grid.SetColumn(right, narrow ? 0 : 1);
            Grid.SetColumnSpan(right, narrow ? 2 : 1);
            right.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        };
    }

    private FrameworkElement SetupGrid(IEnumerable<ConfigDirectoryEntry> entries, bool inSaved,
        Func<ConfigDirectoryEntry, string>? figureLine = null)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        var cards = entries.Select(entry => SetupCard(entry, inSaved, figureLine?.Invoke(entry))).ToArray();
        foreach (var card in cards)
            grid.Children.Add(card);
        int columns = 0;
        Arrange(Detail.ActualWidth);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return grid;

        void Arrange(double width)
        {
            int next = Math.Max(1, (int)((width + 8) / 248));
            if (next == columns)
                return;
            columns = next;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (int column = 0; column < columns; column++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int index = 0; index < cards.Length; index++)
            {
                if (index % columns == 0)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(cards[index], index / columns);
                Grid.SetColumn(cards[index], index % columns);
            }
        }
    }

    private Button SetupCard(ConfigDirectoryEntry entry, bool inSaved, string? figureLine = null)
    {
        bool wrap = ExtraLargeText;
        var art = Artwork(entry, new CornerRadius(4, 4, 0, 0));
        art.Height = 104;
        art.VerticalAlignment = VerticalAlignment.Top;
        var avatar = Avatar(entry, 40);
        avatar.HorizontalAlignment = HorizontalAlignment.Left;
        avatar.VerticalAlignment = VerticalAlignment.Bottom;
        avatar.Margin = new Thickness(12, 0, 0, 0);
        var top = new Grid { Height = 122, Children = { art, avatar } };
        var mouse = ShelfValue("Mouse speed · per full turn", "", wrap);
        mouse.Visibility = Visibility.Collapsed;
        Grid? columns = wrap ? null : Columns(8, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
        Panel facts = columns is null ? new StackPanel { Spacing = 4 } : columns;
        facts.Children.Add(mouse);
        if (!string.IsNullOrWhiteSpace(entry.Facts?.Resolution))
        {
            var resolution = ShelfValue("Resolution", entry.Facts.Resolution, wrap);
            if (columns is not null)
                Grid.SetColumn(resolution, 1);
            facts.Children.Add(resolution);
        }
        var info = new StackPanel
        {
            Padding = new Thickness(12, 4, 12, 12), Spacing = 4,
            Children = {
                CardText(entry.Player, wrap, "BodyStrongTextBlockStyle"), CardText(Identity(entry), wrap),
                CardText(entry.Feel, wrap, "SecondaryText"), facts
            }
        };
        if (figureLine is not null)
        {
            var figure = CardText(figureLine, wrap, "SetupFactLabel");
            Typography.SetNumeralAlignment(figure, FontNumeralAlignment.Tabular);
            info.Children.Add(figure);
        }
        var open = Action("View setup", () => ShowSetupAsync(entry, inSaved));
        open.Content = new StackPanel { Children = { top, info } };
        open.Style = StyleFor("SetupCard");
        open.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(open, $"View setup: {entry.Player}");
        open.EffectiveViewportChanged += ReadVisibleDistance;
        return open;

        async void ReadVisibleDistance(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            var viewport = args.EffectiveViewport;
            if (viewport.Right <= 0 || viewport.Bottom <= 0 ||
                viewport.Left >= sender.ActualWidth || viewport.Top >= sender.ActualHeight)
                return;
            open.EffectiveViewportChanged -= ReadVisibleDistance;
            if (await ReadShelfDistanceAsync(entry, catalogueDpi) is { } cm)
            {
                ((TextBlock)mouse.Children[0]).Text = $"{cm:F1} cm";
                mouse.Visibility = Visibility.Visible;
            }
        }
    }

    private Task<double?> ReadShelfDistanceAsync(ConfigDirectoryEntry entry, double dpi)
    {
        var key = (entry.Id, entry.Sha256, dpi);
        if (!shelfDistances.TryGetValue(key, out var result))
            shelfDistances[key] = result = FetchAsync();
        return result;

        async Task<double?> FetchAsync()
        {
            await shelfFetches.WaitAsync();
            try
            {
                var review = await model.Discover.ReviewAsync(entry.Id, model.Dialect, active: null);
                return ConfigDiscover.SetupTurnDistance(review, dpi);
            }
            catch (Exception error) when (IsSetupReviewProblem(error) || error is InvalidOperationException)
            {
                return null;
            }
            finally { shelfFetches.Release(); }
        }
    }

    private static StackPanel ShelfValue(string label, string value, bool wrap) => new()
    {
        Spacing = 2,
        Children = {
            new TextBlock { Text = value, Style = StyleFor("BodyLargeTextBlockStyle"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap },
            new TextBlock { Text = label, Style = StyleFor("SetupFactLabel"),
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap }
        }
    };

    private async Task ShowSetupAsync(ConfigDirectoryEntry entry, bool fromSaved)
    {
        refreshSharing = null;
        model.Opened(UsagePage.PlayerSetup);
        model.CatalogueEvent(UsageKind.SetupViewed, entry.Id);
        Status.IsOpen = false;
        Detail.Children.Clear();
        ResetDetailScroll();
        var back = new Button { Style = StyleFor("QuietButton"), Content = IconLabel("ChevronLeft", fromSaved ? "Saved" : "Player Setups", 14) };
        AutomationProperties.SetName(back, fromSaved ? "Back to Saved" : "Back to Player Setups");
        back.Click += async (_, _) => await shell.ShowProblemAsync(() => ShowCatalogueAsync(fromSaved));
        Detail.Children.Add(back);
        var search = CatalogueSearch("");
        search.QuerySubmitted += async (_, _) =>
        {
            if (fromSaved)
                savedQuery = search.Text;
            else
            {
                discoverQuery = search.Text;
                discoverGroup = null;
            }
            await shell.ShowProblemAsync(() => ShowCatalogueAsync(fromSaved));
        };
        ShowCatalogueToolbar(search, null);

        double? setupDistance = null;
        var attempt = Action("Try This Setup", () => ReviewSetupAsync(newOnly: true));
        attempt.Style = StyleFor("AccentButtonStyle");
        var import = Action("Import…", () => ReviewSetupAsync(newOnly: false));
        import.Style = StyleFor("SubtleAction");
        attempt.IsEnabled = import.IsEnabled = !model.Studio.IsReadOnly;
        Detail.Children.Add(SetupHero(entry, Identity(entry), attempt, Bookmark(entry)));

        var story = new StackPanel { Spacing = 8, Children = { PanelHeading("Story"), Body(entry.Story) } };
        var facts = Columns(12, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star),
            new GridLength(1, GridUnitType.Star));
        Put(facts, 0, IdentityFact("Era", entry.Era));
        if (entry.Team is not null)
            Put(facts, 1, IdentityFact("Team", entry.Team));
        if (entry.Role is not null)
            Put(facts, 2, IdentityFact("Role", entry.Role));
        story.Children.Add(facts);
        if (entry.FavoriteMap is { } map)
            story.Children.Add(IdentityFact("Favorite map", MapCatalog.DisplayName(map)));

        var notable = new StackPanel { Spacing = 8 };
        var notableCard = Panel(notable);
        var changes = new StackPanel { Spacing = 8 };
        var inSetup = new StackPanel { Spacing = 8 };
        var storyAndFacts = new StackPanel { Spacing = 16, Children = { Panel(story), notableCard } };
        var contentsCard = Panel(inSetup);
        var changesCard = Panel(changes);
        var columns = Columns(16, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
        columns.RowSpacing = 16;
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        Put(columns, 0, storyAndFacts);
        Put(columns, 0, contentsCard);
        Put(columns, 1, changesCard);
        Grid.SetRow(contentsCard, 1);
        Grid.SetRowSpan(changesCard, 2);
        columns.SizeChanged += (_, args) =>
        {
            bool stacked = args.NewSize.Width < 760;
            columns.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1, GridUnitType.Star);
            columns.RowDefinitions[2].Height = stacked ? GridLength.Auto : new GridLength(0);
            Grid.SetColumn(changesCard, stacked ? 0 : 1);
            Grid.SetRow(changesCard, stacked ? 1 : 0);
            Grid.SetRowSpan(changesCard, stacked ? 1 : 2);
            Grid.SetRow(contentsCard, stacked ? 2 : 1);
        };
        Detail.Children.Add(columns);
        Detail.Children.Add(Provenance(entry));

        RenderNotable(null);
        await ShowPreviewAsync();

        async Task ReviewSetupAsync(bool newOnly)
        {
            if (newOnly)
                model.CatalogueEvent(UsageKind.SetupTried, entry.Id);
            await ShowImportReviewAsync(entry.Player, new("Player Setups", "", "PeopleCommunity", entry.Credit,
                "Verified catalogue file"), async token =>
            {
                var review = await model.Discover.ReviewAsync(entry.Id, model.Dialect, active: null, cancellationToken: token);
                setupDistance ??= ConfigDiscover.SetupTurnDistance(review, catalogueDpi);
                return review;
            }, newOnly, () => setupDistance, entry.Id);
        }

        void RenderNotable(double? dpi)
        {
            notable.Children.Clear();
            notable.Children.Add(PanelHeading("Notable settings, explained"));
            if (setupDistance is { } distance && dpi is { } mouseDpi)
                AddFact("Mouse speed", $"{distance:F1} cm", $"Mouse travel for one full turn at {mouseDpi:G} DPI.");
            if (!string.IsNullOrWhiteSpace(entry.Facts?.Resolution))
                AddFact("Resolution", entry.Facts.Resolution, "The screen size in the published setup.");
            notableCard.Visibility = IsVisible(notable.Children.Count > 1);

            void AddFact(string label, string value, string detail)
            {
                if (notable.Children.Count > 1)
                    notable.Children.Add(new Border { Height = 1, Style = StyleFor("CardDivider") });
                var row = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
                Put(row, 0, new TextBlock { Text = label, Style = StyleFor("BodyStrongTextBlockStyle"),
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                Put(row, 1, new TextBlock { Text = value, Style = StyleFor("BodyLargeTextBlockStyle"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Right });
                notable.Children.Add(new StackPanel { Spacing = 2, Children = { row, Caption(detail) } });
            }
        }

        async Task ShowPreviewAsync()
        {
            changes.Children.Clear();
            inSetup.Children.Clear();
            var changeTitle = PanelHeading("What changes for you");
            var compared = Caption("Checking this setup…");
            var heading = new StackPanel { Spacing = 3, Children = { changeTitle, compared } };
            changes.Children.Add(heading);
            inSetup.Children.Add(PanelHeading("In this setup"));
            try
            {
                var active = model.Studio.ReadActive();
                var review = await model.Discover.ReviewAsync(entry.Id, model.Dialect, active);
                double dpi = model.DpiFor(active);
                setupDistance = ConfigDiscover.SetupTurnDistance(review, dpi);
                RenderNotable(dpi);
                var specs = model.Model.Specs.ToDictionary(spec => spec.Name, StringComparer.OrdinalIgnoreCase);
                double? activeDistance = active is null ? null : ConfigDiscover.LoadoutTurnDistance(active.Document, dpi,
                    specs.GetValueOrDefault("sensitivity")?.DefaultText);
                var rows = review.Rows
                    .Where(row => row.NewValue is not null && row.Title.StartsWith("Set ", StringComparison.Ordinal) &&
                        row.Candidate.SelectedByDefault && (row.CanSelect || !row.LockedNames.IsEmpty) &&
                        row.PlatformNote is null && !row.Candidate.Reasons.Contains(ConfigImportRisk.Credential))
                    .Select(row => (Row: row, Spec: specs.GetValueOrDefault(row.Title[4..])))
                    .Where(pair => pair.Spec is not null && !pair.Spec.Name.EndsWith("fps", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(pair => pair.Spec!.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .OrderByDescending(pair => pair.Spec!.Setting.Guided)
                    .Select(pair =>
                    {
                        var (row, spec) = pair;
                        bool mouse = spec!.Name.Equals("sensitivity", StringComparison.OrdinalIgnoreCase);
                        string? before = mouse ? (activeDistance is { } current ? Distance(current) : null) :
                            ConfigValues.Display(spec, row.OldValue ?? spec.DefaultText);
                        string after = mouse ? (setupDistance is { } source ? Distance(source) : "Distance unavailable") :
                            ConfigValues.Display(spec, row.NewValue);
                        return new SetupChange(spec.Title, before, after, !row.LockedNames.IsEmpty, mouse);
                    }).ToArray();
                compared.Text = active is null ? "" : $"Compared with {active.Profile.DisplayName}";
                compared.Visibility = IsVisible(active is not null);

                var compare = Action("Compare", async () =>
                {
                    if (active is null || !await ConfirmLeaveTextAsync())
                        return;
                    var player = await model.ReadSetupSettingsAsync(entry.Id);
                    model.Studio.Focus(active.Profile.Id);
                    loadoutText = null;
                    studioTab = 0;
                    expert = true;
                    expertFilter = ConfigExpertFilter.Group;
                    ClearSettingSearch();
                    comparison = new ExpertComparison(new ExpertSource(entry.Player, SetupId: entry.Id), null, player);
                    ShowStudio();
                    RenderLoadouts();
                });
                compare.Style = StyleFor("SubtleAction");
                compare.IsEnabled = active is not null && rows.Length > 0;
                changes.Children.Remove(heading);
                var tools = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
                Put(tools, 0, heading);
                Put(tools, 1, Buttons(compare, import));
                changes.Children.Add(tools);

                var different = rows.Where(row => row.Locked || row.Before != row.After).ToArray();
                foreach (var row in different.Take(6))
                    changes.Children.Add(Consequence(row, activeDistance, setupDistance));
                if (different.Length == 0)
                    changes.Children.Add(Caption(active is null ? "Open Compare to see the settings in this setup." : "No changes to the settings shown here."));
                if (different.Length > 6)
                    changes.Children.Add(Caption($"{different.Length - 6} more changes"));

                int settings = rows.Count(row => !row.Locked);
                int bindings = review.Rows.Count(row => row.CanSelect && row.Group == "Bindings");
                int toReview = review.Rows.Count(row => row.CanSelect && row.Candidate.Classification == ConfigImportClassification.NeedsOk);
                if (settings > 0)
                    inSetup.Children.Add(Body($"{settings} {(settings == 1 ? "setting" : "settings")}"));
                if (bindings > 0)
                    inSetup.Children.Add(Body($"{bindings} {(bindings == 1 ? "key binding" : "key bindings")}"));
                if (toReview > 0)
                    inSetup.Children.Add(Body(toReview == 1 ? "1 change needs your OK" : $"{toReview} changes need your OK"));
                if (settings + bindings + toReview == 0)
                    inSetup.Children.Add(Body("Nothing in this setup needs your OK."));
                inSetup.Children.Add(Caption("Trying this setup makes a new loadout."));
            }
            catch (Exception error) when (IsSetupReviewProblem(error))
            {
                compared.Text = "This setup couldn’t be checked.";
                changes.Children.Add(Buttons(Action("Try again", ShowPreviewAsync), import));
            }
        }
    }

    private sealed record SetupChange(string Title, string? Before, string After, bool Locked, bool Mouse);

    private static Grid Consequence(SetupChange change, double? current, double? incoming)
    {
        string icon = change.Locked ? "Lock" : change.Mouse ? "Gauge" : "CheckCircle";
        string title = change.Locked ? $"{change.Title} stays locked" : change.Title;
        string detail = Comparison(change);
        if (change.Mouse && incoming is { } after)
        {
            if (change.Locked)
                detail = $"This setup uses {Distance(after)}. Your loadout keeps its mouse speed.";
            else if (current is { } before)
            {
                title = before < after ? "You’ll turn slower" : "You’ll turn faster";
                detail = $"A full turn takes {after:F1} cm of mouse travel. Yours takes {before:F1} cm.";
            }
            else
                detail = $"A full turn takes {after:F1} cm of mouse travel.";
        }
        else if (!change.Locked && (change.Before is null || change.Before != change.After))
        {
            detail = change.Before is null ? $"You’ll use {change.After}." :
                $"You’ll use {change.After} in place of {change.Before}.";
        }
        var row = Columns(8, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        var glyph = Fluent(icon, 16);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
        var heading = new TextBlock { Text = title, Style = StyleFor("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap };
        GlyphAlign.WatchBeside(glyph, heading);
        Put(row, 0, glyph);
        Put(row, 1, new StackPanel { Spacing = 2, Children = { heading, Caption(detail) } });
        return row;
    }

    private static Grid Provenance(ConfigDirectoryEntry entry)
    {
        var signed = IconLabel("ShieldCheckmark", "Signed catalogue entry", 16);
        ((FontIcon)signed.Children[0]).Style = StyleFor("PositiveIcon");
        ((TextBlock)signed.Children[1]).Style = StyleFor("PositiveCaption");
        var source = Caption("");
        var link = new Hyperlink { NavigateUri = entry.SourceUrl };
        link.Inlines.Add(new Run { Text = entry.Provenance.PublishedBy });
        source.Inlines.Add(new Run { Text = "As published by " });
        source.Inlines.Add(link);
        source.Inlines.Add(new Run { Text = $". {entry.Provenance.Changes} Credit: {entry.Credit}." });
        var remove = new HyperlinkButton { Content = "Ask to remove or correct this setup",
            NavigateUri = ConfigDirectory.RemovalUrl, FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
        var footer = Columns(12, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(footer, 0, new StackPanel { Spacing = 4, Children = { signed, source } });
        Put(footer, 1, remove);
        return footer;
    }

    private Button Bookmark(ConfigDirectoryEntry entry)
    {
        var action = new Button { Style = StyleFor("IconAction") };
        Update();
        action.Click += async (_, _) => await shell.RunAsync(async () =>
        {
            string? message = await model.Discover.SetSavedAsync(entry.Id, !IsSaved());
            Update();
            UpdateSavedCount();
            if (message is not null)
                Feedback(message);
        });
        return action;

        bool IsSaved() => model.Discover.State.Saved.Any(bookmark => bookmark.Id == entry.Id);

        void Update()
        {
            bool saved = IsSaved();
            string label = saved ? "Remove from Saved" : "Save for later";
            action.Content = Fluent(saved ? "BookmarkFilled" : "Bookmark", 20, filled: saved);
            AutomationProperties.SetName(action, label);
            ToolTipService.SetToolTip(action, label);
        }
    }

    private Border SetupHero(ConfigDirectoryEntry entry, string eyebrow, Button primary, Button bookmark,
        bool featured = false, bool withPips = false)
    {
        bool wrap = featured && ExtraLargeText;
        var art = Artwork(entry, new CornerRadius(4));
        var body = new Grid { MinHeight = 240, Children = { art, new Border { Style = StyleFor("SetupHeroScrim") } } };
        var name = new TextBlock { Text = entry.Player, Style = StyleFor("SetupArtName") };
        var description = new TextBlock { Text = entry.Feel, Style = StyleFor("SetupArtDescription") };
        if (wrap)
        {
            name.MaxLines = description.MaxLines = 0;
            name.TextTrimming = description.TextTrimming = TextTrimming.None;
        }
        AutomationProperties.SetHeadingLevel(name, AutomationHeadingLevel.Level2);
        var text = new StackPanel { Spacing = 5, Children = {
            new TextBlock { Text = eyebrow.ToUpperInvariant(), Style = StyleFor("Eyebrow") }, name, description
        } };
        var actions = Buttons(primary, bookmark);
        actions.VerticalAlignment = VerticalAlignment.Bottom;
        var textColumn = new GridLength(1, GridUnitType.Star);
        var footer = wrap ? Columns(12, GridLength.Auto, textColumn) : Columns(12, GridLength.Auto, textColumn, GridLength.Auto);
        footer.VerticalAlignment = VerticalAlignment.Bottom;
        footer.Margin = new Thickness(16, 54, 16, withPips ? 42 : 16);
        Put(footer, 0, Avatar(entry, 72));
        Put(footer, 1, new Border { Style = StyleFor("SetupArtBackplate"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = text });
        if (wrap)
        {
            footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
            footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
            footer.RowSpacing = 12;
            Grid.SetRow(actions, 1);
            Grid.SetColumnSpan(actions, 2);
            actions.HorizontalAlignment = HorizontalAlignment.Right;
        }
        Put(footer, wrap ? 0 : 2, actions);
        body.Children.Add(footer);
        if (featured)
            body.SizeChanged += (_, args) => body.MinHeight = args.NewSize.Width >= 850 ? 300 : 240;
        return new Border { Style = StyleFor("FieldCard"), Padding = new Thickness(0), Child = body };
    }

    private static Border Avatar(ConfigDirectoryEntry entry, double diameter)
    {
        var avatar = new Border
        {
            Width = diameter, Height = diameter,
            Style = StyleFor("SetupAvatar"), VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = StringInfo.GetNextTextElement(entry.Player).ToUpper(CultureInfo.CurrentCulture),
                Style = StyleFor("LoadoutInitial"), FontSize = diameter > 40 ? 32 : 20
            }
        };
        AutomationProperties.SetAccessibilityView(avatar, AccessibilityView.Raw);
        return avatar;
    }

    private static StackPanel IdentityFact(string label, string value) => new()
    {
        Spacing = 2,
        Children = { Caption(label), new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap,
            Style = StyleFor("BodyStrongTextBlockStyle") } }
    };

    private Border Artwork(ConfigDirectoryEntry entry, CornerRadius corners)
    {
        string map = entry.FavoriteMap is { Length: > 0 } favorite ? favorite : HostingCatalog.Maps[
            entry.Id.Aggregate(0, (seed, letter) => (seed * 31 + letter) & 0xffff) % HostingCatalog.Maps.Length];
        var picture = new Image { Stretch = Stretch.UniformToFill };
        // Size the art from the card. The bitmap must not set the hero's desired height.
        var layer = new Canvas { Children = { picture } };
        layer.SizeChanged += (_, args) =>
        {
            picture.Width = args.NewSize.Width;
            picture.Height = args.NewSize.Height;
        };
        var art = new Border { Style = StyleFor("MediaTile"), CornerRadius = corners, IsHitTestVisible = false, Child = layer };
        AutomationProperties.SetAccessibilityView(art, AccessibilityView.Raw);
        art.Loaded += async (_, _) =>
        {
            picture.Source = await setupArt.GetAsync(map) ?? new SvgImageSource(new Uri(
                $"ms-appx:///Assets/hero-{(art.ActualTheme == ElementTheme.Light ? "light" : "dark")}.svg"));
        };
        return art;
    }

    private static Grid ShelfHeading(string title, string detail, string? action = null, Action? open = null)
    {
        var header = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(header, 0, new StackPanel { Spacing = 4, Children = { PanelHeading(title), Caption(detail) } });
        if (action is not null && open is not null)
        {
            var link = new HyperlinkButton { Content = action, VerticalAlignment = VerticalAlignment.Center };
            link.Click += (_, _) => open();
            Put(header, 1, link);
        }
        return header;
    }

    private static TextBlock PanelHeading(string title) => new() { Text = title, Style = StyleFor("CardTitle"), TextWrapping = TextWrapping.Wrap };
    private static Border Panel(FrameworkElement content) => new() { Style = StyleFor("FieldCard"), Child = content };
    private static string SetupCount(int count) => $"{count} {(count == 1 ? "setup" : "setups")}";
    private static string Identity(ConfigDirectoryEntry entry) => string.Join(" · ", new[] { entry.Team, entry.Era }.OfType<string>());
    private static string CatalogueDate(DateTimeOffset fetched) => fetched.LocalDateTime.Date == DateTime.Today
        ? "today" : fetched.LocalDateTime.ToString("d MMM", CultureInfo.CurrentCulture);
    private static bool IsSetupReviewProblem(Exception error) => error is IOException or UnauthorizedAccessException or
        ConfigDirectoryException or ConfigFetchHttpException or ConfigImportException;
    private static string Comparison(SetupChange change) =>
        change.Locked ? $"Yours stays {change.Before ?? "as it is"}. This setup uses {change.After}."
            : change.Before is null ? $"This setup uses {change.After}." : $"Yours: {change.Before} · This setup: {change.After}";

    private static TextBlock CardText(string value, bool wrap, string? style = null)
    {
        var text = style is null ? Caption(value) : new TextBlock { Text = value, Style = StyleFor(style) };
        text.TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        text.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        return text;
    }

    private void ResetDetailScroll() => DetailScroll.ChangeView(null, 0, null, disableAnimation: true);
}
