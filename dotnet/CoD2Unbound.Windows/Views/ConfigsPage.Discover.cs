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
        if (!retry)
            model.Opened(saved ? UsagePage.Saved : UsagePage.Setups);
        Status.IsOpen = false;
        Loadouts.SelectedItem = null;
        Detail.Children.Clear();
        ResetDetailScroll();
        var title = Heading(saved ? "Saved" : "Player Setups");
        Detail.Children.Add(title);
        var status = Caption("Loading player setups…");
        Detail.Children.Add(status);
        await model.Discover.LoadAsync(retry);
        UpdateSavedCount();
        Detail.Children.Remove(status);
        if (model.Discover.State.Snapshot is null)
        {
            model.Error(UsageProblem.Directory);
            AddCatalogueStatus("Player Setups is unavailable", "Check your connection and try again.");
            return;
        }

        var state = model.Discover.State;
        catalogueDpi = model.DpiFor(model.Studio.ReadActive());
        var entries = saved ? state.Saved.Select(item => item.Entry).OfType<ConfigDirectoryEntry>().ToImmutableArray() : model.Discover.Entries;
        if (retry && !saved)
        {
            featuredIndex = 0;
            discoverGroup = null;
            shelfDistances.Clear();
        }

        var search = new AutoSuggestBox
        {
            PlaceholderText = "Search players, teams or styles", Text = saved ? savedQuery : discoverQuery,
            AccessKey = "F", QueryIcon = Fluent("Search", 20)
        };
        AutomationProperties.SetName(search, search.PlaceholderText);
        var tools = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(tools, 0, search);
        var count = Caption(SetupCount(entries.Length) + (state.Fetched is { } date ? $" · updated {CatalogueDate(date)}" : ""));
        count.VerticalAlignment = VerticalAlignment.Center;
        var refresh = Action("Refresh player setups", () => ShowCatalogueAsync(saved, retry: true));
        refresh.Content = Fluent("Refresh", 20);
        refresh.Style = StyleFor("IconAction");
        AutomationProperties.SetName(refresh, "Refresh player setups");
        ToolTipService.SetToolTip(refresh, "Refresh player setups");
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left, Children = { count, refresh } };
        Put(tools, 1, counts);
        MoveBelowWhenNarrow(tools, counts, 560);
        Detail.Children.Add(tools);

        if (state.Problem is not null)
            Detail.Children.Add(Caption("Showing saved catalogue results. Refresh to check for updates."));

        var results = new StackPanel { Spacing = 14 };
        Detail.Children.Add(results);
        ConfigDirectoryPopularity? figures = model.Discover.Popularity is { IsCompletedSuccessfully: true } ready
            ? ready.Result : null;
        search.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
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
            figures = await pending;
            if (figures is not null && Detail.Children.Contains(results) && discoverGroup is not null)
                Fill();
        }

        void Fill()
        {
            results.Children.Clear();
            string query = search.Text.Trim();
            if (!saved && discoverGroup is { } group)
            {
                var back = new HyperlinkButton { Content = "‹ Player Setups" };
                back.Click += (_, _) => { discoverGroup = null; Fill(); };
                results.Children.Add(back);
                var caption = Caption("");
                caption.VerticalAlignment = VerticalAlignment.Bottom;
                caption.TextWrapping = TextWrapping.Wrap;
                var gridHeading = Columns(12, new GridLength(1, GridUnitType.Star), GridLength.Auto);
                Put(gridHeading, 0, new StackPanel { Spacing = 4, Children = { PanelHeading(group), caption } });
                results.Children.Add(gridHeading);
                var cards = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
                results.Children.Add(cards);
                var orders = ConfigDiscover.AvailableOrders(figures);
                var order = orders.Contains(shell.SetupOrder) ? shell.SetupOrder : ConfigSetupOrder.FeaturedFirst;
                if (orders.Length > 1)
                {
                    var sort = new ComboBox { MinWidth = 152 };
                    var label = new TextBlock { Text = "Sort by", VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetLabeledBy(sort, label);
                    ToolTipService.SetToolTip(sort, "Sort the setups");
                    sort.ItemsSource = orders.Select(value => value switch
                    {
                        ConfigSetupOrder.MostImported => "Most imported",
                        ConfigSetupOrder.MostViewed => "Most viewed",
                        _ => "Featured first"
                    }).ToArray();
                    sort.SelectedIndex = orders.IndexOf(order);
                    sort.SelectionChanged += (_, _) =>
                    {
                        if (sort.SelectedIndex < 0)
                            return;
                        shell.SetupOrder = order = orders[sort.SelectedIndex];
                        FillGrid();
                    };
                    var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Bottom, Children = { label, sort } };
                    Put(gridHeading, 1, sortRow);
                    MoveBelowWhenNarrow(gridHeading, sortRow, 720);
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
                return;
            }

            if (saved || query.Length > 0)
            {
                var matching = ConfigDiscover.Setups(entries, ConfigSetupFilter.All, query, CultureInfo.CurrentCulture);
                if (!matching.IsEmpty)
                    results.Children.Add(SetupGrid(matching, saved));
                else
                    results.Children.Add(EmptyState(query.Length > 0 ? "Search" : "Bookmark",
                        saved && entries.IsEmpty ? "No saved player setups" : "No matching player setups",
                        saved && entries.IsEmpty ? "Setups you save for later appear here." : "Try another search."));
                if (saved && query.Length == 0)
                    foreach (var missing in state.Saved.Where(item => item.Entry is null))
                        results.Children.Add(SettingRow("Unavailable setup", null,
                            Action("Remove from Saved", async () =>
                            {
                                await model.Discover.SetSavedAsync(missing.Id, false);
                                await ShowCatalogueAsync(saved: true);
                            })));
                return;
            }

            var heroes = ConfigDiscover.Heroes(entries);
            if (!heroes.IsEmpty)
                results.Children.Add(FeaturedHero(heroes));

            var shelves = ConfigDiscover.StyleShelves(entries, CultureInfo.CurrentCulture);
            foreach (var shelf in shelves)
            {
                string heading = shelf.Filter switch { ConfigSetupFilter.Classic => "Classic era", ConfigSetupFilter.Cod2x => "CoD2x era", _ => "Modern" };
                string note = shelf.Filter switch
                {
                    ConfigSetupFilter.Classic => "From the game’s first years, around 2005 to 2008.",
                    ConfigSetupFilter.Cod2x => "Made for CoD2x.",
                    _ => "From the game’s later years."
                };
                var matching = ConfigDiscover.Setups(entries, shelf.Filter, "", CultureInfo.CurrentCulture);
                results.Children.Add(ShelfHeading(heading, note, $"See all {shelf.Total}", () => OpenGroup(heading, matching)));
                results.Children.Add(SetupShelf(shelf.Entries, saved));
            }

            var teams = ConfigDiscover.Teams(entries, CultureInfo.CurrentCulture);
            if (!teams.IsEmpty)
            {
                results.Children.Add(ShelfHeading("Teams", "Browse setups by team"));
                var teamCards = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal };
                teamCards.SizeChanged += (_, args) =>
                {
                    double width = args.NewSize.Width;
                    if (width > 0)
                        teamCards.ItemWidth = Math.Floor(width / Math.Max(1, (int)(width / 200)));
                };
                foreach (var team in teams)
                {
                    var matching = entries.Where(entry => entry.Team == team.Name).ToImmutableArray();
                    var contents = Columns(6, new GridLength(1, GridUnitType.Star), GridLength.Auto);
                    Put(contents, 0, new TextBlock { Text = team.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                    Put(contents, 1, new TextBlock { Text = $"· {team.Count}" });
                    var button = new Button { Content = contents, Margin = new Thickness(0, 0, 8, 8),
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    AutomationProperties.SetName(button, $"{team.Name} · {team.Count}");
                    ToolTipService.SetToolTip(button, team.Name);
                    button.Click += (_, _) => OpenGroup(team.Name, matching);
                    teamCards.Children.Add(button);
                }
                results.Children.Add(teamCards);
            }

            if (shelves.IsEmpty && entries.Select(entry => entry.Era).Distinct().Count() > 1)
                foreach (var era in ConfigDiscover.EraShelves(entries))
                {
                    results.Children.Add(ShelfHeading(era.Era, SetupCount(era.Entries.Length), $"See all {era.Entries.Length}",
                        () => OpenGroup(era.Era, era.Entries)));
                    results.Children.Add(SetupShelf(era.Entries.Take(10), saved));
                }

            if (!entries.IsEmpty)
                results.Children.Add(ShelfHeading("All setups", SetupCount(entries.Length), "See all", () =>
                    OpenGroup("All setups", entries)));
            else
                results.Children.Add(EmptyState("Search", "No player setups yet", "Check back later."));
        }

        void OpenGroup(string label, ImmutableArray<ConfigDirectoryEntry> matching)
        {
            discoverGroup = label;
            var ids = matching.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            groupEntries = [.. entries.Where(entry => ids.Contains(entry.Id))];
            Fill();
            ResetDetailScroll();
        }

        void AddCatalogueStatus(string heading, string detail)
        {
            var retryButton = Action("Try again", () => ShowCatalogueAsync(saved, retry: true));
            Detail.Children.Add(SettingRow(heading, detail, retryButton));
        }
    }

    private Grid FeaturedHero(ImmutableArray<ConfigDirectoryEntry> entries)
    {
        var picture = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
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
            view.Style = StyleFor("LauncherAccentButton");
            picture.Content = SetupHero(entry, $"Featured setup · {entry.Era}", view,
                Bookmark(entry), hasPager);
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
        var art = Artwork(entry, new CornerRadius(4, 4, 0, 0));
        art.Height = 104;
        art.VerticalAlignment = VerticalAlignment.Top;
        var avatar = Avatar(entry, 40);
        avatar.HorizontalAlignment = HorizontalAlignment.Left;
        avatar.VerticalAlignment = VerticalAlignment.Bottom;
        avatar.Margin = new Thickness(12, 0, 0, 0);
        var top = new Grid { Height = 122, Children = { art, avatar } };
        var mouse = ShelfValue("Mouse speed · per full turn", "");
        mouse.Visibility = Visibility.Collapsed;
        var facts = Columns(8, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
        Put(facts, 0, mouse);
        if (!string.IsNullOrWhiteSpace(entry.Facts?.Resolution))
            Put(facts, 1, ShelfValue("Resolution", entry.Facts.Resolution));
        var info = new StackPanel
        {
            Padding = new Thickness(12, 4, 12, 12), Spacing = 4,
            Children = {
                OneLine(entry.Player, "BodyStrongTextBlockStyle"), OneLine(Identity(entry)), OneLine(entry.Feel, "SecondaryText"), facts
            }
        };
        if (figureLine is not null)
        {
            var figure = OneLine(figureLine, "SetupFactLabel");
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

    private static StackPanel ShelfValue(string label, string value) => new()
    {
        Spacing = 2,
        Children = {
            new TextBlock { Text = value, Style = StyleFor("BodyLargeTextBlockStyle"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            new TextBlock { Text = label, Style = StyleFor("SetupFactLabel") }
        }
    };

    private async Task ShowSetupAsync(ConfigDirectoryEntry entry, bool fromSaved)
    {
        model.Opened(UsagePage.PlayerSetup);
        model.CatalogueEvent(UsageKind.SetupViewed, entry.Id);
        Status.IsOpen = false;
        Detail.Children.Clear();
        ResetDetailScroll();
        var back = new HyperlinkButton { Content = fromSaved ? "‹ Saved" : "‹ Player Setups" };
        back.Click += async (_, _) => await shell.RunAsync(() => ShowCatalogueAsync(fromSaved));
        Detail.Children.Add(back);

        double? setupDistance = null;
        var attempt = Action("Try this setup", () => ReviewSetupAsync(newOnly: true));
        attempt.Style = StyleFor("LauncherAccentButton");
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
        var columns = Columns(12, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
        Put(columns, 0, new StackPanel { Spacing = 12, Children = { Panel(story), notableCard } });
        Put(columns, 1, new StackPanel { Spacing = 12, Children = { Panel(changes), Panel(inSetup) } });
        Detail.Children.Add(columns);
        Detail.Children.Add(Provenance(entry));

        RenderNotable(null);
        await ShowPreviewAsync();

        async Task ReviewSetupAsync(bool newOnly)
        {
            if (newOnly)
                model.CatalogueEvent(UsageKind.SetupTried, entry.Id);
            try
            {
                var review = await model.Discover.ReviewAsync(entry.Id, model.Dialect, active: null);
                setupDistance ??= ConfigDiscover.SetupTurnDistance(review, model.DpiFor(model.Studio.ReadActive()));
                await ReviewAsync(review, entry.Player, newOnly, () => setupDistance, entry.Id);
            }
            catch (Exception error) when (error is IOException or ConfigDirectoryException or ConfigImportSessionException)
            {
                model.Error(UsageProblem.Import);
                throw;
            }
        }

        void RenderNotable(double? dpi)
        {
            notable.Children.Clear();
            notable.Children.Add(PanelHeading("Notable settings"));
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
                    var all = new StackPanel { Spacing = 6, MinWidth = 320 };
                    foreach (var row in rows)
                        all.Children.Add(SettingRow(row.Title, Comparison(row)));
                    await new ContentDialog
                    {
                        XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme, Title = "Compare setups",
                        Content = new ScrollViewer { Content = all, MaxHeight = 338 }, CloseButtonText = "Close"
                    }.ShowAsync();
                });
                compare.Style = StyleFor("SubtleAction");
                compare.IsEnabled = rows.Length > 0;
                changes.Children.Add(Buttons(compare, import));

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
        glyph.Margin = new Thickness(0, 2, 0, 0);
        AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
        Put(row, 0, glyph);
        Put(row, 1, new StackPanel { Spacing = 2, Children = {
            new TextBlock { Text = title, Style = StyleFor("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap },
            Caption(detail)
        } });
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
        var action = new Button { Width = 36, Height = 36, Padding = new Thickness(0) };
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

    private Border SetupHero(ConfigDirectoryEntry entry, string eyebrow, Button primary, Button bookmark, bool withPips = false)
    {
        var art = Artwork(entry, new CornerRadius(4));
        var body = new Grid { MinHeight = 240, Children = { art, new Border { Style = StyleFor("SetupHeroScrim") } } };
        var name = new TextBlock { Text = entry.Player, Style = StyleFor("SetupArtName") };
        AutomationProperties.SetHeadingLevel(name, AutomationHeadingLevel.Level2);
        var text = new StackPanel { Spacing = 5, Children = {
            new TextBlock { Text = eyebrow.ToUpperInvariant(), Style = StyleFor("Eyebrow") }, name,
            new TextBlock { Text = entry.Feel, Style = StyleFor("SetupArtDescription") }
        } };
        var actions = Buttons(primary, bookmark);
        actions.VerticalAlignment = VerticalAlignment.Bottom;
        var footer = Columns(12, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        footer.VerticalAlignment = VerticalAlignment.Bottom;
        footer.Margin = new Thickness(16, 54, 16, withPips ? 42 : 16);
        Put(footer, 0, Avatar(entry, 72));
        Put(footer, 1, new Border { Style = StyleFor("SetupArtBackplate"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = text });
        Put(footer, 2, actions);
        body.Children.Add(footer);
        body.SizeChanged += (_, args) => body.MinHeight = args.NewSize.Width >= 850 ? 300 : 240;
        return new Border { Style = StyleFor("FieldCard"), Padding = new Thickness(0), Child = body };
    }

    private static Border Avatar(ConfigDirectoryEntry entry, double diameter)
    {
        var avatar = new Border
        {
            Width = diameter, Height = diameter, CornerRadius = new CornerRadius(diameter / 2),
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

    private static TextBlock OneLine(string value, string? style = null)
    {
        var text = style is null ? Caption(value) : new TextBlock { Text = value, Style = StyleFor(style) };
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.TextWrapping = TextWrapping.NoWrap;
        return text;
    }

    private void ResetDetailScroll() => DetailScroll.ChangeView(null, 0, null, disableAnimation: true);
}
