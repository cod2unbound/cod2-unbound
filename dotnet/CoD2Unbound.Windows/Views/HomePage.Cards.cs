using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

/// Jump back in and Last match (JumpBackInCard and LastMatchCard on the Mac).
public sealed partial class HomePage
{
    private const int SettingsPrivacyPane = 3;

    /// Home's own row styles (HomePage.xaml), built from the shared tokens.
    private Style LocalStyle(string key) => (Style)Resources[key];
    private readonly List<Button> recentButtons = [];
    private readonly List<(ListViewItem Item, bool Listed)> recentItems = [];

    /// A card's list: a stock ListView whose items click, with the Mac's row hover and press fills set
    /// through the item's own lightweight resources.
    private ListView HomeList(string itemStyle)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, ItemContainerStyle = LocalStyle(itemStyle),
            Padding = new Thickness()
        };
        list.Resources["ListViewItemBackgroundPointerOver"] = Ink("hover");
        list.Resources["ListViewItemBackgroundPressed"] = Ink("press");
        return list;
    }

    /// A card's empty state: a stencil heading, a sentence and at most one button.
    private static StackPanel CardEmpty(string heading, string message, (string Title, Action Run)? action = null)
    {
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(10, 22, 10, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock
        {
            Text = heading.ToUpperInvariant(), Style = StyleFor("EmptyHeading"), TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var sentence = new TextBlock { Text = message, Style = StyleFor("SecondaryText"), TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(sentence);
        if (action is { } act)
        {
            var button = new Button { Content = act.Title, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            button.Click += (_, _) => act.Run();
            panel.Children.Add(button);
        }
        return panel;
    }

    private void RenderRecent()
    {
        RecentRows.Children.Clear();
        recentButtons.Clear();
        recentItems.Clear();
        RecentRows.Margin = model.Recent.IsEmpty ? new Thickness(8, 0, 8, 0) : new Thickness();
        if (model.Recent.IsEmpty)
        {
            RecentRows.Children.Add(CardEmpty("Nothing played yet", "Servers you join appear here, ready to rejoin.",
                ("Find a Server", () => OnFindServer(this, new RoutedEventArgs()))));
            return;
        }
        var list = HomeList("RecentItem");
        AutomationProperties.SetName(list, "Recent servers");
        list.ItemClick += async (_, args) =>
        {
            if (args.ClickedItem is FrameworkElement { Tag: GameServer server } && RowOpens(!server.Fields.IsEmpty))
                await shell.OpenServersAsync(ServerScope.All, server.Address);
        };
        foreach (var server in model.Recent)
            list.Items.Add(RecentRow(server));
        RecentRows.Children.Add(list);
        RenderRecentState(model.LaunchProblem);
    }

    /// A recent server: art, name, map · mode · version, when it was played and Launch. The row shows the
    /// server in Servers; a server never seen in a list is plain text.
    private ListViewItem RecentRow(GameServer server)
    {
        string name = QuakeColors.Plain(server.Name);
        string? map = KnownMap(server);
        bool listed = !server.Fields.IsEmpty;
        string?[] parts = [map is null ? null : MapCatalog.DisplayName(map), server.GameType == "—" ? null : GameModes.Long(server.GameType),
            listed ? server.IsCod2x ? "CoD2x" : "Original 1.3" : null];
        string subtitle = parts.Any(part => part is not null) ? string.Join(" · ", parts.OfType<string>()) : "Joined by address";
        string? played = model.Played(server.Address, includeTime: false);

        var content = Columns(12, new GridLength(64), new GridLength(1, GridUnitType.Star), GridLength.Auto);
        var art = previews.Tile(server.Map, 64, 48);
        art.CornerRadius = new CornerRadius(9); // concentric with the row's 16 at 7 in
        Put(content, 0, art);
        var lines = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Style = LocalStyle("RecentName"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        lines.Children.Add(new TextBlock { Text = subtitle, Style = LocalStyle("RecentDetail"),
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        Put(content, 1, lines);
        if (played is not null)
        {
            var stamp = new TextBlock { Text = played, Style = StyleFor("MonoCaption"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Put(content, 2, stamp);
        }

        string spoken = $"{name}, {subtitle}" + (played is null ? "" : $", played {played.ToLowerInvariant()}");
        var row = new Grid { Tag = server };
        row.Children.Add(content);
        // Launch sits as far from the row's end as from its top and bottom (art 48 + 2 × 7).
        var launch = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { Fluent("Play", 11, filled: true), new TextBlock { Text = "Launch" } } },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        ((FontIcon)((StackPanel)launch.Content).Children[0]).VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(launch, $"Launch {name}");
        ToolTipService.SetToolTip(launch, $"Launch and join {name}");
        launch.Click += async (_, _) => await LaunchAsync(server.Address);
        recentButtons.Add(launch);
        row.Children.Add(launch);
        launch.SizeChanged += (_, args) => content.Margin = new Thickness(0, 0, args.NewSize.Width + 12, 0);

        var item = new ListViewItem { Content = row, Tag = server, Style = LocalStyle("RecentItem"), ContextFlyout = RecentMenu(server) };
        AutomationProperties.SetName(item, spoken);
        recentItems.Add((item, listed));
        return item;
    }

    private MenuFlyout RecentMenu(GameServer server)
    {
        var menu = new MenuFlyout();
        Add("Launch", () => LaunchAsync(server.Address));
        Add("Show in Servers", () => shell.OpenServersAsync(ServerScope.All, server.Address));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Copy Address", () =>
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(server.Address);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            return Task.CompletedTask;
        });
        return menu;

        void Add(string text, Func<Task> run)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (_, _) => await run();
            menu.Items.Add(item);
        }
    }

    /// As on the Mac, a row shows its server in Servers only when a list has shown that server and pages
    /// can change (not while the game runs); otherwise it is plain text beside its Launch button.
    private bool RowOpens(bool listed) => listed && !model.Game.Running;

    private void RenderRecentState(string? reason)
    {
        foreach (var (item, listed) in recentItems)
        {
            bool opens = RowOpens(listed);
            // The item's own lightweight resources: no hover or press fill on a row that does not open.
            if (opens)
            {
                item.Resources.Remove("ListViewItemBackgroundPointerOver");
                item.Resources.Remove("ListViewItemBackgroundPressed");
            }
            else
            {
                item.Resources["ListViewItemBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                item.Resources["ListViewItemBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            AutomationProperties.SetHelpText(item, opens ? "Shows this server in Servers" : "");
        }
        foreach (var button in recentButtons)
        {
            button.IsEnabled = reason is null;
            AutomationProperties.SetHelpText(button, reason ?? "");
        }
    }

    private async void OnRecent(object sender, RoutedEventArgs args) => await shell.OpenServersAsync(ServerScope.Recent);

    private void RenderMatch()
    {
        MatchRows.Children.Clear();
        MatchRows.RowDefinitions.Clear();
        if (!model.Settings.MatchSummariesEnabled)
            MatchRows.Children.Add(CardEmpty("Match summaries are off", "Turn them on to see how each match ran. They stay on this PC.",
                ("Turn On in Settings…", () => OpenSettingsPane(SettingsPrivacyPane))));
        else if (model.UnreadableMatches)
            MatchRows.Children.Add(CardEmpty("Couldn’t read matches", "Your match summaries couldn’t be read. Try again after your next match.",
                ("Try Again", async () => await shell.RunAsync(RefreshPlayHistoryAsync))));
        else if (model.Matches.LastOrDefault() is { } match)
            AddMatch(match);
        else
            MatchRows.Children.Add(CardEmpty("Not measured yet", "After your first match, its ping and connection show up here."));
    }

    private void AddMatch(MatchSummary match)
    {
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            MatchRows.RowDefinitions.Add(new RowDefinition { Height = height });
        MatchRows.RowSpacing = 0;

        var heading = Columns(12, new GridLength(64), new GridLength(1, GridUnitType.Star));
        var art = previews.Tile(match.Map, 64, 48);
        art.CornerRadius = new CornerRadius(9);
        Put(heading, 0, art);
        var title = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        string titleText = $"{MapCatalog.DisplayName(match.Map)} · {GameModes.Long(match.Mode)}";
        title.Children.Add(new TextBlock { Text = titleText, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Style = LocalStyle("RecentName"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        string stamp = MatchFacts.Duration(match.DurationSeconds).ToUpperInvariant();
        if (MatchFacts.Date(match.EndedAt) is { } date)
            stamp = MatchFacts.Played(date, TimeProvider.System.GetUtcNow(), TimeZoneInfo.Local, includeTime: false) + " · " + stamp;
        title.Children.Add(new TextBlock { Text = stamp, Style = StyleFor("MonoCaption"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis });
        Put(heading, 1, title);
        AutomationProperties.SetName(heading, $"{titleText}, {stamp}");
        MatchRows.Children.Add(heading);

        // How the match ran, one fact a line.
        var facts = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        string? ping = match.PingAverageMS is { } value ? $"{value:F0} ms" : null;
        facts.Children.Add(Fact("Ping", ping is null ? "Ping not measured" : $"{ping[..^3]} milliseconds ping",
            Value(ping ?? "Not measured", ping is null ? "CaptionBrush" : "BodyBrush")));
        var connection = MatchFacts.Connection(match.PacketsReceived > 0 ? 1 : 0, match.PacketLossPercent);
        string words = MatchFacts.ConnectionText(connection);
        facts.Children.Add(Fact("Connection", "Connection " + words.ToLowerInvariant(), Status(connection, words)));
        Grid.SetRow(facts, 1);
        MatchRows.Children.Add(facts);

        var check = new Button { Content = "Open Check-up", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        ToolTipService.SetToolTip(check, "Open Check-up for this match");
        check.Click += async (_, _) => await shell.OpenLibraryCheckupAsync(match);
        Grid.SetRow(check, 3);
        MatchRows.Children.Add(check);

        TextBlock Value(string text, string brush) => new()
        {
            Text = text, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ink(brush),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private FrameworkElement Status(MatchConnection connection, string words)
    {
        string brush = connection switch { MatchConnection.Steady => "PositiveBrush", MatchConnection.Dips => "caution", _ => "CaptionBrush" };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (connection == MatchConnection.Steady)
        {
            var icon = Fluent("CheckCircle", 15);
            icon.Foreground = Ink(brush);
            AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            label.Children.Add(icon);
        }
        label.Children.Add(new TextBlock { Text = words, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ink(brush), VerticalAlignment = VerticalAlignment.Center });
        GlyphAlign.SetIsEnabled(label, true);
        return label;
    }

    private Grid Fact(string title, string spoken, FrameworkElement value)
    {
        var row = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        row.Height = 34;
        row.BorderBrush = Ink("SeparatorBrush");
        row.BorderThickness = new Thickness(0, 1, 0, 0);
        var label = new TextBlock { Text = title, Style = StyleFor("SecondaryText"), VerticalAlignment = VerticalAlignment.Center };
        Put(row, 0, label);
        Put(row, 1, value);
        AutomationProperties.SetName(row, spoken);
        return row;
    }

    private async void OnAllMatches(object sender, RoutedEventArgs args) => await shell.RunAsync(() => shell.OpenMatchesAsync());

    private async void OpenSettingsPane(int pane) => await shell.RunAsync(async () =>
    {
        if (model.Settings.LastSettingsPane != pane)
            await session.UpdateSettingsAsync(model.Settings with { LastSettingsPane = pane });
        await shell.NavigateAsync(LauncherPlace.Settings);
    });
}
