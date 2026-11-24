using System.Collections.ObjectModel;
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
    private readonly ObservableCollection<HostedServer> hosts = [];
    private bool syncing;
    private HostedPhase? shownPhase;
    private TextBlock? phaseText;
    private TextBlock? actionFeedback;
    private Dashboard? dashboard;
    private HostForm? form;

    internal HostingPage(MainWindow shell, WindowsLauncherSession session, Button exportButton, InfoBar exportStatus)
    {
        this.shell = shell;
        this.session = session;
        this.exportButton = exportButton;
        this.exportStatus = exportStatus;
        hosting = session.Hosting;
        InitializeComponent();
        Servers.ItemsSource = hosts;
        exportButton.Click += async (_, _) => await shell.RunAsync(ExportSelectedAsync);
        exportStatus.Closed += (_, _) =>
        {
            exportFeedback = null;
            exportStatus.ActionButton = null;
            exportStatus.Visibility = Visibility.Collapsed;
        };
        Unloaded += (_, _) =>
        {
            if (form is not null)
                shownPhase = null;
            EndForm();
        };
        Loaded += async (_, _) => await shell.ShowProblemAsync(UpdateRunStateAsync);
    }

    private HostedServer? Selected => Servers.SelectedItem as HostedServer;
    private HostedRunState State(HostedServer server) => hosting.Running.GetValueOrDefault(server.Id) ?? new(HostedPhase.Stopped);

    internal async Task RefreshAsync(string? id = null)
    {
        id ??= Selected?.Id;
        var next = await hosting.ReadAsync();
        syncing = true;
        hosts.Reconcile(next, item => item.Id);
        var target = hosts.FirstOrDefault(item => item.Id == id) ?? hosts.FirstOrDefault();
        if (!ReferenceEquals(Selected, target))
            Servers.SelectedItem = target;
        syncing = false;
        Sidebar.Visibility = IsVisible(hosts.Count != 0);
        await ShowServerAsync();
    }

    private void OnSelectionPillLoaded(object sender, RoutedEventArgs args) => TrackSelectionPill((Border)sender);

    private void OnServerRowChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem item)
            return;
        if (args.InRecycleQueue)
        {
            item.ContextFlyout = null;
            return;
        }
        if (args.Item is HostedServer server)
        {
            AutomationProperties.SetName(item, server.Name);
            AttachRowMenu(item);
            args.RegisterUpdateCallback((_, update) => UpdateRow(server, update.ItemContainer as ListViewItem));
        }
    }

    private void UpdateRow(HostedServer server, ListViewItem? item)
    {
        if (item?.ContentTemplateRoot is FrameworkElement root && root.FindName("HostRowStatus") is TextBlock detail)
        {
            var state = State(server);
            string status = state switch
            {
                { Phase: HostedPhase.Running, Status: { } live } =>
                    $"Running · {live.Players.Length} of {live.MaxPlayers} · {MapCatalog.DisplayName(HostingCatalog.StockMap(live.Map))}",
                { Phase: HostedPhase.Stopped } => $"Stopped · {Edition(server)}",
                _ => $"{Edition(server)} · {PhaseTitle(state.Phase)}"
            };
            if (detail.Text != status)
                detail.Text = status;
            if (AutomationProperties.GetHelpText(item) != status)
                AutomationProperties.SetHelpText(item, status);
        }
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
        exportButton.Visibility = IsVisible(Selected is not null);
        if (Selected is not { } server)
        {
            var empty = EmptyState("Live", "Host your own server",
                "Run a server on this PC with Original 1.3 or CoD2x, or write the files for a server on another machine.");
            empty.MaxWidth = 520;
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(12, 44, 12, 0);
            var create = Action("New server", NewAsync);
            create.Style = StyleFor("LauncherAccentButton");
            empty.Children.Add(Buttons(create, Action("Write a server.cfg…", ExportSelectedAsync, "X")));
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
        actionFeedback = Caption("");
        actionFeedback.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(actionFeedback, AutomationLiveSetting.Polite);
        var nextMap = Action("Next map", async () =>
        {
            if (!await hosting.NextMapAsync(server.Id))
                Notify("The server is no longer ready for a map change.");
        }, "M");
        nextMap.IsEnabled = HostingConfig.Password(server.AdminPassword).Length > 0;
        var join = Action("Join", () => JoinAsync(server), "J");
        join.Style = StyleFor("LauncherAccentButton");
        var actions = Buttons(Action("Edit…", () =>
        {
            Details.Children.Clear();
            dashboard = null;
            ShowForm(configured, isNew: false, editingRunning: true);
            return Task.CompletedTask;
        }, "E"), nextMap, join, Action("Stop server", () => ToggleAsync(server), "R"));
        Details.Children.Add(RunningHeader(server.Name, phaseText, actions));
        Details.Children.Add(actionFeedback);

        dashboard = new Dashboard(this, server, join);
        Details.Children.Add(dashboard.Stats);
        Details.Children.Add(dashboard.Content);
        await UpdateRunStateAsync();
    }

    private static Grid RunningHeader(string name, TextBlock phase, StackPanel actions)
    {
        var title = Heading(name);
        title.MaxWidth = 260;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(title, name);
        phase.VerticalAlignment = VerticalAlignment.Center;
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { title, phase } };
        label.VerticalAlignment = VerticalAlignment.Center;

        var header = Columns(12, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowSpacing = 8;
        Put(header, 0, label);
        Put(header, 1, actions);
        header.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 740;
            Grid.SetRow(actions, compact ? 1 : 0);
            Grid.SetColumn(actions, compact ? 0 : 1);
        };
        return header;
    }

    internal async Task UpdateRunStateAsync()
    {
        foreach (var host in hosts)
            UpdateRow(host, Servers.ContainerFromItem(host) as ListViewItem);
        if (Selected is not { } server || form is { IsNew: true })
            return;
        var state = State(server);
        if (form is { EditingRunning: true })
        {
            UpdatePhaseText(state);
            return;
        }
        if (shownPhase != state.Phase && (dashboard is null || !hosting.IsActive(server.Id)))
        {
            await ShowServerAsync();
            return;
        }
        if (dashboard is not { } live)
            return;
        shownPhase = state.Phase;
        UpdatePhaseText(state);
        await live.UpdateAsync(state);
    }

    private void UpdatePhaseText(HostedRunState state)
    {
        if (phaseText is null)
            return;
        string phase = PhaseTitle(state.Phase);
        if (state is { Phase: HostedPhase.Running, Since: { } since })
            phase = $"Running for {Math.Max(0, (int)(DateTimeOffset.UtcNow - since).TotalMinutes)} min";
        if (phaseText.Text != phase)
            phaseText.Text = phase;
    }

    private async Task JoinAsync(HostedServer server)
    {
        if (!HostedDashboard.From(server, hosting.DashboardState(State(server))).CanJoin)
            return;
        string address = FormattableString.Invariant($"127.0.0.1:{server.Port}");
        if (await shell.JoinServerAsync(new(address, server.JoinPassword), UsageJoin.List, requested: server.Engine)
                is { } message)
            Notify(message);
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
        exportButton.Visibility = Visibility.Collapsed;
        ShowForm(fresh, isNew: true);
        return Task.CompletedTask;
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
