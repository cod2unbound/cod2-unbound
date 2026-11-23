using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
        private readonly ComboBox edition;
        private readonly ComboBox reach;
        private static readonly int[] PlayerSlots = [2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 32];
        private readonly NumberBox slots;
        private readonly NumberBox port;
        private readonly PasswordBox join;
        private readonly PasswordBox admin;
        private readonly ComboBox rules;
        private readonly ComboBox type;
        private readonly ComboBox match;
        private readonly ToggleSwitch friendly;
        private readonly List<string> maps;
        private readonly StackPanel mapRows = new() { Spacing = 3 };
        private readonly StackPanel zpamRows = new() { Spacing = 6 };
        private readonly StackPanel publicRules;
        private readonly TextBlock editionHelp = Caption("");
        private readonly TextBlock validation = Caption("");
        private readonly Button start;
        private readonly Button save;
        private readonly Button? retryPort;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
        private readonly CancellationTokenSource lifetime = new();
        private ZpamDownloadState? shownZpam;

        internal bool IsNew { get; }
        internal bool EditingRunning { get; }
        internal StackPanel Content { get; } = new() { Spacing = 12 };

        internal HostForm(HostingPage page, HostedServer server, bool isNew, bool editingRunning)
        {
            this.page = page;
            this.server = server;
            IsNew = isNew;
            EditingRunning = editingRunning;
            name = new() { Header = "Server name", Text = server.Name, MaxLength = 64 };
            edition = Choice("Game version", ["CoD2x 1.4.6.8", "Original 1.3"], (int)server.Engine);
            reach = Choice("Who can join", ["Local network", "Public server list"], page.hosting.LoopbackOnly ? 0 : (int)server.Reach);
            reach.IsEnabled = !page.hosting.LoopbackOnly;
            slots = Number("Player slots", 2, 64, server.Players);
            port = Number("UDP port", 1025, 65535, server.Port);
            join = new() { Header = "Password to join", Password = server.JoinPassword, MaxLength = 32, PasswordRevealMode = PasswordRevealMode.Hidden };
            admin = new() { Header = "Admin password", Password = server.AdminPassword, MaxLength = 32, PasswordRevealMode = PasswordRevealMode.Peek };
            rules = Choice("Rules", ["Public", "Competitive · zPAM"], (int)server.Rules);
            type = Choice("Game type", HostingCatalog.GameTypes.Select(item => item.Title).ToArray(),
                HostingCatalog.GameTypes.IndexOf((server.GameType, HostingCatalog.GameTypeTitle(server.GameType))));
            match = Choice("Match mode", HostingCatalog.MatchModes.Select(item => item.Title).ToArray(),
                HostingCatalog.MatchModes.IndexOf((server.MatchMode, HostingCatalog.MatchModeTitle(server.MatchMode))));
            friendly = new() { Header = "Friendly fire", IsOn = server.FriendlyFire };
            maps = [.. HostingConfig.Maps(server)];

            Content.Children.Add(Heading(isNew ? "New server" : server.Name));
            start = page.Action("Start server", () => PersistAsync(startServer: true), "R");
            start.Style = StyleFor("LauncherAccentButton");
            start.Visibility = IsVisible(!editingRunning);
            save = page.Action(isNew ? "Save server" : "Save changes", () => PersistAsync(startServer: false), "S");
            if (editingRunning)
            {
                var phase = Caption("");
                page.phaseText = phase;
                page.UpdatePhaseText(page.State(server));
                Content.Children.Add(phase);
                save.Style = StyleFor("LauncherAccentButton");
                Content.Children.Add(Buttons(page.Action("Cancel", page.ShowServerAsync, "U"), save));
                Content.Children.Add(new InfoBar
                {
                    IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
                    Message = "Changes apply the next time this server starts."
                });
                name.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
            }
            else
            {
                Content.Children.Add(isNew ? Buttons(start, save, page.Action("Cancel", page.ShowServerAsync, "U"))
                    : Buttons(start, save));
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
                    Title = state.Phase == HostedPhase.PortBusy ? $"UDP port {server.Port} is already in use." : "The server couldn’t start.",
                    Message = retryPort is null ? state.Problem : "Another server or a running game is using it.",
                    ActionButton = retryPort
                });
            Content.Children.Add(validation);

            var settings = new StackPanel { Spacing = 8, Padding = new Thickness(12),
                Children = { name, edition, editionHelp, reach, slots, port, join, admin } };
            publicRules = new StackPanel { Spacing = 8, Children = { type, friendly } };
            var matchSettings = new StackPanel { Spacing = 8, Padding = new Thickness(12),
                Children = { rules, publicRules, zpamRows } };
            var serverColumn = new StackPanel { Spacing = 6, Children = { GroupTitle("Server"), Card(settings) } };
            var matchColumn = new StackPanel { Spacing = 6, Children = { GroupTitle("Match"), Card(matchSettings),
                GroupTitle("Maps, in Order"), Card(mapRows) } };
            GridLength fill = new(1, GridUnitType.Star);
            var columns = Columns(12, fill, fill);
            columns.RowSpacing = 12;
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(columns, 0, serverColumn);
            Put(columns, 1, matchColumn);
            columns.SizeChanged += (_, args) =>
            {
                bool narrow = args.NewSize.Width < 520;
                columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : fill;
                Grid.SetColumn(matchColumn, narrow ? 0 : 1);
                Grid.SetRow(matchColumn, narrow ? 1 : 0);
            };
            Content.Children.Add(columns);

            if (!isNew && !editingRunning)
                Content.Children.Add(Buttons(page.Action(DeleteTitle, () => page.DeleteServerAsync(server)),
                    page.Action("Open log folder", () => OpenAsync(page.hosting.LogFolder(server.Id)))));

            timer = page.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(200);
            timer.Tick += (_, _) => ReadZpamState();
            name.TextChanged += (_, _) => Validate();
            slots.ValueChanged += (_, _) => Validate();
            port.ValueChanged += (_, _) => Validate();
            join.PasswordChanged += (_, _) => Validate();
            admin.PasswordChanged += (_, _) => Validate();
            edition.SelectionChanged += async (_, _) => await RuleChangedAsync();
            rules.SelectionChanged += async (_, _) => await RuleChangedAsync();
            RenderMaps();
            _ = RuleChangedAsync();
        }

        private bool Competitive => rules.SelectedIndex == (int)HostedRules.Zpam;
        private ZpamLine SelectedLine => edition.SelectedIndex == (int)GameEdition.Cod2x ? ZpamLine.Four : ZpamLine.Three;

        private async Task RuleChangedAsync()
        {
            editionHelp.Text = edition.SelectedIndex == (int)GameEdition.Cod2x
                ? "Players can join with CoD2x 1.4.6.8 or newer."
                : "Players can join with Original 1.3.";
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
            // Preserve focus on Cancel while bytes arrive.
            if (state.Phase == ZpamDownloadPhase.Getting && shownZpam?.Phase == ZpamDownloadPhase.Getting &&
                state.Release == shownZpam.Release && zpamRows.Children[1] is ProgressBar current)
            {
                current.Value = state.Received;
                shownZpam = state;
                return;
            }
            shownZpam = state;
            zpamRows.Children.Clear();
            var line = SelectedLine;
            switch (state)
            {
                case { Phase: ZpamDownloadPhase.Missing, Release: { } release }:
                    zpamRows.Children.Add(Body($"zPAM {release.Version} is available ({release.Bytes / 1_048_576d:0.#} MB)."));
                    var get = new Button { Content = $"Get zPAM {release.Version}", Style = StyleFor("LauncherAccentButton") };
                    get.Click += async (_, _) => await page.shell.ShowProblemAsync(async () =>
                    {
                        await page.hosting.Zpam.GetAsync(line, lifetime.Token);
                        ReadZpamState();
                    });
                    zpamRows.Children.Add(get);
                    break;
                case { Phase: ZpamDownloadPhase.Getting, Release: { } release }:
                    zpamRows.Children.Add(Body($"Getting zPAM {release.Version}…"));
                    var progress = new ProgressBar { Maximum = release.Bytes, Value = state.Received };
                    AutomationProperties.SetName(progress, "Downloading zPAM");
                    zpamRows.Children.Add(progress);
                    var cancel = new Button { Content = "Cancel Download" };
                    cancel.Click += (_, _) => page.hosting.Zpam.Cancel(line);
                    zpamRows.Children.Add(cancel);
                    break;
                case { Phase: ZpamDownloadPhase.Ready }:
                    zpamRows.Children.Add(Body($"zPAM {state.Version} · Installed"));
                    zpamRows.Children.Add(match);
                    zpamRows.Children.Add(Caption("Players get maps from cod2x.me."));
                    break;
                case { Phase: ZpamDownloadPhase.Failed }:
                    zpamRows.Children.Add(Body(state.Problem ?? "zPAM couldn’t be loaded."));
                    var retry = new Button { Content = "Try Again" };
                    retry.Click += async (_, _) => await LookUpAsync(line);
                    zpamRows.Children.Add(retry);
                    break;
                default:
                    zpamRows.Children.Add(Body("Looking up zPAM…"));
                    zpamRows.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
                    break;
            }
            Validate();
        }

        private void RenderMaps()
        {
            mapRows.Children.Clear();
            for (int index = 0; index < maps.Count; index++)
            {
                int position = index;
                var earlier = MapAction("ChevronUp", $"Move {MapCatalog.DisplayName(maps[index])} earlier",
                    () => Move(position, -1), index > 0);
                var later = MapAction("ChevronDown", $"Move {MapCatalog.DisplayName(maps[index])} later",
                    () => Move(position, 1), index + 1 < maps.Count);
                var remove = new Button { Content = "Remove", IsEnabled = maps.Count > 1 };
                AutomationProperties.SetName(remove, $"Remove {MapCatalog.DisplayName(maps[index])}");
                remove.Click += (_, _) => { maps.RemoveAt(position); RenderMaps(); };
                mapRows.Children.Add(SettingRow($"{index + 1}. {MapCatalog.DisplayName(maps[index])}", null,
                    Buttons(earlier, later, remove), grouped: true));
            }

            var add = page.Action("Add Map…", AddMapAsync);
            add.IsEnabled = HostingCatalog.Maps.Any(map => !maps.Contains(map));
            add.Margin = new Thickness(12, 4, 12, 10);
            mapRows.Children.Add(add);

            void Move(int index, int offset)
            {
                int other = index + offset;
                (maps[index], maps[other]) = (maps[other], maps[index]);
                RenderMaps();
            }
        }

        private async Task AddMapAsync()
        {
            var options = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (int index = 0; index < 3; index++)
                options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dialog = page.Dialog("Add a Map", new ScrollViewer
            {
                Content = options, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }, close: "Done");
            var previews = new MapPreviews(map => Task.Run(() => page.session.ReadMapArtwork(map)));
            int indexInGrid = 0;
            foreach (var map in HostingCatalog.Maps.Where(map => !maps.Contains(map)))
            {
                int column = indexInGrid % 3, row = indexInGrid / 3;
                if (column == 0)
                    options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var image = new Image { Width = 90, Height = 60, Stretch = Stretch.UniformToFill };
                var title = Caption(MapCatalog.DisplayName(map));
                var tile = new Button { Content = new StackPanel { Spacing = 3, Children = { image, title } } };
                AutomationProperties.SetName(tile, $"Add {title.Text}");
                tile.Click += (_, _) => { maps.Add(map); RenderMaps(); dialog.Hide(); };
                Grid.SetRow(tile, row);
                Put(options, column, tile);
                _ = previews.FillAsync(image, map);
                indexInGrid++;
            }
            await dialog.ShowAsync();
        }

        private HostedServer Current() => server with
        {
            Name = HostingConfig.Safe(name.Text, 64),
            Engine = (GameEdition)edition.SelectedIndex,
            Reach = (HostedReach)reach.SelectedIndex,
            Port = (int)port.Value,
            Players = (int)slots.Value,
            JoinPassword = HostingConfig.Password(join.Password),
            AdminPassword = HostingConfig.Password(admin.Password),
            Rules = (HostedRules)rules.SelectedIndex,
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
                : !double.IsInteger(slots.Value) || !PlayerSlots.Contains((int)slots.Value)
                    ? "Choose 2, 4, 6, 8, 10, 12, 14, 16, 20, 24 or 32 player slots."
                : !double.IsInteger(port.Value) || port.Value < 1025 || port.Value > 65535 ? "Enter a valid UDP port."
                : HostingConfig.Password(join.Password) != join.Password || HostingConfig.Password(admin.Password) != admin.Password
                    ? "Passwords can’t contain spaces or quotes." : "";
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

        private static ComboBox Choice(string header, string[] options, int selection) => new()
        {
            Header = header, ItemsSource = options, SelectedIndex = Math.Max(0, selection), HorizontalAlignment = HorizontalAlignment.Stretch
        };

        private static NumberBox Number(string header, int minimum, int maximum, int value) => new()
        {
            Header = header, Minimum = minimum, Maximum = maximum, Value = value,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };

        private static Button MapAction(string icon, string label, Action action, bool enabled)
        {
            var button = new Button { Content = Fluent(icon, 14), Style = StyleFor("IconAction"), IsEnabled = enabled };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            button.Click += (_, _) => action();
            return button;
        }
    }
}
