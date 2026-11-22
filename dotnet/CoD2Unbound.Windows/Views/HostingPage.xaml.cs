using System.Collections.Immutable;
using System.Globalization;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HostingPage : Page
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly WindowsHosting hosting;
    private ImmutableArray<HostedServer> hosts = [];
    private bool syncing;
    private TextBlock? phaseText;
    private TextBlock? detailText;
    private Button? processButton;
    private Button? settingsButton;
    private Button? playersButton;

    internal HostingPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        this.session = session;
        hosting = session.Hosting;
        InitializeComponent();
    }

    internal async Task RefreshAsync(string? id = null)
    {
        id ??= Selected?.Id;
        hosts = await hosting.ReadAsync();
        syncing = true;
        Servers.ItemsSource = hosts;
        Servers.SelectedItem = hosts.FirstOrDefault(item => item.Id == id) ?? hosts.FirstOrDefault();
        syncing = false;
        ShowServer();
    }

    private HostedServer? Selected => Servers.SelectedItem as HostedServer;

    private void OnServerChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!syncing)
            ShowServer();
    }

    private void ShowServer()
    {
        Details.Children.Clear();
        processButton = settingsButton = playersButton = null;
        if (Selected is not { } server)
        {
            Details.Children.Add(new TextBlock { Text = "No servers yet", Style = StyleFor("SectionTitle") });
            Details.Children.Add(Caption("Create a server to play with friends on your network."));
            return;
        }

        Details.Children.Add(new TextBlock { Text = server.Name, Style = StyleFor("SectionTitle"), TextWrapping = TextWrapping.Wrap });
        phaseText = new TextBlock { Style = StyleFor("BodyStrongTextBlockStyle") };
        AutomationProperties.SetLiveSetting(phaseText, AutomationLiveSetting.Polite);
        detailText = Caption("");
        Details.Children.Add(phaseText);
        Details.Children.Add(detailText);

        processButton = Action("Start server", () => ToggleAsync(server), "R");
        settingsButton = Action("Edit server…", () => EditAsync(server), "E");
        Details.Children.Add(Buttons(processButton, settingsButton));
        playersButton = Action("Players…", () => PlayersAsync(server), "P");
        Details.Children.Add(Buttons(Action("Server log…", () => LogAsync(server), "L"), playersButton));

        Details.Children.Add(SettingRow("Game version", server.Engine == GameEdition.Cod2x ? "CoD2x 1.4.6.8" : "Original 1.3"));
        Details.Children.Add(SettingRow("Game type", HostingCatalog.GameTypeTitle(HostingConfig.GameType(server))));
        Details.Children.Add(SettingRow("Port", server.Port.ToString(CultureInfo.InvariantCulture)));
        Details.Children.Add(SettingRow("Map rotation", string.Join(" · ", server.Maps.Select(MapCatalog.DisplayName))));
        UpdateRunState();
    }

    internal void UpdateRunState()
    {
        if (Selected is not { } server || phaseText is null || detailText is null || processButton is null ||
            settingsButton is null || playersButton is null)
            return;
        var state = hosting.Running.GetValueOrDefault(server.Id) ?? new(HostedPhase.Stopped);
        bool active = hosting.IsActive(server.Id);
        string phase = state.Phase switch
        {
            HostedPhase.Running => "Running",
            HostedPhase.Starting => "Starting…",
            HostedPhase.Stopping => "Stopping…",
            HostedPhase.PortBusy => "Port is in use",
            HostedPhase.Failed => "Server stopped with an error",
            _ => "Stopped"
        };
        if (phaseText.Text != phase)
            phaseText.Text = phase;
        detailText.Text = state.Status is { } status
            ? $"{MapCatalog.DisplayName(status.Map)} · {status.Players.Length}/{status.MaxPlayers} players"
            : state.NextPort is { } port ? $"Choose another port, such as {port}." : state.Problem ?? "";
        processButton.Content = active ? "Stop server" : "Start server";
        processButton.IsEnabled = state.Phase != HostedPhase.Stopping;
        settingsButton.IsEnabled = !active;
        playersButton.IsEnabled = state.Status is not null;
    }

    private async Task ToggleAsync(HostedServer server)
    {
        if (hosting.IsActive(server.Id))
            await hosting.StopAsync(server.Id);
        else
            await hosting.StartAsync(server, session.Setup.DataPath);
        UpdateRunState();
    }

    private async void OnServers(object sender, RoutedEventArgs args) => await shell.OpenServersAsync(ServerScope.All);

    private async void OnNew(object sender, RoutedEventArgs args) => await shell.RunAsync(() =>
    {
        var fresh = HostedServer.New(edition: session.Settings.Working.Engine);
        if (hosts.Any(server => server.Port == fresh.Port))
            fresh = fresh with { Port = HostingConfig.OtherPort(fresh, port => hosts.All(server => server.Port != port)) };
        return EditAsync(fresh);
    });

    private async Task EditAsync(HostedServer server)
    {
        bool isNew = hosts.All(item => item.Id != server.Id);
        var name = new TextBox { Header = "Server name", Text = server.Name, MaxLength = 64 };
        var edition = new ComboBox { Header = "Game version", ItemsSource = new[] { "CoD2x 1.4.6.8", "Original 1.3" }, SelectedIndex = (int)server.Engine };
        var reach = new ComboBox { Header = "Who can join", ItemsSource = new[] { "Local network", "Public server list" },
            SelectedIndex = hosting.LoopbackOnly ? 0 : (int)server.Reach, IsEnabled = !hosting.LoopbackOnly };
        var players = new NumberBox { Header = "Player slots", Minimum = 2, Maximum = 64, Value = server.Players,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var port = new NumberBox { Header = "Port", FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["CourierPrime"], Minimum = 1025, Maximum = 65535, Value = server.Port };
        var gameTypes = HostingCatalog.GameTypes;
        var type = new ComboBox { Header = "Game type", ItemsSource = gameTypes.Select(item => item.Title).ToArray(),
            SelectedItem = HostingCatalog.GameTypeTitle(server.GameType) };
        if (type.SelectedIndex < 0)
            type.SelectedIndex = 0;
        var friendly = new ToggleSwitch { Header = "Friendly fire", IsOn = server.FriendlyFire, OnContent = "On", OffContent = "Off" };
        var join = new PasswordBox { Header = "Join password", Password = server.JoinPassword, MaxLength = 32,
            PasswordRevealMode = PasswordRevealMode.Hidden };
        var admin = new PasswordBox { Header = "Admin password", Password = server.AdminPassword, MaxLength = 32,
            PasswordRevealMode = PasswordRevealMode.Hidden };
        var maps = server.Maps.ToList();
        var rotation = new ComboBox { Header = "Map rotation" };
        var mapsAvailable = new ComboBox { ItemsSource = HostingCatalog.Maps.Select(MapCatalog.DisplayName).ToArray(), Header = "Add a map", SelectedIndex = 0 };
        var addMap = new Button { Content = "Add map" };
        var removeMap = new Button { Content = "Remove map" };
        var earlier = new Button { Content = "Move earlier" };
        var problem = Caption("");
        var form = new StackPanel { Spacing = 12 };
        foreach (var field in new UIElement[] { name, edition, reach, players, port, type, friendly, join, admin, rotation, mapsAvailable,
            Buttons(addMap, removeMap, earlier), problem })
            form.Children.Add(field);
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 350, MinWidth = 368 };
        var dialog = new ContentDialog
        {
            Title = isNew ? "New server" : "Edit server", Content = scroll, XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme,
            PrimaryButtonText = isNew ? "Create server" : "Save changes", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        name.TextChanged += (_, _) => Validate();
        players.ValueChanged += (_, _) => Validate();
        port.ValueChanged += (_, _) => Validate();
        join.PasswordChanged += (_, _) => Validate();
        admin.PasswordChanged += (_, _) => Validate();
        rotation.SelectionChanged += (_, _) => SyncMapButtons();
        addMap.Click += (_, _) =>
        {
            if (mapsAvailable.SelectedIndex >= 0)
            {
                maps.Add(HostingCatalog.Maps[mapsAvailable.SelectedIndex]);
                SyncMaps(maps.Count - 1);
            }
        };
        removeMap.Click += (_, _) =>
        {
            int selected = rotation.SelectedIndex;
            if (selected >= 0 && maps.Count > 1)
            {
                maps.RemoveAt(selected);
                SyncMaps(Math.Min(selected, maps.Count - 1));
            }
        };
        earlier.Click += (_, _) =>
        {
            int index = rotation.SelectedIndex;
            if (index > 0)
            {
                (maps[index - 1], maps[index]) = (maps[index], maps[index - 1]);
                SyncMaps(index - 1);
            }
        };
        SyncMaps(0);
        Validate();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var saved = server with
            {
                Name = HostingConfig.Safe(name.Text, 64), Engine = (GameEdition)edition.SelectedIndex, Reach = (HostedReach)reach.SelectedIndex,
                Players = (int)players.Value, Port = (int)port.Value, GameType = gameTypes[type.SelectedIndex].Id,
                FriendlyFire = friendly.IsOn, JoinPassword = HostingConfig.Password(join.Password),
                AdminPassword = HostingConfig.Password(admin.Password), Maps = [.. maps]
            };
            await hosting.SaveAsync(saved);
            await RefreshAsync(saved.Id);
        }
        join.Password = admin.Password = "";

        void SyncMaps(int index)
        {
            rotation.ItemsSource = maps.Select(MapCatalog.DisplayName).ToArray();
            rotation.SelectedIndex = index;
            SyncMapButtons();
        }

        void SyncMapButtons()
        {
            removeMap.IsEnabled = maps.Count > 1 && rotation.SelectedIndex >= 0;
            earlier.IsEnabled = rotation.SelectedIndex > 0;
        }

        void Validate()
        {
            problem.Text = HostingConfig.Safe(name.Text, 64).Length == 0 ? "Enter a server name."
                : !double.IsInteger(players.Value) || !double.IsInteger(port.Value) ? "Enter a whole number for players and port."
                : HostingConfig.Password(join.Password) != join.Password || HostingConfig.Password(admin.Password) != admin.Password
                    ? "Passwords can’t contain spaces or quotes." : "";
            dialog.IsPrimaryButtonEnabled = problem.Text.Length == 0;
        }
    }

    private async Task LogAsync(HostedServer server)
    {
        string log = await hosting.ReadLogAsync(server.Id) ?? "No server log yet.";
        var text = new TextBox { Text = log, IsReadOnly = true, AcceptsReturn = true, MinWidth = 360, MaxHeight = 340,
            Style = StyleFor("MonoField"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(text, "Server log");
        await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "Server log", Content = text, CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.None }.ShowAsync();
    }

    private async Task PlayersAsync(HostedServer server)
    {
        var players = hosting.Running.GetValueOrDefault(server.Id)?.Status?.Players ?? [];
        var list = new StackPanel { Spacing = 8 };
        if (players.IsEmpty)
            list.Children.Add(Body("No players are connected."));
        foreach (var player in players)
            list.Children.Add(SettingRow(QuakeColors.Plain(player.Name), $"{player.Score} points · {player.Ping} ms"));
        var content = new ScrollViewer { MaxHeight = 340, Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "Players", Content = content, CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.None }.ShowAsync();
    }

    private Button Action(string title, Func<Task> action, string accessKey)
    {
        var button = new Button { Content = title, AccessKey = accessKey };
        button.Click += async (_, _) => await shell.RunAsync(action);
        return button;
    }
}
