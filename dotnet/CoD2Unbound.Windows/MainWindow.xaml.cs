using System.Text.Json;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.Views;
using CoD2Unbound.Windows.ViewModels;
using static CoD2Unbound.Windows.Views.PagePresentation;
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
    private bool busy;
    private bool loaded;
    private double sizedAtScale;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer gameWatcher;
    private GameSessionState? lastGame;
    private bool minimizedForGame;
    private bool flushedForClose;
    private bool closing;
    private DateTimeOffset hostPolled;
    private DateTimeOffset? attemptedUpdate;
    private readonly DateTimeOffset firstUpdate = DateTimeOffset.UtcNow.AddSeconds(8);

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        windowTheme = ThemeSettings.CreateForWindowId(AppWindow.Id);
        windowTheme.Changed += OnSystemThemeChanged;
        Root.ActualThemeChanged += OnAppThemeChanged;
        gameWatcher = DispatcherQueue.CreateTimer();
        gameWatcher.Interval = TimeSpan.FromSeconds(1);
        gameWatcher.Tick += OnWatchGame;
        session.ClientStarted += OnClientStarted;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            gameWatcher.Stop();
            windowTheme.Changed -= OnSystemThemeChanged;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (loaded)
            return;
        loaded = true;
        ResizeForDisplay(startup: true);
        UpdateCaptionColors();
        await RunAsync(async () =>
        {
            await session.InitializeAsync();
            ApplyAppearance(session.Settings.Working.Appearance);
            await NavigateAsync(session.SetupComplete ? LauncherPlace.Home : LauncherPlace.Setup);
            gameWatcher.Start();
        });
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ResizeForDisplay();
        UpdatePaneMode(args.NewSize.Width);
    }

    private void OnJumpHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        // The native title and caption buttons reserve different widths on each side.
        double hostLeft = JumpHost.TransformToVisual(WindowBar).TransformPoint(default).X;
        double hostWidth = args.NewSize.Width;
        double availableOffset = Math.Max(0, (hostWidth - JumpButton.Width) / 2);
        double centerOffset = WindowBar.ActualWidth / 2 - hostLeft - hostWidth / 2;
        double offset = Math.Clamp(centerOffset, -availableOffset, availableOffset);
        JumpButton.Margin = new Thickness(Math.Max(0, 2 * offset), 0, Math.Max(0, -2 * offset), 0);
    }

    private void OnAppThemeChanged(FrameworkElement sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateCaptionColors);

    private void OnSystemThemeChanged(ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateCaptionColors();
            if (ContentFrame.Content is HomePage home)
                home.RenderArtScrim();
        });

    internal bool IsHighContrast => windowTheme.HighContrast;
    internal ElementTheme DialogTheme => Root.RequestedTheme;

    private void UpdateCaptionColors()
    {
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
        PlayerPlate.Visibility = IsVisible(Nav.IsPaneOpen && session.SetupComplete);
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

    private void OnJumpShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (JumpButton.IsEnabled)
        {
            JumpMenu.ShowAt(JumpButton);
            args.Handled = true;
        }
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

    private async void OnHomeInvoked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => NavigateAsync(LauncherPlace.Home));

    private async void OnSettingsInvoked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => NavigateAsync(LauncherPlace.Settings));

    private async void OnServersInvoked(object sender, RoutedEventArgs args) =>
        await OpenServersAsync(ServerScope.All);

    private async void OnLibraryInvoked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => NavigateAsync(LauncherPlace.Library));

    private async void OnConfigsInvoked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => NavigateAsync(LauncherPlace.Configs));

    internal Task OpenServersAsync(ServerScope scope, string? address = null) =>
        RunAsync(() => NavigateAsync(LauncherPlace.Servers, scope, address));

    internal Task OpenCheckupAsync() => RunAsync(async () =>
    {
        await NavigateAsync(LauncherPlace.Configs);
        if (ContentFrame.Content is ConfigsPage configs)
            configs.OpenCheckup();
    });

    internal async Task NavigateAsync(LauncherPlace place, ServerScope scope = ServerScope.All, string? address = null)
    {
        Nav.IsPaneVisible = JumpButton.IsEnabled = session.SetupComplete;
        if (Nav.IsPaneVisible)
        {
            UpdatePaneMode(Root.ActualWidth);
            await RefreshPlayerAsync();
            OnPaneChanged(Nav, EventArgs.Empty);
        }
        if (place == LauncherPlace.Setup)
        {
            var page = new SetupPage(this, session);
            ContentFrame.Content = page;
            page.Refresh();
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
            if (session.Settings.Working.RefreshServersOnOpen)
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
            var model = await Task.Run(() => new ConfigsViewModel(session, owner));
            Nav.SelectedItem = ConfigsItem;
            ContentFrame.Content = new ConfigsPage(this, model);
        }
        else
        {
            Nav.SelectedItem = Nav.SettingsItem;
            ContentFrame.Content = new SettingsPage(this, session, openGameData: place == LauncherPlace.GameDataSettings);
        }
        object? content = ContentFrame.Content;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(ContentFrame.Content, content) && content is DependencyObject root &&
                FocusManager.FindFirstFocusableElement(root) is Control first)
                first.Focus(FocusState.Programmatic);
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

    private void OnClientStarted()
    {
        if (session.Settings.Working.HideWhilePlaying && AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State != OverlappedPresenterState.Minimized)
        {
            minimizedForGame = true;
            presenter.Minimize();
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (flushedForClose || !loaded || session.Settings is null)
            return;
        if (!closing && !busy && !session.Hosting.HasActiveServers && session.Settings.Saved == session.Settings.Working)
            return;
        args.Cancel = true;
        if (closing || busy)
            return;
        closing = true;
        try
        {
            await ShowProblemAsync(async () =>
            {
                await session.Settings.FlushAsync();
                await session.Hosting.StopAllAsync();
                flushedForClose = true;
                Close();
            });
        }
        finally { closing = false; }
    }

    internal async Task RefreshPlayerAsync()
    {
        var player = await session.ReadPlayerAsync();
        PlayerName.Text = PlayerCardName.Text = player.Name;
        PaneLoadout.Text = player.Loadout ?? player.Key;
        PlayerCardLoadout.Text = player.Loadout ?? "Loadout unavailable";
        PlayerCardKey.Text = player.Key;
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
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = QuakeColors.Plain(favorite.Name), Tag = favorite.Address,
                Icon = new SymbolIcon(Symbol.Favorite), SelectsOnInvoked = false
            });
        }
    }

    private async void OnWatchGame(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!session.SetupComplete || busy || closing)
            return;
        await RunAsync(async () =>
        {
            await session.Game.ReconcileAsync();
            var state = session.Game.State;
            if (!state.Running && minimizedForGame)
            {
                minimizedForGame = false;
                if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                    presenter.Restore();
            }
            var now = DateTimeOffset.UtcNow;
            if (session.Hosting.HasActiveServers && now - hostPolled >= TimeSpan.FromSeconds(3))
            {
                hostPolled = now;
                await session.Hosting.PollAsync();
                if (ContentFrame.Content is ServersPage hosting)
                    hosting.UpdateHostingState();
            }
            if (session.TestMaster is null && session.Settings.Working.CheckUpdatesAutomatically && now >= firstUpdate &&
                LauncherUpdates.AutoCheckDue(now, attemptedUpdate, session.Library.DispatchesCheckedAt, state.Running))
            {
                attemptedUpdate = now;
                await session.CheckUpdatesAsync(automatic: true);
                if (ContentFrame.Content is SettingsPage settings)
                    settings.RefreshUpdates();
            }
            if (lastGame?.Running == state.Running && lastGame.LastExit == state.LastExit)
                return;
            lastGame = state;
            if (ContentFrame.Content is HomePage home)
                home.RenderGame(state);
            if (ContentFrame.Content is ServersPage servers)
                servers.UpdateGameState(state);
        }, clearProblem: false);
    }

    internal void ApplyAppearance(LauncherAppearance appearance) => Root.RequestedTheme = appearance switch
    {
        LauncherAppearance.Light => ElementTheme.Light,
        LauncherAppearance.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    internal bool IsBusy => busy;
    internal void ClearProblem() => Problem.IsOpen = false;

    internal async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(AppWindow.Id) { Title = "Choose the Call of Duty 2 folder" };
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
        finally { busy = false; }
    }

    internal async Task ShowProblemAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or FormatException or LaunchPlanException or System.Runtime.InteropServices.COMException
            or ConfigDirectoryException or ConfigImportSessionException or ConfigBindingException or ConfigEditException)
        {
            Problem.Message = error.Message;
            Problem.IsOpen = true;
        }
    }

}

internal enum LauncherPlace { Setup, Home, Settings, GameDataSettings, Servers, Library, Configs }
