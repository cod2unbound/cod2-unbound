using System.Text.Json;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.Views;
using CoD2Unbound.Windows.ViewModels;
using static CoD2Unbound.Windows.Views.PagePresentation;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow : Window
{
    private readonly WindowsLauncherSession session = new();
    private readonly ThemeSettings windowTheme;
    private readonly bool useCustomTitleBar;
    private bool busy;
    private bool loaded;
    private double sizedAtScale;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer gameWatcher;
    private GameSessionState? lastGame;
    private string? lastMigrationProblem;
    private bool minimizedForGame;
    private bool flushedForClose;
    private bool closing;
    private bool closeRequested;
    private DateTimeOffset hostPolled;
    private DateTimeOffset? attemptedUpdate;
    private CancellationTokenSource? updateRequest;
    private readonly DateTimeOffset firstUpdate = DateTimeOffset.UtcNow.AddSeconds(8);

    public MainWindow()
    {
        StartupTrace.Mark("window constructor");
        InitializeComponent();
        StartupTrace.Mark("window XAML");
        InitializeJumpTo();
        bool windows11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        if (windows11 && MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            SolidFrame.Visibility = Visibility.Collapsed;
        }
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        AppWindow.SetIcon(Path.Combine(assets, "mark.ico"));
        useCustomTitleBar = AppWindowTitleBar.IsCustomizationSupported();
        if (useCustomTitleBar)
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(WindowBar);
            WindowBar.IconSource = new BitmapIconSource
            {
                UriSource = new Uri(Path.Combine(assets, "mark-16.png")),
                ShowAsMonochrome = false
            };
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        else
        {
            WindowBar.Content = null;
            WindowBar.Visibility = Visibility.Collapsed;
            ClientJumpBar.Content = JumpHost;
            ClientJumpBar.Visibility = Visibility.Visible;
        }
        windowTheme = ThemeSettings.CreateForWindowId(AppWindow.Id);
        windowTheme.Changed += OnSystemThemeChanged;
        Root.ActualThemeChanged += OnAppThemeChanged;
        gameWatcher = DispatcherQueue.CreateTimer();
        gameWatcher.Interval = TimeSpan.FromSeconds(1);
        gameWatcher.Tick += OnWatchGame;
        session.ClientStarted += OnClientStarted;
        session.Updates.Changed += OnUpdateChanged;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendered -= OnFirstRendered;
#if UPDATE_FIXTURES
            CompositionTarget.Rendered -= OnFirstContentRendered;
#endif
            gameWatcher.Stop();
            session.Reports.CancelCrash();
            session.Updates.Changed -= OnUpdateChanged;
            if (updateRequest is { } request)
                request.Cancel();
            else if (session.Updates.State.Phase == UpdatePhase.Ready)
                _ = session.Updates.DiscardVerifiedAsync();
            windowTheme.Changed -= OnSystemThemeChanged;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (loaded)
            return;
        loaded = true;
        StartupTrace.Mark("root loaded");
        ResizeForDisplay(startup: true);
        session.Reports.Facts.Window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        sessionEnd = new(session.Reports.Facts.Window, EndWindowsSession);
        UpdateCaptionColors();
        CompositionTarget.Rendered += OnFirstRendered;
    }

    private void OnFirstRendered(object? sender, RenderedEventArgs args)
    {
        CompositionTarget.Rendered -= OnFirstRendered;
        StartupTrace.Mark("first XAML frame");
        DispatcherQueue.TryEnqueue(InitializeFirstPage);
    }

    private async void InitializeFirstPage()
    {
        if (!Root.IsLoaded)
            return;
        await RunAsync(async () =>
        {
            await session.InitializeAsync();
            StartupTrace.Mark("session initialized");
            ApplyAppearance(session.Settings.Working.Appearance);
            if (!session.SetupUpdating)
                await ShowProblemAsync(() => Task.Run(() => session.Game.ReconcileAsync()));
            await NavigateAsync(session.SetupComplete ? LauncherPlace.Home : LauncherPlace.Setup);
            initialized = true;
            ShowMigrationProblem();
            StartupTrace.Mark("first page ready");
#if UPDATE_FIXTURES
            CompositionTarget.Rendered += OnFirstContentRendered;
#endif
            gameWatcher.Start();
            _ = RemoveInstalledNotificationsAsync();
        });
    }

#if UPDATE_FIXTURES
    private void OnFirstContentRendered(object? sender, RenderedEventArgs args)
    {
        CompositionTarget.Rendered -= OnFirstContentRendered;
        StartupTrace.Mark("first content frame");
    }
#endif

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ResizeForDisplay();
        UpdatePaneMode(args.NewSize.Width);
    }

    private void OnJumpHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!useCustomTitleBar)
            return;
        // The native title and caption buttons reserve different widths on each side.
        double hostLeft = JumpHost.TransformToVisual(WindowBar).TransformPoint(default).X;
        double hostWidth = args.NewSize.Width;
        double availableOffset = Math.Max(0, (hostWidth - JumpBox.Width) / 2);
        double centerOffset = WindowBar.ActualWidth / 2 - hostLeft - hostWidth / 2;
        double offset = Math.Clamp(centerOffset, -availableOffset, availableOffset);
        JumpBox.Margin = new Thickness(Math.Max(0, 2 * offset), 0, Math.Max(0, -2 * offset), 0);
    }

    private void OnAppThemeChanged(FrameworkElement sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateCaptionColors);

    private void OnSystemThemeChanged(ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateCaptionColors();
            if (ContentFrame.Content is HomePage home)
                home.UpdateTheme();
            else if (ContentFrame.Content is ServersPage servers)
                servers.UpdateContrast();
            else if (ContentFrame.Content is ConfigsPage configs)
                configs.UpdateContrast();
            else if (ContentFrame.Content is SettingsPage settings)
                settings.UpdateContrast();
        });

    internal ConfigSetupOrder SetupOrder { get; set; }
    internal bool IsHighContrast => windowTheme.HighContrast;
    internal ElementTheme DialogTheme => Root.RequestedTheme;
    internal XamlRoot DialogRoot => Root.XamlRoot;

    private void UpdateCaptionColors()
    {
        // Windows 10 supports caption button colors when content extends into the title bar.
        if (!useCustomTitleBar)
            return;
        var foreground = windowTheme.HighContrast ? null : (WindowBar.Foreground as SolidColorBrush)?.Color;
        var caption = AppWindow.TitleBar;
        caption.ButtonForegroundColor = foreground;
        caption.ButtonHoverForegroundColor = foreground;
        caption.ButtonPressedForegroundColor = foreground;
        caption.ButtonInactiveForegroundColor = foreground;
    }

    private void UpdatePaneMode(double width)
    {
        var mode = width >= 1200 ? NavigationViewPaneDisplayMode.Left : NavigationViewPaneDisplayMode.LeftCompact;
        if (Nav.PaneDisplayMode == mode)
            return;
        Nav.PaneDisplayMode = mode;
        Nav.IsPaneOpen = mode == NavigationViewPaneDisplayMode.Left;
    }

    private void OnPaneChanged(NavigationView sender, object args)
    {
        PlayerPlate.Visibility = IsVisible(Nav.IsPaneOpen && !session.SetupActive);
        RefreshFavorites();
    }

    private void ResizeForDisplay(bool startup = false)
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        if (AppWindow.Presenter is not OverlappedPresenter presenter || (!startup && sizedAtScale == scale))
            return;
        sizedAtScale = scale;
        presenter.PreferredMinimumWidth = Pixels(1024);
        presenter.PreferredMinimumHeight = Pixels(640);
        if (startup)
        {
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Max(Pixels(1024), Math.Min(Pixels(1440), work.Width));
            int height = Math.Max(Pixels(640), Math.Min(Pixels(900), work.Height));
            AppWindow.MoveAndResize(new RectInt32(work.X + Math.Max(0, (work.Width - width) / 2),
                work.Y + Math.Max(0, (work.Height - height) / 2), width, height));
        }

        int Pixels(int effectivePixels) => (int)Math.Ceiling(effectivePixels * scale);
    }

    private void OnFullScreen(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);
        sizedAtScale = 0;
        ResizeForDisplay();
        args.Handled = true;
    }

    private async void OnNavigation(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
            await RunAsync(() => NavigateAsync(LauncherPlace.Settings));
        else if (ReferenceEquals(args.InvokedItemContainer, HomeItem))
            await RunAsync(() => NavigateAsync(LauncherPlace.Home));
        else if (ReferenceEquals(args.InvokedItemContainer, ServersItem))
            await OpenServersAsync(ServerScope.All);
        else if (ReferenceEquals(args.InvokedItemContainer, LibraryItem))
            await RunAsync(() => NavigateAsync(LauncherPlace.Library));
        else if (ReferenceEquals(args.InvokedItemContainer, ConfigsItem))
            await RunAsync(() => NavigateAsync(LauncherPlace.Configs));
        else if (args.InvokedItemContainer?.Tag is string address)
            await OpenServersAsync(ServerScope.Favorites, address);
    }

    internal Task OpenServersAsync(ServerScope scope, string? address = null) =>
        RunAsync(() => NavigateAsync(LauncherPlace.Servers, scope, address));

    internal Task OpenCheckupAsync() => RunAsync(async () =>
    {
        await NavigateAsync(LauncherPlace.Configs);
        if (ContentFrame.Content is ConfigsPage configs)
            configs.OpenCheckup();
    });

    internal async Task OpenResolutionAsync()
    {
        await NavigateAsync(LauncherPlace.Configs);
        if (ContentFrame.Content is ConfigsPage configs)
            configs.OpenResolution();
    }

    internal async Task NavigateAsync(LauncherPlace place, ServerScope scope = ServerScope.All, string? address = null,
        bool refreshServers = true, LauncherPlace cancelSetupTo = LauncherPlace.GameDataSettings)
    {
        CloseJumpTo(restoreFocus: false);
        Nav.IsPaneVisible = JumpBox.IsEnabled = !session.SetupActive && place != LauncherPlace.Setup;
        JumpHost.Visibility = Nav.IsPaneVisible ? Visibility.Visible : Visibility.Collapsed;
        if (Nav.IsPaneVisible)
        {
            if (Nav.SettingsItem is NavigationViewItem settings)
            {
                settings.AccessKey = "T";
                settings.HighContrastAdjustment = ElementHighContrastAdjustment.None;
            }
            UpdatePaneMode(Root.ActualWidth);
            await RefreshPlayerAsync();
            OnPaneChanged(Nav, EventArgs.Empty);
        }
        if (place is LauncherPlace.Home or LauncherPlace.Servers or LauncherPlace.Library or LauncherPlace.Configs)
            session.Opened(place switch
            {
                LauncherPlace.Home => UsagePage.Home,
                LauncherPlace.Servers => UsagePage.Servers,
                LauncherPlace.Library => UsagePage.Library,
                _ => UsagePage.Configs
            });
        if (place == LauncherPlace.Setup)
        {
            var page = new SetupPage(this, session, cancelSetupTo);
            ContentFrame.Content = page;
            await page.RefreshAsync();
        }
        else if (place == LauncherPlace.Home)
        {
            Nav.SelectedItem = HomeItem;
            var page = new HomePage(this, session);
            ContentFrame.Content = page;
            await page.RefreshAsync();
        }
        else if (place == LauncherPlace.Servers)
        {
            Nav.SelectedItem = ServersItem;
            var page = new ServersPage(this, session, scope, address);
            if (refreshServers && session.Browser.ShouldRefreshOnOpen(session.Settings.Working.RefreshServersOnOpen, session.Library))
                page.Loaded += OnServersLoaded;
            ContentFrame.Content = page;
        }
        else if (place == LauncherPlace.Library)
        {
            Nav.SelectedItem = LibraryItem;
            ContentFrame.Content = new LibraryPage(this, session);
        }
        else if (place == LauncherPlace.Configs)
        {
            nint owner = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var model = await Task.Run(async () =>
            {
                var configs = new ConfigsViewModel(session, owner);
                await configs.LoadSharingAsync();
                return configs;
            });
            Nav.SelectedItem = ConfigsItem;
            ContentFrame.Content = new ConfigsPage(this, model);
        }
        else
        {
            Nav.SelectedItem = Nav.SettingsItem;
            ContentFrame.Content = new SettingsPage(this, session, openGameData: place == LauncherPlace.GameDataSettings,
                openUpdates: place == LauncherPlace.UpdateSettings);
        }
        object? content = ContentFrame.Content;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(ContentFrame.Content, content) && content is Page page)
            {
                page.IsTabStop = true;
                page.UseSystemFocusVisuals = false;
                page.Focus(FocusState.Programmatic);
            }
        });
    }

    private async void OnServersLoaded(object sender, RoutedEventArgs args)
    {
        var page = (ServersPage)sender;
        page.Loaded -= OnServersLoaded;
        // Yield until the navigation action releases the shell.
        await Task.Yield();
        if (ReferenceEquals(ContentFrame.Content, page))
            await page.RefreshAsync();
    }

    private void OnClientStarted() => MinimizeForGame();

    internal async Task RefreshPlayerAsync(bool loadoutChanged = false)
    {
        var player = await session.ReadPlayerAsync();
        PlayerName.Inlines.Clear();
        AddGameName(PlayerName, player.Name, Root.ActualTheme, IsHighContrast);
        PaneLoadout.Text = player.Loadout ?? player.Key;
        if (!loadoutChanged)
            return;
        if (ContentFrame.Content is HomePage home)
            await home.RefreshAsync();
        else if (ContentFrame.Content is ConfigsPage configs)
            configs.RefreshActiveLoadout();
        else if (ContentFrame.Content is ServersPage servers)
            servers.RefreshActiveLoadout();
    }

    internal void RefreshFavorites()
    {
        while (Nav.MenuItems.Count > 4)
            Nav.MenuItems.RemoveAt(Nav.MenuItems.Count - 1);
        if (!Nav.IsPaneOpen || session.Library.Favorites.Count == 0)
            return;
        Nav.MenuItems.Add(new NavigationViewItemHeader { Content = "Favorites" });
        foreach (var favorite in session.Library.Favorites.Select(session.ServerForAddress)
            .OrderBy(row => QuakeColors.Plain(row.Name), StringComparer.CurrentCultureIgnoreCase).Take(12))
        {
            var icon = Fluent("StarFilled", 16, filled: true);
            if (!IsHighContrast)
                icon.Foreground = ThemeBrush("FavoriteBrush", Root.ActualTheme, false);
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = QuakeColors.Plain(favorite.Name), Tag = favorite.Address,
                Icon = icon, SelectsOnInvoked = false, HighContrastAdjustment = ElementHighContrastAdjustment.None
            });
        }
    }

    private async void OnWatchGame(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (busy || closing)
            return;
        if (session.SetupUpdating)
        {
            session.RefreshUpdateOutcome();
            await session.RecordInstalledUpdateAsync();
            RefreshUpdateViews(session.Updates.State);
            return;
        }
        if (!session.SetupComplete)
            return;
        if (session.Updates.State.Phase == UpdatePhase.Ready)
            session.Updates.RefreshInstallBlock(session.UpdateInstaller.Observe());
        await RunAsync(async () =>
        {
            await session.Game.ReconcileAsync();
            ShowMigrationProblem();
            var state = session.Game.State;
            bool sameRunningState = lastGame?.Running == state.Running;
            var now = DateTimeOffset.UtcNow;
            await RefreshGameViewsAsync(state, now);
            if (session.Hosting.HasActiveServers && now - hostPolled >= TimeSpan.FromSeconds(3))
            {
                hostPolled = now;
                await session.Hosting.PollAsync();
                if (ContentFrame.Content is ServersPage hosting)
                    await hosting.UpdateHostingStateAsync();
            }
            if (sameRunningState && session.CanAutoCheckUpdates &&
                session.Settings.Working.CheckUpdatesAutomatically && updateRequest is null &&
                session.Updates.State.Phase is not (UpdatePhase.Installed or UpdatePhase.Ready or UpdatePhase.Installing) &&
                now >= firstUpdate &&
                LauncherUpdates.AutoCheckDue(now, attemptedUpdate, session.Library.DispatchesCheckedAt, state.Running))
            {
                attemptedUpdate = now;
                using var request = new CancellationTokenSource();
                updateRequest = request;
                try { await CheckUpdatesAndRefreshNewsAsync(automatic: true, request.Token); }
                finally { updateRequest = null; }
            }
        }, clearProblem: false);
    }

    private void ShowMigrationProblem()
    {
        string? problem = session.MigrationProblem;
        if (problem == lastMigrationProblem)
            return;
        if (problem is not null)
            ShowShellNotice(problem, InfoBarSeverity.Error);
        else if (Problem.Message == lastMigrationProblem)
            Problem.IsOpen = false;
        lastMigrationProblem = problem;
    }

    private void OnUpdateChanged(LauncherUpdateState state) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            session.ObserveUpdate(state);
            RefreshUpdateViews(state);
        });

    private void RefreshUpdateViews(LauncherUpdateState state)
    {
        if (ContentFrame.Content is SettingsPage settings)
            settings.RefreshUpdates(state);
        else if (ContentFrame.Content is HomePage home)
            home.RefreshUpdate(state);
    }

    private async Task CheckUpdatesAndRefreshNewsAsync(bool automatic, CancellationToken cancellationToken)
    {
        string? version = await session.CheckUpdatesAsync(automatic, cancellationToken);
        if (ContentFrame.Content is HomePage home)
            home.RenderNews();
        if (!cancellationToken.IsCancellationRequested)
            await NotifyUpdateAsync(version);
    }

    internal async Task RunUpdateAsync(bool install)
    {
        if (updateRequest is not null)
        {
            if (session.Updates.State.CanCancel)
                updateRequest.Cancel();
            return;
        }
        if (!session.CanCheckUpdates || closing)
            return;
        using var request = new CancellationTokenSource();
        updateRequest = request;
        try
        {
            if (install && session.Updates.State.Offer is not null)
            {
                var result = await session.Updates.InstallAsync(session.UpdateInstaller, request.Token);
                if (result.Phase == UpdatePhase.Installing)
                {
                    await session.Reports.Usage.FlushAsync();
                    flushedForClose = true;
                    Close();
                }
            }
            else
                await CheckUpdatesAndRefreshNewsAsync(automatic: false, request.Token);
        }
        finally { updateRequest = null; }
    }

    internal void ApplyAppearance(LauncherAppearance appearance) => Root.RequestedTheme = appearance switch
    {
        LauncherAppearance.Light => ElementTheme.Light,
        LauncherAppearance.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    internal bool IsBusy => busy;
    internal void ClearProblem() => Problem.IsOpen = false;

    internal async Task<string?> PickFolderAsync(string title = "Choose the Call of Duty 2 folder")
    {
        var picker = new FolderPicker(AppWindow.Id) { Title = title };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    internal async Task RunAsync(Func<Task> action, bool clearProblem = true)
    {
        if (busy || closing)
            return;
        busy = true;
        if (clearProblem)
            Problem.IsOpen = false;
        try { await ShowProblemAsync(action); }
        finally
        {
            busy = false;
            if (closeRequested)
            {
                closeRequested = false;
                await CompleteCloseAsync();
            }
            if (!flushedForClose && !windowsSessionEnding)
            {
                ScheduleActivation();
                RefreshTaskbarJumpList();
            }
        }
    }

    internal async Task ShowProblemAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or FormatException or LaunchPlanException or System.Runtime.InteropServices.COMException
            or System.ComponentModel.Win32Exception
            or ConfigDirectoryException or ConfigImportSessionException or ConfigBindingException or ConfigEditException)
        {
            ShowShellNotice(error.Message, InfoBarSeverity.Error);
        }
    }

}

internal enum LauncherPlace { Setup, Home, Settings, GameDataSettings, UpdateSettings, Servers, Library, Configs }
