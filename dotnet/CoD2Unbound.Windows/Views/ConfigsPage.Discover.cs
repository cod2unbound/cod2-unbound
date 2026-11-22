using System.Collections.Immutable;
using System.Globalization;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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

    private async Task ShowCatalogueAsync(bool saved, bool retry = false)
    {
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
            AddCatalogueStatus("Player Setups is unavailable", "Check your connection and try again.");
            return;
        }

        var state = model.Discover.State;
        var entries = saved ? state.Saved.Select(item => item.Entry).OfType<ConfigDirectoryEntry>().ToImmutableArray() : model.Discover.Entries;
        if (retry && !saved)
        {
            featuredIndex = 0;
            discoverGroup = null;
        }

        var search = new AutoSuggestBox
        {
            PlaceholderText = "Search players, teams or styles", Text = saved ? savedQuery : discoverQuery,
            AccessKey = "F", QueryIcon = Fluent("Search", 20)
        };
        AutomationProperties.SetName(search, search.PlaceholderText);
        var tools = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(tools, 0, search);
        var count = Caption(SetupCount(entries.Length) + (state.Fetched is { } date ? $" · Updated {date.LocalDateTime:d}" : ""));
        count.VerticalAlignment = VerticalAlignment.Center;
        var refresh = Action("Refresh player setups", () => ShowCatalogueAsync(saved, retry: true));
        refresh.Content = Fluent("Refresh", 20);
        refresh.Style = StyleFor("IconAction");
        AutomationProperties.SetName(refresh, "Refresh player setups");
        ToolTipService.SetToolTip(refresh, "Refresh player setups");
        Put(tools, 1, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { count, refresh } });
        Detail.Children.Add(tools);

        if (state.Problem is not null)
            Detail.Children.Add(Caption("Showing saved catalogue results. Refresh to check for updates."));

        var results = new StackPanel { Spacing = 14 };
        Detail.Children.Add(results);
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

        void Fill()
        {
            results.Children.Clear();
            string query = search.Text.Trim();
            if (!saved && discoverGroup is { } group)
            {
                var back = new HyperlinkButton { Content = "‹ Player Setups" };
                back.Click += (_, _) => { discoverGroup = null; Fill(); };
                results.Children.Add(back);
                results.Children.Add(ShelfHeading(group, SetupCount(groupEntries.Length)));
                results.Children.Add(SetupGrid(groupEntries, saved));
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
            {
                featuredIndex = Math.Clamp(featuredIndex, 0, heroes.Length - 1);
                var hero = heroes[featuredIndex];
                var carousel = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
                Put(carousel, 0, Caption($"Featured setup {featuredIndex + 1} of {heroes.Length}"));
                if (heroes.Length > 1)
                {
                    var previous = new Button { Content = "‹", Style = StyleFor("IconAction") };
                    var next = new Button { Content = "›", Style = StyleFor("IconAction") };
                    AutomationProperties.SetName(previous, "Previous featured setup");
                    AutomationProperties.SetName(next, "Next featured setup");
                    previous.Click += (_, _) => Move(-1);
                    next.Click += (_, _) => Move(1);
                    Put(carousel, 1, Buttons(previous, next));
                }
                var view = Action("See this setup", () => ShowSetupAsync(hero, false));
                view.Style = StyleFor("LauncherAccentButton");
                results.Children.Add(carousel);
                results.Children.Add(SetupHero(hero, view, Bookmark(hero, () => ShowCatalogueAsync(false))));
                void Move(int delta)
                {
                    featuredIndex = (featuredIndex + delta + heroes.Length) % heroes.Length;
                    Fill();
                }
            }

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
                foreach (var team in teams)
                {
                    var matching = entries.Where(entry => entry.Team == team.Name).ToImmutableArray();
                    var button = new Button { Content = team.Name + " · " + team.Count, Margin = new Thickness(0, 0, 8, 8) };
                    button.Click += (_, _) => OpenGroup(team.Name, matching);
                    teamCards.Children.Add(button);
                }
                results.Children.Add(teamCards);
            }

            if (shelves.IsEmpty && entries.Select(entry => entry.Era).Distinct().Count() > 1)
                foreach (var era in ConfigDiscover.EraShelves(entries))
                {
                    results.Children.Add(ShelfHeading(era.Era, SetupCount(era.Entries.Length)));
                    results.Children.Add(SetupShelf(era.Entries.Take(10), saved));
                }

            if (!entries.IsEmpty)
                results.Children.Add(ShelfHeading("All setups", SetupCount(entries.Length), "See all", () =>
                    OpenGroup("All setups", ConfigDiscover.Setups(entries, ConfigSetupFilter.All, "", CultureInfo.CurrentCulture))));
            else
                results.Children.Add(EmptyState("Search", "No player setups yet", "Check back later."));
        }

        void OpenGroup(string label, ImmutableArray<ConfigDirectoryEntry> entries)
        {
            discoverGroup = label;
            groupEntries = entries;
            Fill();
            ResetDetailScroll();
        }

        void AddCatalogueStatus(string heading, string detail)
        {
            var retryButton = Action("Try again", () => ShowCatalogueAsync(saved, retry: true));
            Detail.Children.Add(SettingRow(heading, detail, retryButton));
        }
    }

    private FrameworkElement SetupShelf(IEnumerable<ConfigDirectoryEntry> entries, bool inSaved)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var entry in entries)
            row.Children.Add(SetupCard(entry, inSaved));
        return new ScrollViewer
        {
            Content = row, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private FrameworkElement SetupGrid(IEnumerable<ConfigDirectoryEntry> entries, bool inSaved)
    {
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 198, ItemHeight = 190 };
        foreach (var entry in entries)
            grid.Children.Add(SetupCard(entry, inSaved));
        return grid;
    }

    private Button SetupCard(ConfigDirectoryEntry entry, bool inSaved)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(Artwork(entry, height: 68));
        body.Children.Add(new TextBlock { Text = entry.Player, Style = StyleFor("BodyStrongTextBlockStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(OneLine(Identity(entry)));
        body.Children.Add(OneLine(entry.Feel));
        if (SetupFacts(entry) is { Length: > 0 } facts)
            body.Children.Add(MonoCaption(facts));
        var open = Action("View setup", () => ShowSetupAsync(entry, inSaved));
        open.Content = body;
        open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        open.VerticalContentAlignment = VerticalAlignment.Top;
        open.Width = 190;
        open.Height = 182;
        AutomationProperties.SetName(open, $"View setup: {entry.Player}");
        return open;
    }

    private async Task ShowSetupAsync(ConfigDirectoryEntry entry, bool fromSaved)
    {
        Status.IsOpen = false;
        Detail.Children.Clear();
        ResetDetailScroll();
        var back = new HyperlinkButton { Content = fromSaved ? "‹ Saved" : "‹ Player Setups" };
        back.Click += async (_, _) => await shell.RunAsync(() => ShowCatalogueAsync(fromSaved));
        Detail.Children.Add(back);

        var attempt = Action("Try this setup", () => ReviewSetupAsync(newOnly: true));
        attempt.Style = StyleFor("LauncherAccentButton");
        var import = Action("Import…", () => ReviewSetupAsync(newOnly: false));
        attempt.IsEnabled = import.IsEnabled = !model.Studio.IsReadOnly;
        Detail.Children.Add(SetupHero(entry, attempt, import, Bookmark(entry, () => ShowSetupAsync(entry, fromSaved))));

        var story = new StackPanel { Spacing = 8, Children = { PanelHeading("Story"), Body(entry.Story) } };
        var facts = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal };
        AddIdentityFact("Era", entry.Era);
        AddIdentityFact("Team", entry.Team);
        AddIdentityFact("Role", entry.Role);
        AddIdentityFact("Favorite map", entry.FavoriteMap is { } map ? MapCatalog.DisplayName(map) : null);
        story.Children.Add(facts);
        story.Children.Add(Caption($"As published by {entry.Provenance.PublishedBy} · Credit: {entry.Credit}"));
        story.Children.Add(new HyperlinkButton { Content = "Source", NavigateUri = entry.SourceUrl, Padding = new Thickness(0) });

        var changes = new StackPanel { Spacing = 8 };
        var inSetup = new StackPanel { Spacing = 8 };
        var primary = new StackPanel { Spacing = 12, Children = { Panel(story), Panel(changes) } };
        var secondary = new StackPanel { Spacing = 12, Children = { Panel(inSetup) } };
        if (SetupFacts(entry).Length > 0 || !entry.StyleTags.IsEmpty)
        {
            var notable = new StackPanel { Spacing = 8, Children = { PanelHeading("Notable settings") } };
            if (entry.Facts?.Sensitivity is { Length: > 0 } sensitivity)
                notable.Children.Add(SettingRow("Mouse speed", sensitivity));
            if (entry.Facts?.Resolution is { Length: > 0 } resolution)
                notable.Children.Add(SettingRow("Resolution", resolution));
            if (!entry.StyleTags.IsEmpty)
                notable.Children.Add(Caption(string.Join(" · ", entry.StyleTags)));
            secondary.Children.Insert(0, Panel(notable));
        }
        var columns = Columns(12, new GridLength(1.25, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
        Put(columns, 0, primary);
        Put(columns, 1, secondary);
        Detail.Children.Add(columns);
        var signed = Caption("Signed catalogue entry");
        signed.Style = StyleFor("PositiveCaption");
        Detail.Children.Add(signed);
        await ShowPreviewAsync();

        async Task ReviewSetupAsync(bool newOnly)
        {
            var review = await model.Discover.ReviewAsync(entry.Id, model.Dialect, active: null);
            await ReviewAsync(review, entry.Player, newOnly);
        }

        void AddIdentityFact(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                facts.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 16, 8), Children =
                    { Caption(label), new TextBlock { Text = value, Style = StyleFor("BodyStrongTextBlockStyle") } } });
        }

        async Task ShowPreviewAsync()
        {
            changes.Children.Clear();
            changes.Children.Add(PanelHeading("What changes for you"));
            changes.Children.Add(Caption("Checking this setup…"));
            inSetup.Children.Clear();
            inSetup.Children.Add(PanelHeading("In this setup"));
            try
            {
                var active = model.Studio.ReadActive();
                var review = await model.Discover.ReviewAsync(entry.Id, model.Dialect, active);
                var specs = model.Model.Specs.ToDictionary(spec => spec.Name, StringComparer.OrdinalIgnoreCase);
                var rows = review.Rows
                    .Where(row => row.NewValue is not null && row.Title.StartsWith("Set ", StringComparison.Ordinal) &&
                        (row.CanSelect || !row.LockedNames.IsEmpty) && row.PlatformNote is null &&
                        !row.Candidate.Reasons.Contains(ConfigImportRisk.Credential))
                    .Select(row => (Row: row, Spec: specs.GetValueOrDefault(row.Title[4..])))
                    .Where(pair => pair.Spec is not null && !pair.Spec.Name.EndsWith("fps", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(pair => pair.Spec!.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .OrderByDescending(pair => pair.Spec!.Setting.Guided)
                    .Select(pair =>
                    {
                        var (row, spec) = pair;
                        string? before = row.OldValue is { } old ? ConfigValues.Display(spec!, old) : null;
                        return (Title: spec!.Title, Before: before, After: ConfigValues.Display(spec, row.NewValue),
                            Locked: !row.LockedNames.IsEmpty);
                    }).ToArray();
                changes.Children.RemoveAt(1);
                if (active is not null)
                    changes.Children.Add(Caption($"Compared with {active.Profile.DisplayName}"));

                var compare = Action("Compare", async () =>
                {
                    var all = new StackPanel { Spacing = 6, MinWidth = 340 };
                    foreach (var row in rows)
                        all.Children.Add(SettingRow(row.Title, Comparison(row.Before, row.After, row.Locked)));
                    var content = new ScrollViewer { Content = all, MaxHeight = 338 };
                    await new ContentDialog
                    {
                        XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "Compare setups",
                        Content = content, CloseButtonText = "Close"
                    }.ShowAsync();
                });
                compare.IsEnabled = rows.Length > 0;
                changes.Children.Add(compare);

                var different = rows.Where(row => row.Before != row.After).ToArray();
                foreach (var row in different.Take(6))
                    changes.Children.Add(SettingRow(row.Title, Comparison(row.Before, row.After, row.Locked)));
                if (different.Length == 0)
                    changes.Children.Add(Caption(active is null ? "Open Compare to see the settings in this setup." : "No changes to the settings shown here."));
                if (different.Length > 6)
                    changes.Children.Add(Caption($"{different.Length - 6} more changes"));
                int bindings = review.Rows.Count(row => row.CanSelect && row.Group == "Bindings");
                int toReview = review.Rows.Count(row => row.CanSelect && row.Candidate.Classification == ConfigImportClassification.NeedsOk);
                inSetup.Children.Add(Body($"{rows.Length} {(rows.Length == 1 ? "setting" : "settings")}"));
                inSetup.Children.Add(Body($"{bindings} {(bindings == 1 ? "key binding" : "key bindings")}"));
                inSetup.Children.Add(Body($"{toReview} {(toReview == 1 ? "line to review" : "lines to review")}"));
                inSetup.Children.Add(Caption("Trying this setup makes a new loadout."));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ConfigDirectoryException
                or ConfigFetchHttpException or ConfigImportException)
            {
                changes.Children.RemoveAt(1);
                changes.Children.Add(Caption("This setup couldn’t be checked."));
                changes.Children.Add(Action("Try again", ShowPreviewAsync));
            }
        }
    }

    private Button Bookmark(ConfigDirectoryEntry entry, Func<Task> refresh)
    {
        bool saved = model.Discover.State.Saved.Any(bookmark => bookmark.Id == entry.Id);
        string label = saved ? "Remove from Saved" : "Save for later";
        var action = Action(label, async () =>
        {
            string? message = await model.Discover.SetSavedAsync(entry.Id, !saved);
            await refresh();
            if (message is not null)
                Feedback(message);
        });
        action.Content = Fluent("Bookmark", 20);
        action.Style = StyleFor("IconAction");
        AutomationProperties.SetName(action, label);
        ToolTipService.SetToolTip(action, label);
        return action;
    }

    private Border SetupHero(ConfigDirectoryEntry entry, params Button[] actions)
    {
        var layout = new StackPanel { Spacing = 8 };
        layout.Children.Add(Artwork(entry, height: 150));
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = Identity(entry).ToUpperInvariant(), Style = StyleFor("Eyebrow") });
        text.Children.Add(new TextBlock { Text = entry.Player.ToUpperInvariant(), Style = StyleFor("StencilText"),
            FontSize = 38, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Body(entry.Feel));
        var row = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(row, 0, text);
        var buttons = Buttons(actions);
        buttons.VerticalAlignment = VerticalAlignment.Bottom;
        Put(row, 1, buttons);
        layout.Children.Add(row);
        return Panel(layout);
    }

    private Border Artwork(ConfigDirectoryEntry entry, double height)
    {
        string map = entry.FavoriteMap is { Length: > 0 } favorite ? favorite : HostingCatalog.Maps[
            entry.Id.Aggregate(0, (seed, letter) => (seed * 31 + letter) & 0xffff) % HostingCatalog.Maps.Length];
        var picture = new Image { Stretch = Stretch.UniformToFill };
        AutomationProperties.SetAccessibilityView(picture, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var art = new Border { Height = height, Style = StyleFor("MediaTile"), Child = picture };
        art.Loaded += async (_, _) =>
        {
            var image = await setupArt.GetAsync(map) ?? new SvgImageSource(new Uri(
                $"ms-appx:///Assets/hero-{(art.ActualTheme == ElementTheme.Light ? "light" : "dark")}.svg"));
            picture.Source = image;
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
    private static string Identity(ConfigDirectoryEntry entry) => string.Join(" · ", new[] { entry.Team, entry.Era, entry.Role }.OfType<string>());
    private static string SetupFacts(ConfigDirectoryEntry entry) =>
        string.Join(" · ", new[] { entry.Facts?.Sensitivity, entry.Facts?.Resolution }.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string Comparison(string? before, string after, bool locked) =>
        locked ? $"Yours stays {before ?? "as it is"}. This setup uses {after}."
            : before is null ? $"This setup uses {after}." : $"Yours: {before} · This setup: {after}";

    private static TextBlock OneLine(string value)
    {
        var text = Caption(value);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.TextWrapping = TextWrapping.NoWrap;
        return text;
    }

    private void ResetDetailScroll() => DetailScroll.ChangeView(null, 0, null, disableAnimation: true);
}
