using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HostingPage
{
    private void EndForm()
    {
        form?.Dispose();
        form = null;
    }

    private void ShowForm(HostedServer server, bool isNew, bool editingRunning = false)
    {
        form = new HostForm(this, server, isNew, editingRunning);
        Details.Children.Add(form.Content);
    }

    private sealed class HostForm : IDisposable
    {
        private readonly HostingPage page;
        private readonly HostedServer server;
        private readonly TextBox name;
        private readonly SelectorBar edition;
        private readonly ComboBox reach;
        private static readonly int[] PlayerSlots = [2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 32];
        private readonly ComboBox slots;
        private readonly NumberBox port;
        private readonly PasswordBox join;
        private readonly PasswordBox admin;
        private readonly SelectorBar rules;
        private readonly ComboBox type;
        private readonly ComboBox match;
        private readonly ToggleSwitch friendly;
        private readonly List<string> maps;
        private readonly Grid mapRows = new() { RowSpacing = 12, ColumnSpacing = 10, Padding = new Thickness(12) };
        private readonly MapPreviews previews;
        private readonly StackPanel zpamRows = new();
        private readonly StackPanel publicRules;
        private readonly TextBlock editionHelp = Caption("");
        private readonly TextBlock reachHelp = Caption("");
        private readonly TextBlock rulesHelp = Caption("");
        private readonly TextBlock formHint = Caption("");
        private readonly TextBlock validation = Caption("");
        private ProgressBar? downloadProgress;
        private TextBlock? downloadSize;
        private readonly Button start;
        private readonly Button save;
        private readonly Button? retryPort;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
        private readonly CancellationTokenSource lifetime = new();
        private ZpamDownloadState? shownZpam;

        internal bool IsNew { get; }
        internal bool EditingRunning { get; }
        internal StackPanel Content { get; } = new() { Spacing = 14 };

        internal HostForm(HostingPage page, HostedServer server, bool isNew, bool editingRunning)
        {
            this.page = page;
            this.server = server;
            IsNew = isNew;
            EditingRunning = editingRunning;
            previews = new(map => Task.Run(() => page.session.ReadMapArtwork(map)));
            name = new() { Text = server.Name, MaxLength = 64, Width = 200 };
            AutomationProperties.SetName(name, "Server name");
            edition = Segments("Game version", ["CoD2x", "Original 1.3"], (int)server.Engine);
            reach = Choice("Who can join", ["My Network", "The Internet"], page.hosting.LoopbackOnly ? 0 : (int)server.Reach);
            reach.IsEnabled = !page.hosting.LoopbackOnly;
            var values = PlayerSlots.Append(server.Players).Distinct().Order().ToArray();
            slots = Choice("Player slots", values.Select(value => value.ToString()).ToArray(), Array.IndexOf(values, server.Players));
            port = Number("UDP port", 1025, 65535, server.Port);
            join = new() { Password = server.JoinPassword, MaxLength = 32, PasswordRevealMode = PasswordRevealMode.Hidden,
                PlaceholderText = "None", Width = 180 };
            AutomationProperties.SetName(join, "Password to join");
            admin = new() { Password = server.AdminPassword, MaxLength = 32, PasswordRevealMode = PasswordRevealMode.Peek, Width = 122 };
            AutomationProperties.SetName(admin, "Admin password");
            rules = Segments("Rules", ["Public", "Competitive · zPAM"], (int)server.Rules);
            type = Choice("Game type", HostingCatalog.GameTypes.Select(item => item.Title).ToArray(),
                HostingCatalog.GameTypes.IndexOf((server.GameType, HostingCatalog.GameTypeTitle(server.GameType))));
            match = Choice("Match mode", HostingCatalog.MatchModes.Select(item => item.Title).ToArray(),
                HostingCatalog.MatchModes.IndexOf((server.MatchMode, HostingCatalog.MatchModeTitle(server.MatchMode))));
            friendly = new() { MinWidth = 0, IsOn = server.FriendlyFire };
            AutomationProperties.SetName(friendly, "Friendly fire");
            maps = [.. HostingConfig.Maps(server)];

            start = page.Action("Start Server", () => PersistAsync(startServer: true), "R");
            AutomationProperties.SetName(start, "Start server");
            start.Style = StyleFor("LauncherAccentButton");
            start.Visibility = IsVisible(!editingRunning);
            save = page.Action(isNew ? "Save Server" : "Save Changes", () => PersistAsync(startServer: false), "S");
            AutomationProperties.SetName(save, isNew ? "Save server" : "Save changes");
            if (editingRunning)
            {
                var phase = Caption("");
                page.phaseText = phase;
                page.UpdatePhaseText(page.State(server));
                save.Style = StyleFor("LauncherAccentButton");
                var actions = Buttons(page.Action("Cancel", page.ShowServerAsync, "U"), save);
                Content.Children.Add(RunningHeader(server.Name, phase, actions));
                Content.Children.Add(new InfoBar
                {
                    IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
                    Message = "Changes apply the next time this server starts."
                });
                name.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
            }
            else
            {
                var alternate = isNew ? page.Action("Cancel", page.CancelDraftAsync, "U")
                    : page.Action("Delete Server…", () => page.DeleteServerAsync(server), "D");
                Content.Children.Add(RunningHeader(isNew ? "New server" : server.Name, formHint, Buttons(alternate, start)));
            }

            var state = page.State(server);
            if (state is { Phase: HostedPhase.PortBusy, NextPort: { } next })
            {
                retryPort = page.Action($"Use {next} and Start", async () =>
                {
                    port.Value = next;
                    await PersistAsync(startServer: true);
                });
            }
            if (state.Phase is HostedPhase.PortBusy or HostedPhase.Failed)
                Content.Children.Add(new InfoBar
                {
                    IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error,
                    Title = state.Phase == HostedPhase.PortBusy ? $"The server couldn’t start: port {server.Port} is already in use." : "The server couldn’t start",
                    Message = retryPort is null ? state.Problem : $"Another server or a running game is using it. The next free port is {state.NextPort}.",
                    ActionButton = retryPort
                });
            Content.Children.Add(validation);

            var settings = GroupRows(
                FormRow("Name", Caption("What players see in the server list."), name),
                FormRow("Game version", editionHelp, edition),
                FormRow("Who can join", reachHelp, reach),
                FormRow("Players", null, slots),
                FormRow("Password to join", Caption("Leave empty for an open server."), join),
                FormRow("Admin password", Caption("For controlling the server from inside the game. Kept on this PC."), admin));
            publicRules = GroupRows(FormRow("Game type", null, type), FormRow("Friendly fire", null, friendly));
            var matchSettings = GroupRows(FormRow("Rules", rulesHelp, rules), publicRules, zpamRows);
            var serverColumn = new StackPanel { Spacing = 6, Children = { GroupTitle("Server"), Card(settings) } };
            var mapHint = Caption("Use the map menu to reorder. The server starts on the first map.");
            var matchColumn = new StackPanel { Spacing = 6, Children = { GroupTitle("Match"), Card(matchSettings),
                GroupTitle("Maps, in order"), Card(mapRows), mapHint } };
            GridLength fill = new(1, GridUnitType.Star);
            var columns = Columns(20, fill, fill);
            columns.RowSpacing = 12;
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(columns, 0, serverColumn);
            Put(columns, 1, matchColumn);
            columns.SizeChanged += (_, args) =>
            {
                bool narrow = args.NewSize.Width < 720;
                columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : fill;
                Grid.SetColumn(matchColumn, narrow ? 0 : 1);
                Grid.SetRow(matchColumn, narrow ? 1 : 0);
            };
            Content.Children.Add(columns);

            if (!editingRunning)
            {
                var advanced = Buttons(port, save);
                Content.Children.Add(advanced);
            }

            timer = page.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(200);
            timer.Tick += (_, _) => ReadZpamState();
            name.TextChanged += (_, _) => Validate();
            slots.SelectionChanged += (_, _) => Validate();
            reach.SelectionChanged += (_, _) => UpdateReachHelp();
            UpdateReachHelp();
            port.ValueChanged += (_, _) => Validate();
            join.PasswordChanged += (_, _) => Validate();
            admin.PasswordChanged += (_, _) => Validate();
            edition.SelectionChanged += async (_, _) => await RuleChangedAsync();
            rules.SelectionChanged += async (_, _) => await RuleChangedAsync();
            RenderMaps();
            _ = RuleChangedAsync();
        }

        private bool Competitive => SelectedIndex(rules) == (int)HostedRules.Zpam;
        private ZpamLine SelectedLine => SelectedIndex(edition) == (int)GameEdition.Cod2x ? ZpamLine.Four : ZpamLine.Three;

        private async Task RuleChangedAsync()
        {
            editionHelp.Text = SelectedIndex(edition) == (int)GameEdition.Cod2x
                ? "Anyone with CoD2x 1.4.6.8 or newer can join, on Mac, Windows or Linux. A running CoD2x server asks CoD2x’s update service about new versions."
                : "Anyone with the original 1.3 can join, on Mac or Windows.";
            rulesHelp.Text = Competitive ? "Match rules, ready-up and strict settings for every player."
                : "The game’s own rules, for casual play.";
            shownZpam = null;
            publicRules.Visibility = IsVisible(!Competitive);
            zpamRows.Visibility = IsVisible(Competitive);
            Validate();
            if (Competitive)
            {
                timer.Start();
                if (page.hosting.Zpam.State(SelectedLine).Phase == ZpamDownloadPhase.Unknown)
                    await LookUpAsync(SelectedLine);
                ReadZpamState();
            }
            else
                timer.Stop();
        }

        private async Task LookUpAsync(ZpamLine line)
        {
            try { await page.hosting.Zpam.LookUpAsync(line, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        private void ReadZpamState()
        {
            if (lifetime.IsCancellationRequested || !Competitive)
                return;
            var state = page.hosting.Zpam.State(SelectedLine);
            if (state == shownZpam)
                return;
            if (state.Phase == ZpamDownloadPhase.Getting && shownZpam?.Phase == ZpamDownloadPhase.Getting &&
                state.Release == shownZpam.Release && downloadProgress is not null && downloadSize is not null)
            {
                downloadProgress.Value = state.Received;
                downloadSize.Text = DownloadSize(state.Received, state.Release?.Bytes ?? 0);
                shownZpam = state;
                return;
            }
            shownZpam = state;
            zpamRows.Children.Clear();
            downloadProgress = null;
            downloadSize = null;
            var line = SelectedLine;
            switch (state)
            {
                case { Phase: ZpamDownloadPhase.Missing, Release: { } release }:
                    var get = new Button { Content = $"Get zPAM {release.Version}", Style = StyleFor("LauncherAccentButton") };
                    get.Click += async (_, _) => await page.shell.ShowProblemAsync(async () =>
                    {
                        await page.hosting.Zpam.GetAsync(line, lifetime.Token);
                        ReadZpamState();
                    });
                    zpamRows.Children.Add(FormRow("zPAM isn’t on this PC yet", Caption(
                        $"The match mod competitive servers run, made by the author of CoD2x. {release.Bytes / 1_048_576d:0.#} MB with its map pack. Checked after download and kept beside your servers."), get));
                    break;
                case { Phase: ZpamDownloadPhase.Getting, Release: { } release }:
                    downloadProgress = new ProgressBar { Maximum = release.Bytes, Value = state.Received, Width = 90, VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetName(downloadProgress, "Downloading zPAM");
                    downloadSize = Caption(DownloadSize(state.Received, release.Bytes));
                    var cancel = new Button { Content = "Cancel" };
                    AutomationProperties.SetName(cancel, "Cancel Download");
                    cancel.Click += (_, _) => page.hosting.Zpam.Cancel(line);
                    zpamRows.Children.Add(FormRow($"Getting zPAM {release.Version}…", downloadSize, Buttons(downloadProgress, cancel)));
                    break;
                case { Phase: ZpamDownloadPhase.Ready }:
                    zpamRows.Children.Add(FormRow($"zPAM {state.Version}", Caption(SelectedLine == ZpamLine.Four
                        ? "Installed. A running match server checks CoD2x’s update service and keeps zPAM current by itself."
                        : "Installed, for the original 1.3."), ReadyLabel("Ready")));
                    zpamRows.Children.Add(FormRow("Match mode", Caption("zPAM’s own name for the format."), match));
                    zpamRows.Children.Add(FormRow("Players get maps from", Caption("zPAM’s map pack is fetched by each player’s game from this address."),
                        Caption("cod2x.me · zPAM’s default")));
                    break;
                case { Phase: ZpamDownloadPhase.Failed }:
                    var retry = new Button { Content = "Try Again" };
                    retry.Click += async (_, _) => await LookUpAsync(line);
                    zpamRows.Children.Add(FormRow("zPAM couldn’t be looked up", Caption(state.Problem ?? "zPAM couldn’t be loaded."), retry));
                    break;
                default:
                    zpamRows.Children.Add(FormRow("Looking up zPAM…", null, new ProgressRing { IsActive = true, Width = 20, Height = 20 }));
                    break;
            }
            Validate();
        }

        private void RenderMaps()
        {
            mapRows.Children.Clear();
            mapRows.RowDefinitions.Clear();
            mapRows.ColumnDefinitions.Clear();
            for (int column = 0; column < 4; column++)
                mapRows.ColumnDefinitions.Add(new ColumnDefinition());
            for (int index = 0; index < maps.Count; index++)
            {
                int position = index;
                string map = maps[index];
                string title = MapCatalog.DisplayName(map);
                var tile = MapTile(map, title);
                AutomationProperties.SetName(tile, $"{title}, map {index + 1}");
                var menu = new MenuFlyout();
                menu.Items.Add(MapCommand("Move Earlier", index > 0, () => Move(position, -1)));
                menu.Items.Add(MapCommand("Move Later", index + 1 < maps.Count, () => Move(position, 1)));
                menu.Items.Add(MapCommand($"Remove {title}", maps.Count > 1, () => { maps.RemoveAt(position); RenderMaps(); }));
                tile.Flyout = menu;
                PlaceMap(mapRows, tile, index);
            }

            var add = new Button { Style = StyleFor("SetupCard"), IsEnabled = HostingCatalog.Maps.Any(map => !maps.Contains(map)),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel { Spacing = 6, Children = { Fluent("Add", 18), Caption("Add Map") } } };
            AutomationProperties.SetName(add, "Add Map…");
            var picker = new Flyout { Placement = FlyoutPlacementMode.Left };
            picker.Opening += (_, _) => picker.Content = MapChoices(picker, add);
            add.Flyout = picker;
            PlaceMap(mapRows, add, maps.Count);

            void Move(int index, int offset)
            {
                int other = index + offset;
                (maps[index], maps[other]) = (maps[other], maps[index]);
                RenderMaps();
            }
        }

        private StackPanel MapChoices(Flyout picker, Button anchor)
        {
            var available = HostingCatalog.Maps.Where(map => !maps.Contains(map)).ToArray();
            var options = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
            for (int index = 0; index < 4; index++)
                options.ColumnDefinitions.Add(new ColumnDefinition());
            for (int index = 0; index < available.Length; index++)
            {
                string map = available[index];
                string title = MapCatalog.DisplayName(map);
                var tile = MapTile(map, title);
                AutomationProperties.SetName(tile, $"Add {title}");
                tile.Click += (_, _) =>
                {
                    picker.Hide();
                    maps.Add(map);
                    RenderMaps();
                    if (mapRows.Children[^1] is Button next && next.IsEnabled)
                        next.Focus(FocusState.Programmatic);
                };
                PlaceMap(options, tile, index);
            }
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
                Children = { Heading("Add a map"), Caption($"{available.Length} not in the rotation") } };
            var done = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right };
            done.Click += (_, _) => { picker.Hide(); anchor.Focus(FocusState.Programmatic); };
            return new StackPanel { Width = 460, Spacing = 10, Children =
            {
                header,
                new ScrollViewer { Content = options, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                Caption("Click a map to add it to the end of the rotation."),
                done
            } };
        }

        private Button MapTile(string map, string title)
        {
            var image = new Image { Height = 60, Stretch = Stretch.UniformToFill };
            _ = previews.FillAsync(image, map);
            return new Button { Style = StyleFor("SetupCard"), HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 4, Children =
                {
                    new Border { Style = StyleFor("MediaTile"), Child = image },
                    new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }
                } } };
        }

        private static void PlaceMap(Grid grid, FrameworkElement tile, int index)
        {
            int row = index / 4, column = index % 4;
            if (column == 0)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(tile, row);
            Put(grid, column, tile);
        }

        private static MenuFlyoutItem MapCommand(string text, bool enabled, Action perform)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => perform();
            return item;
        }

        private void UpdateReachHelp() => reachHelp.Text = reach.SelectedIndex == (int)HostedReach.Listed
            ? "Listed publicly once your router lets players in." : "Macs and PCs on the same network.";

        private static string DownloadSize(long received, long total) => $"{received / 1_048_576d:0.#} of {total / 1_048_576d:0.#} MB";

        private HostedServer Current() => server with
        {
            Name = HostingConfig.Safe(name.Text, 64),
            Engine = (GameEdition)SelectedIndex(edition),
            Reach = (HostedReach)reach.SelectedIndex,
            Port = (int)port.Value,
            Players = int.Parse((string)slots.SelectedItem, System.Globalization.CultureInfo.InvariantCulture),
            JoinPassword = HostingConfig.Password(join.Password),
            AdminPassword = HostingConfig.Password(admin.Password),
            Rules = (HostedRules)SelectedIndex(rules),
            GameType = HostingCatalog.GameTypes[type.SelectedIndex].Id,
            MatchMode = HostingCatalog.MatchModes[match.SelectedIndex].Id,
            FriendlyFire = friendly.IsOn,
            Maps = [.. maps]
        };

        private async Task PersistAsync(bool startServer)
        {
            Validate();
            if (!(startServer ? start.IsEnabled : save.IsEnabled))
                return;
            var current = Current();
            if (IsNew && current.AdminPassword.Length == 0)
                current = current with { AdminPassword = HostedServer.RandomPassword() };
            await page.hosting.SaveAsync(current);
            if (startServer)
            {
                try { await page.ToggleAsync(current, refresh: false); }
                finally { await page.RefreshAsync(current.Id); }
            }
            else
                await page.RefreshAsync(current.Id);
        }

        private void Validate()
        {
            validation.Text = HostingConfig.Safe(name.Text, 64).Length == 0 ? "Enter a server name."
                : !double.IsInteger(port.Value) || port.Value < 1025 || port.Value > 65535 ? "Enter a valid UDP port."
                : HostingConfig.Password(join.Password) != join.Password || HostingConfig.Password(admin.Password) != admin.Password
                    ? "Passwords can’t contain spaces or quotes." : "";
            formHint.Text = Competitive ? page.hosting.Zpam.State(SelectedLine).Phase switch
            {
                ZpamDownloadPhase.Ready => $"Port {port.Value:0}",
                ZpamDownloadPhase.Getting => "Start is ready when zPAM is.",
                _ => "Competitive rules need zPAM first."
            } : $"Port {port.Value:0}";
            validation.Visibility = IsVisible(validation.Text.Length > 0);
            save.IsEnabled = validation.Text.Length == 0;
            start.IsEnabled = save.IsEnabled && (!Competitive || page.hosting.Zpam.State(SelectedLine).Phase == ZpamDownloadPhase.Ready);
            if (retryPort is not null)
                retryPort.IsEnabled = start.IsEnabled;
        }

        public void Dispose()
        {
            timer.Stop();
            lifetime.Cancel();
            lifetime.Dispose();
        }

        private static int SelectedIndex(SelectorBar bar) => bar.Items.IndexOf(bar.SelectedItem);

        private static SelectorBar Segments(string label, string[] options, int selection)
        {
            var bar = new SelectorBar();
            foreach (string option in options)
                bar.Items.Add(new SelectorBarItem { Text = option });
            bar.SelectedItem = bar.Items[selection];
            AutomationProperties.SetName(bar, label);
            return bar;
        }

        private static ComboBox Choice(string label, string[] options, int selection)
        {
            var combo = new ComboBox { ItemsSource = options, SelectedIndex = Math.Max(0, selection) };
            AutomationProperties.SetName(combo, label);
            return combo;
        }

        private static NumberBox Number(string header, int minimum, int maximum, int value) => new()
        {
            Header = header, Minimum = minimum, Maximum = maximum, Value = value, Width = 125,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
    }

    private static Grid FormRow(string title, TextBlock? detail, FrameworkElement action)
    {
        var row = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        row.Style = StyleFor("FormRow");
        var label = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Put(row, 0, label);
        Put(row, 1, action);
        action.VerticalAlignment = VerticalAlignment.Center;
        if (detail is not null)
        {
            row.RowDefinitions.Add(new RowDefinition());
            row.RowDefinitions.Add(new RowDefinition());
            row.RowSpacing = 3;
            Grid.SetColumnSpan(detail, 2);
            Grid.SetRow(detail, 1);
            row.Children.Add(detail);
        }
        return row;
    }

    private static StackPanel GroupRows(params FrameworkElement[] rows)
    {
        var group = new StackPanel();
        foreach (var row in rows)
        {
            if (group.Children.Count != 0)
                group.Children.Add(new Border { Style = StyleFor("CardDivider"), Height = 1 });
            group.Children.Add(row);
        }
        return group;
    }

    private static StackPanel ReadyLabel(string title)
    {
        var icon = Fluent("Check", 15);
        icon.Style = StyleFor("PositiveIcon");
        return Buttons(icon, new TextBlock { Text = title, Style = StyleFor("PositiveCaption") });
    }
}
