using System.Text.Json;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.Views;
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

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        windowTheme = ThemeSettings.CreateForWindowId(AppWindow.Id);
        windowTheme.Changed += OnSystemThemeChanged;
        Root.ActualThemeChanged += OnAppThemeChanged;
        Closed += (_, _) => windowTheme.Changed -= OnSystemThemeChanged;
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
        DispatcherQueue.TryEnqueue(UpdateCaptionColors);

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

    private void OnPaneChanged(NavigationView sender, object args) =>
        PaneLoadout.Visibility = IsVisible(Nav.IsPaneOpen && PaneLoadout.Text.Length > 0);

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
    }

    private async void OnHomeInvoked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => NavigateAsync(LauncherPlace.Home));

    private async void OnSettingsInvoked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => NavigateAsync(LauncherPlace.Settings));

    internal async Task NavigateAsync(LauncherPlace place)
    {
        Nav.IsPaneVisible = JumpButton.IsEnabled = session.SetupComplete;
        if (Nav.IsPaneVisible)
            UpdatePaneMode(Root.ActualWidth);
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
        else
        {
            Nav.SelectedItem = Nav.SettingsItem;
            ContentFrame.Content = new SettingsPage(this, session);
        }
    }

    internal void ShowPaneLoadout(string? loadout)
    {
        PaneLoadout.Text = loadout ?? "";
        OnPaneChanged(Nav, EventArgs.Empty);
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

    internal async Task RunAsync(Func<Task> action)
    {
        if (busy)
            return;
        busy = true;
        Problem.IsOpen = false;
        try
        {
            await action();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Problem.Message = error.Message;
            Problem.IsOpen = true;
        }
        finally { busy = false; }
    }

}

internal enum LauncherPlace { Setup, Home, Settings }
