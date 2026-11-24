using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HostingPage
{
    private sealed class Dashboard
    {
        private static readonly GridLength Fill = new(1, GridUnitType.Star);
        private readonly HostingPage page;
        private readonly HostedServer server;
        private readonly Button join;
        private readonly StackPanel joinRows = new() { Spacing = 1 };
        private readonly ItemsControl playerRows = new();
        private readonly ObservableCollection<Border> inlineRows = [];
        private readonly Border emptyPlayers = SettingRow("No players connected", null, grouped: true);
        private readonly Border morePlayers;
        private readonly TextBlock morePlayerCount = Body("");
        private readonly Button showAllPlayers = new();
        private readonly TextBlock playersValue = Heading("");
        private readonly TextBlock mapValue = Heading("");
        private readonly TextBlock gameType = Caption("");
        private readonly TextBlock log = MonoCaption("Waiting for the server log…");
        private readonly ScrollViewer tail;
        private readonly TextBlock full = Caption("Server is full.");
        private HostedJoinAddresses? lastAddresses;
        private bool lastWifi;
        private int inlineLimit = 6;

        internal Grid Stats { get; } = Columns(8, Fill, Fill, Fill, Fill);
        internal Grid Content { get; } = Columns(12, Fill, Fill);

        internal Dashboard(HostingPage page, HostedServer server, Button join)
        {
            this.page = page;
            this.server = server;
            this.join = join;
            Stats.RowSpacing = 8;
            Stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(Stats, 0, Tile(playersValue, Caption("Players")));
            Put(Stats, 1, Tile(mapValue, gameType));
            Put(Stats, 2, Tile(Heading(Edition(server)), Caption(server.Rules == HostedRules.Zpam ? "Competitive · zPAM" : "Public rules")));
            Put(Stats, 3, Tile(Heading(FormattableString.Invariant($"{server.Port}")), Caption("UDP port")));
            Stats.SizeChanged += (_, args) =>
            {
                bool narrow = args.NewSize.Width < 480;
                for (int index = 0; index < 4; index++)
                {
                    Stats.ColumnDefinitions[index].Width = narrow && index >= 2 ? new GridLength(0) : Fill;
                    var tile = (FrameworkElement)Stats.Children[index];
                    Grid.SetRow(tile, narrow ? index / 2 : 0);
                    Grid.SetColumn(tile, narrow ? index % 2 : index);
                }
            };

            playerRows.ItemsSource = inlineRows;
            showAllPlayers.Click += async (_, _) => await page.ShowAllPlayersAsync(server);
            var more = Columns(12, Fill, GridLength.Auto);
            morePlayerCount.VerticalAlignment = VerticalAlignment.Center;
            Put(more, 0, morePlayerCount);
            Put(more, 1, showAllPlayers);
            morePlayers = new Border { Padding = new Thickness(16, 12, 16, 12), Child = more };
            var joinPanel = new StackPanel { Spacing = 6, Children =
            {
                GroupTitle("How to join"), Card(joinRows), GroupTitle("Players"), full,
                Card(new StackPanel { Children = { emptyPlayers, playerRows, morePlayers } })
            } };
            full.Visibility = Visibility.Collapsed;
            AutomationProperties.SetLiveSetting(full, AutomationLiveSetting.Polite);
            log.TextWrapping = TextWrapping.Wrap;
            log.LineHeight = 18;
            log.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            log.IsTextSelectionEnabled = true;
            AutomationProperties.SetName(log, "Server log");
            tail = new ScrollViewer
            {
                Height = 295, Content = log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10), ZoomMode = ZoomMode.Disabled
            };
            var reveal = page.Action("Show in File Explorer", () => OpenAsync(page.hosting.LogFolder(server.Id)));
            reveal.Style = StyleFor("SubtleLinkAction");
            var logTitle = Columns(8, Fill, GridLength.Auto);
            var logHeading = GroupTitle("Server log");
            logHeading.VerticalAlignment = VerticalAlignment.Center;
            Put(logTitle, 0, logHeading);
            Put(logTitle, 1, reveal);
            var logPanel = new StackPanel { Spacing = 6, Children = { logTitle, Card(tail) } };
            Content.RowSpacing = 12;
            Content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(Content, 0, joinPanel);
            Put(Content, 1, logPanel);
            Content.SizeChanged += (_, args) =>
            {
                bool narrow = args.NewSize.Width < 620;
                Content.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : Fill;
                Grid.SetRow(logPanel, narrow ? 1 : 0);
                Grid.SetColumn(logPanel, narrow ? 0 : 1);
            };
            Content.Loaded += (_, _) => page.DetailScroll.SizeChanged += ResizePlayerList;
            Content.Unloaded += (_, _) => page.DetailScroll.SizeChanged -= ResizePlayerList;

            void ResizePlayerList(object sender, SizeChangedEventArgs args)
            {
                inlineLimit = args.NewSize.Height < 575 ? 3 : 6;
                UpdatePlayers(HostedDashboard.From(server, page.hosting.DashboardState(page.State(server))));
            }
        }

        internal async Task UpdateAsync(HostedRunState state)
        {
            var view = HostedDashboard.From(server, page.hosting.DashboardState(state));
            playersValue.Text = view.PlayerCount;
            mapValue.Text = view.Map;
            gameType.Text = view.GameType;
            full.Visibility = IsVisible(view.IsFull);
            join.IsEnabled = view.CanJoin;
            UpdatePlayers(view);
            var (addresses, wifi) = page.hosting.JoinAddresses(server);
            if (lastAddresses is null || !addresses.Network.SequenceEqual(lastAddresses.Network) || wifi != lastWifi)
            {
                lastAddresses = addresses;
                lastWifi = wifi;
                UpdateAddresses(addresses, wifi);
            }
            string? next = await page.hosting.ReadLogAsync(server.Id);
            if (next is { Length: > 0 } && next != log.Text)
            {
                bool follow = tail.ScrollableHeight - tail.VerticalOffset < 8;
                log.Text = next;
                if (follow)
                {
                    tail.UpdateLayout();
                    tail.ChangeView(null, tail.ScrollableHeight, null, true);
                }
            }
        }

        private void UpdateAddresses(HostedJoinAddresses addresses, bool wifi)
        {
            joinRows.Children.Clear();
            joinRows.Children.Add(AddressRow("On this PC", addresses.ThisPc));
            foreach (string address in addresses.Network)
                joinRows.Children.Add(AddressRow("On Your Network", address));
            if (!addresses.Network.IsEmpty && !addresses.InLocalList)
                joinRows.Children.Add(SettingRow("Find the server by address",
                    $"The game’s local list checks ports {HostedServer.DefaultPort}–{server.LastListedPort}.", grouped: true));
            if (addresses.Invite is { } link)
            {
                joinRows.Children.Add(SettingRow("Over the Internet",
                    $"Forward UDP port {server.Port} on your router to this PC.",
                    page.Action("How To", () => OpenAsync("https://github.com/cod2unbound/cod2-unbound/blob/main/docs/server.md#letting-players-in-from-the-internet")),
                    grouped: true));
                joinRows.Children.Add(SettingRow("Invite Link", "Opens CoD2 Unbound on your network. Share any password separately.",
                    page.Action("Copy Link", () => { page.Copy(link); return Task.CompletedTask; }), grouped: true));
            }
            if (wifi)
                joinRows.Children.Add(SettingRow("This PC Is on Wi-Fi", "A wired connection may be steadier for your players.", grouped: true));
        }

        private Border AddressRow(string title, string address)
        {
            var copy = page.Action("Copy", () => { page.Copy(address); return Task.CompletedTask; });
            AutomationProperties.SetName(copy, $"Copy {title.ToLowerInvariant()} address {address}");
            return SettingRow(title, address, copy, grouped: true);
        }

        private void UpdatePlayers(HostedDashboard view)
        {
            emptyPlayers.Visibility = IsVisible(view.Players.IsEmpty);
            morePlayers.Visibility = IsVisible(view.Players.Length > inlineLimit);
            showAllPlayers.Content = $"All {view.Players.Length} Players…";
            morePlayerCount.Text = $"{view.Players.Length - inlineLimit} more";
            UpdateRoster(inlineRows, view.Players, inlineLimit);
            updateModal?.Invoke(view.Players);
        }

        private Action<ImmutableArray<HostedStatusPlayer>>? updateModal;

        private void UpdateRoster(ObservableCollection<Border> rows, ImmutableArray<HostedStatusPlayer> roster, int limit)
        {
            var existing = rows.ToDictionary(row => ((PlayerControls)row.Tag).Identity);
            var next = new List<Border>();
            foreach (var player in roster.Take(limit))
            {
                var row = existing.GetValueOrDefault((player.Id, player.Name)) ?? PlayerRow(player);
                var controls = (PlayerControls)row.Tag;
                controls.Player = player;
                controls.Ping.Text = FormattableString.Invariant($"{player.Ping} ms");
                controls.Kick.IsEnabled = HostingConfig.Password(server.AdminPassword).Length > 0 &&
                    HostedDashboard.CanKick(roster, player);
                next.Add(row);
            }
            rows.Reconcile(next, row => ((PlayerControls)row.Tag).Identity);
        }

        private Border PlayerRow(HostedStatusPlayer player)
        {
            string name = QuakeColors.Plain(player.Name);
            var ping = Caption("");
            ping.VerticalAlignment = VerticalAlignment.Center;
            var kick = new Button { Content = "Kick" };
            var controls = new PlayerControls(player, ping, kick);
            AutomationProperties.SetName(kick, $"Kick {name}");
            kick.Click += async (_, _) => await page.shell.RunAsync(async () =>
            {
                if (!await page.hosting.KickAsync(server.Id, controls.Player))
                    page.Notify("This player is no longer available to kick.");
                await page.hosting.PollAsync();
                await page.UpdateRunStateAsync();
            });
            var row = SettingRow(name, null, Buttons(ping, kick), grouped: true);
            row.Tag = controls;
            return row;
        }

        internal async Task ShowPlayersAsync(HostedStatus status)
        {
            var rows = new ObservableCollection<Border>();
            var empty = SettingRow("No players connected", null, grouped: true);
            var list = new ScrollViewer
            {
                Content = Card(new StackPanel { Children = { empty, new ItemsControl { ItemsSource = rows } } }),
                MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var count = Caption("");
            updateModal = roster =>
            {
                count.Text = $"{roster.Length} of {status.MaxPlayers}. A player you kick can join again.";
                empty.Visibility = IsVisible(roster.IsEmpty);
                UpdateRoster(rows, roster, int.MaxValue);
            };
            updateModal(status.Players);
            try
            {
                await page.Dialog($"Players on {server.Name}", new StackPanel { Spacing = 8, Children = { count, list } },
                    close: "Done").ShowAsync();
            }
            finally { updateModal = null; }
        }

        private sealed class PlayerControls(HostedStatusPlayer player, TextBlock ping, Button kick)
        {
            internal (int Id, string Name) Identity { get; } = (player.Id, player.Name);
            internal HostedStatusPlayer Player { get; set; } = player;
            internal TextBlock Ping { get; } = ping;
            internal Button Kick { get; } = kick;
        }
        private static Border Tile(TextBlock value, TextBlock label) =>
            new() { Style = StyleFor("FieldCard"), Padding = new Thickness(10),
                Child = new StackPanel { Spacing = 2, Children = { value, label } } };
    }

    private Task ShowAllPlayersAsync(HostedServer server) => shell.ShowProblemAsync(async () =>
    {
        if (dashboard is { } live && hosting.DashboardState(State(server)).Status is { } status)
            await live.ShowPlayersAsync(status);
    });

    private static TextBlock GroupTitle(string text) => new() { Text = text, Style = StyleFor("BodyStrongTextBlockStyle") };
    private static Border Card(UIElement content) => new() { Style = StyleFor("FieldCard"), Padding = new Thickness(0), Child = content };
}
