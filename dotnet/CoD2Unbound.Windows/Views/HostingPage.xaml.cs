using System.Collections.Immutable;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HostingPage : Page
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly WindowsHosting hosting;
    private ImmutableArray<HostedServer> hosts = [];
    private bool syncing;
    private HostedPhase? shownPhase;
    private TextBlock? phaseText;
    private TextBlock? actionFeedback;
    private Dashboard? dashboard;
    private HostForm? form;

    internal HostingPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        this.session = session;
        hosting = session.Hosting;
        InitializeComponent();
        Unloaded += (_, _) => EndForm();
    }

    private HostedServer? Selected => Servers.SelectedItem as HostedServer;
    private HostedRunState State(HostedServer server) => hosting.Running.GetValueOrDefault(server.Id) ?? new(HostedPhase.Stopped);

    internal async Task RefreshAsync(string? id = null)
    {
        id ??= Selected?.Id;
        hosts = await hosting.ReadAsync();
        syncing = true;
        Servers.ItemsSource = hosts;
        Servers.SelectedItem = hosts.FirstOrDefault(item => item.Id == id) ?? hosts.FirstOrDefault();
        syncing = false;
        Sidebar.Visibility = IsVisible(!hosts.IsEmpty);
        await ShowServerAsync();
    }

    private void OnSelectionPillLoaded(object sender, RoutedEventArgs args) => TrackSelectionPill((Border)sender);

    private void OnServerRowChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is HostedServer server && !args.InRecycleQueue)
        {
            AutomationProperties.SetName(args.ItemContainer, server.Name);
            args.RegisterUpdateCallback((_, update) => UpdateRow(server, update.ItemContainer as ListViewItem));
        }
    }

    private void UpdateRow(HostedServer server, ListViewItem? item)
    {
        if (item?.ContentTemplateRoot is FrameworkElement root && root.FindName("HostRowStatus") is TextBlock detail)
            detail.Text = $"{Edition(server)} · {PhaseTitle(State(server).Phase)}";
    }

    private async void OnServerChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!syncing)
            await shell.ShowProblemAsync(ShowServerAsync);
    }

    private async Task ShowServerAsync()
    {
        EndForm();
        Details.Children.Clear();
        dashboard = null;
        shownPhase = null;
        phaseText = null;
        actionFeedback = null;
        DetailScroll.ChangeView(null, 0, null, true);
        if (Selected is not { } server)
        {
            var empty = EmptyState("Live", "Host Your Own Server",
                "Run a server on this PC with Original 1.3 or CoD2x, or write the files for a server on another machine.");
            empty.MaxWidth = 520;
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(12, 44, 12, 0);
            var create = Action("New Server", NewAsync);
            create.Style = StyleFor("LauncherAccentButton");
            empty.Children.Add(Buttons(create, Action("Write a server.cfg…", () =>
                ExportAsync(HostedServer.New(edition: session.Settings.Working.Engine)), "X")));
            Details.Children.Add(empty);
            return;
        }
        var state = State(server);
        shownPhase = state.Phase;
        if (!hosting.IsActive(server.Id))
        {
            ShowForm(server, isNew: false);
            return;
        }
        var configured = server;
        server = hosting.ActiveSettings(server);

        phaseText = Caption("");
        AutomationProperties.SetLiveSetting(phaseText, AutomationLiveSetting.Polite);
        Details.Children.Add(Heading(server.Name));
        Details.Children.Add(phaseText);
        actionFeedback = Caption("");
        actionFeedback.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(actionFeedback, AutomationLiveSetting.Polite);
        var nextMap = Action("Next Map", async () =>
        {
            if (!await hosting.NextMapAsync(server.Id))
                Notify("The server is no longer ready for a map change.");
        }, "M");
        nextMap.IsEnabled = HostingConfig.Password(server.AdminPassword).Length > 0;
        var join = Action("Join", () => JoinAsync(server), "J");
        join.Style = StyleFor("LauncherAccentButton");
        Details.Children.Add(Buttons(Action("Edit…", () =>
        {
            Details.Children.Clear();
            dashboard = null;
            ShowForm(configured, isNew: false, editingRunning: true);
            return Task.CompletedTask;
        }, "E"), nextMap, join, Action("Stop Server", () => ToggleAsync(server), "R")));
        Details.Children.Add(actionFeedback);

        dashboard = new Dashboard(this, server, join);
        Details.Children.Add(dashboard.Stats);
        Details.Children.Add(dashboard.Content);
        await UpdateRunStateAsync();
    }

    internal async Task UpdateRunStateAsync()
    {
        foreach (var host in hosts)
            UpdateRow(host, Servers.ContainerFromItem(host) as ListViewItem);
        if (Selected is not { } server || form is { IsNew: true } or { EditingRunning: true })
            return;
        var state = State(server);
        if (shownPhase != state.Phase && (dashboard is null || !hosting.IsActive(server.Id)))
        {
            await ShowServerAsync();
            return;
        }
        if (dashboard is not { } live || phaseText is null)
            return;
        shownPhase = state.Phase;
        string phase = PhaseTitle(state.Phase);
        if (state is { Phase: HostedPhase.Running, Since: { } since })
            phase += $" · {Math.Max(0, (int)(DateTimeOffset.UtcNow - since).TotalMinutes)} min";
        if (phaseText.Text != phase)
            phaseText.Text = phase;
        await live.UpdateAsync(state);
    }

    private async Task JoinAsync(HostedServer server)
    {
        if (!HostedDashboard.From(server, State(server)).CanJoin)
            return;
        var result = await session.JoinAsync(new($"127.0.0.1:{server.Port}", server.JoinPassword), server.Engine);
        if (result != GameStartState.Started)
            Notify(result is GameStartState.NeedsSetup or GameStartState.MissingData
                ? "Choose a complete game data folder before launching." : "Call of Duty 2 is already running.");
    }

    private async Task ToggleAsync(HostedServer server, bool refresh = true)
    {
        if (hosting.IsActive(server.Id))
            await hosting.StopAsync(server.Id);
        else
        {
            try
            {
                await hosting.StartAsync(server, session.Setup.DataPath);
                if (hosting.IsActive(server.Id))
                    session.Record(new(UsageKind.HostingStarted,
                        server.Reach == HostedReach.Listed && !hosting.LoopbackOnly ? 1 : 0, (int)server.Engine));
                else
                    session.ShowError(UsageProblem.Hosting);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or LaunchPlanException)
            {
                session.ShowError(UsageProblem.Hosting);
                throw;
            }
        }
        if (refresh)
            await ShowServerAsync();
    }

    internal Task NewAsync()
    {
        var fresh = HostedServer.New(edition: session.Settings.Working.Engine);
        if (hosts.Any(server => server.Port == fresh.Port))
            fresh = fresh with { Port = HostingConfig.OtherPort(fresh, port => hosts.All(server => server.Port != port)) };
        EndForm();
        Details.Children.Clear();
        dashboard = null;
        ShowForm(fresh, isNew: true);
        return Task.CompletedTask;
    }

    private async Task ExportAsync(HostedServer server)
    {
        var passwords = new ToggleSwitch { Header = "Include passwords", OffContent = "Off", OnContent = "On" };
        var match = new ToggleSwitch { Header = "Include zPAM match settings", IsOn = true, OffContent = "Off", OnContent = "On",
            Visibility = IsVisible(server.Rules == HostedRules.Zpam) };
        var content = new StackPanel { Spacing = 8, Children =
        {
            Body("Write a folder you can copy to a Linux or Windows server."),
            SettingRow("server.cfg", "Server name, rules, maps and limits."),
            SettingRow("Start commands", "One for Linux and one for Windows."),
            match, passwords,
            Caption("Leave passwords off to write CHANGE_ME in their place. Game files are never included.")
        } };
        if (await Dialog($"Export “{server.Name}” for Another Machine", content, "Choose Folder…").ShowAsync() != ContentDialogResult.Primary ||
            await shell.PickFolderAsync("Choose where to write the server files") is not { } parent)
            return;
        await OpenAsync(await hosting.ExportAsync(server, parent, passwords.IsOn, match.IsOn));
    }

    private ContentDialog Dialog(string title, UIElement content, string primary = "", string close = "Cancel") => new()
    {
        Title = title, Content = content, XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme,
        PrimaryButtonText = primary, CloseButtonText = close,
        DefaultButton = primary.Length == 0 ? ContentDialogButton.Close : ContentDialogButton.Primary
    };

    private void Notify(string message)
    {
        if (actionFeedback is not null)
        {
            actionFeedback.Text = message;
            actionFeedback.Visibility = Visibility.Visible;
        }
    }

    private void Copy(string value)
    {
        var data = new DataPackage();
        data.SetText(value);
        Clipboard.SetContent(data);
        Notify("Copied.");
    }

    private Button Action(string title, Func<Task> action, string accessKey = "")
    {
        var button = new Button { Content = title, AccessKey = accessKey };
        button.Click += async (_, _) => await shell.RunAsync(action);
        return button;
    }

    private static TextBlock Heading(string title) => new()
    {
        Text = title, Style = StyleFor("SectionTitle"), TextWrapping = TextWrapping.Wrap
    };

    private static string PhaseTitle(HostedPhase phase) => phase switch
    {
        HostedPhase.Starting => "Starting…",
        HostedPhase.Running => "Running",
        HostedPhase.Stopping => "Stopping…",
        HostedPhase.PortBusy => "Port in use",
        HostedPhase.Failed => "Server stopped with an error",
        _ => "Stopped"
    };

    private static string Edition(HostedServer server) => server.Engine == GameEdition.Cod2x ? "CoD2x" : "Original 1.3";
}
