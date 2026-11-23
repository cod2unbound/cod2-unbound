using System.Collections.Immutable;
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
        private readonly StackPanel playerRows = new() { Spacing = 1 };
        private readonly TextBlock playersValue = Heading("");
        private readonly TextBlock mapValue = Heading("");
        private readonly TextBlock gameType = Caption("");
        private readonly TextBlock log = MonoCaption("Waiting for the server log…");
        private readonly ScrollViewer tail;
        private readonly TextBlock full = Caption("Server is full.");
        private readonly List<TextBlock> pingLabels = [];
        private ImmutableArray<HostedStatusPlayer> shownPlayers;
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

            var joinPanel = new StackPanel { Spacing = 6, Children =
            {
                GroupTitle("How to join"), Card(joinRows), GroupTitle("Players"), full, Card(playerRows)
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
            page.DetailScroll.SizeChanged += ResizePlayerList;
            Content.Unloaded += (_, _) => page.DetailScroll.SizeChanged -= ResizePlayerList;

            void ResizePlayerList(object sender, SizeChangedEventArgs args)
            {
                inlineLimit = args.NewSize.Height < 575 ? 3 : 6;
                shownPlayers = default;
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
            bool samePlayers = !shownPlayers.IsDefault && view.Players.Select(row => (row.Id, row.Name))
                .SequenceEqual(shownPlayers.Select(row => (row.Id, row.Name)));
            shownPlayers = view.Players;
            if (samePlayers)
            {
                for (int index = 0; index < pingLabels.Count; index++)
                    pingLabels[index].Text = Ping(view.Players[index]);
                return;
            }
            FillPlayers(playerRows, view.Players, inlineLimit, pingLabels, modal: false);
            if (view.Players.Length > inlineLimit)
            {
                var all = new Button { Content = $"All {view.Players.Length} Players…" };
                all.Click += async (_, _) => await page.ShowAllPlayersAsync(server);
                playerRows.Children.Add(SettingRow($"{view.Players.Length - inlineLimit} more", null, all, grouped: true));
            }
        }

        internal void FillPlayers(StackPanel target, ImmutableArray<HostedStatusPlayer> roster, int limit,
            List<TextBlock>? labels, bool modal)
        {
            target.Children.Clear();
            labels?.Clear();
            if (roster.IsEmpty)
                target.Children.Add(SettingRow("No players connected", null, grouped: true));
            foreach (var player in roster.Take(limit))
            {
                string name = QuakeColors.Plain(player.Name);
                var ping = Caption(Ping(player));
                ping.VerticalAlignment = VerticalAlignment.Center;
                labels?.Add(ping);
                var kick = new Button
                {
                    Content = "Kick", IsEnabled = HostingConfig.Password(server.AdminPassword).Length > 0 &&
                        HostedDashboard.CanKick(roster, player)
                };
                AutomationProperties.SetName(kick, $"Kick {name}");
                kick.Click += async (_, _) => await page.shell.RunAsync(async () =>
                {
                    if (!await page.hosting.KickAsync(server.Id, player))
                        page.Notify("This player is no longer available to kick.");
                    await page.hosting.PollAsync();
                    await page.UpdateRunStateAsync();
                    if (modal)
                        FillPlayers(target, page.State(server).Status?.Players ?? [], limit, null, modal: true);
                });
                target.Children.Add(SettingRow(name, null, Buttons(ping, kick), grouped: true));
            }
        }

        private static string Ping(HostedStatusPlayer player) => FormattableString.Invariant($"{player.Ping} ms");
        private static Border Tile(TextBlock value, TextBlock label) =>
            new() { Style = StyleFor("FieldCard"), Padding = new Thickness(10),
                Child = new StackPanel { Spacing = 2, Children = { value, label } } };
    }

    private Task ShowAllPlayersAsync(HostedServer server) => shell.ShowProblemAsync(async () =>
    {
        if (dashboard is not { } live || hosting.DashboardState(State(server)).Status is not { } status)
            return;
        var rows = new StackPanel();
        live.FillPlayers(rows, status.Players, int.MaxValue, null, modal: true);
        var list = new ScrollViewer { Content = Card(rows), MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = Dialog($"Players on {server.Name}", new StackPanel { Spacing = 8, Children =
        {
            Caption($"{status.Players.Length} of {status.MaxPlayers}. A player you kick can join again."), list
        } }, close: "Done");
        await dialog.ShowAsync();
    });

    private static TextBlock GroupTitle(string text) => new() { Text = text, Style = StyleFor("BodyStrongTextBlockStyle") };
    private static Border Card(UIElement content) => new() { Style = StyleFor("FieldCard"), Padding = new Thickness(0), Child = content };
}
