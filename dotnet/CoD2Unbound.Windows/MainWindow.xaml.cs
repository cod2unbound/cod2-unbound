using System.Text.Json;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow : Window
{
    private readonly WindowsLauncherSession session = new();
    private SetupSession? pendingData;
    private bool busy;
    private bool loaded;
    private bool updatingSettings;
    private double sizedAtScale;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (loaded)
            return;
        loaded = true;
        ResizeForDisplay(startup: true);
        await RunAsync(async () =>
        {
            await session.InitializeAsync();
            SyncSettings(session.Settings.Working);
            if (session.SetupComplete)
                await ShowHomeAsync();
            else
                ShowSetup();
        });
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ResizeForDisplay();
        UpdatePaneMode(args.NewSize.Width);
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
            await RunAsync(ShowSettingsAsync);
        else if (ReferenceEquals(args.InvokedItemContainer, HomeItem))
            await RunAsync(ShowHomeAsync);
    }

    private async void OnHomeInvoked(object sender, RoutedEventArgs args) => await RunAsync(ShowHomeAsync);
    private async void OnSettingsInvoked(object sender, RoutedEventArgs args) => await RunAsync(ShowSettingsAsync);

    private async Task ShowHomeAsync()
    {
        pendingData = null;
        ShowPage(HomePage);
        Nav.SelectedItem = HomeItem;
        var data = session.Setup.ChooseData(session.Setup.DataPath);
        HomeEdition.Text = session.Settings.Working.Engine == GameEdition.Original ? "Original 1.3" : "CoD2x 1.4.6.8";
        HomeDataStatus.Text = DataSummary(data);
        LaunchReason.Text = data.State == SetupDataState.Found
            ? "Launching the game from Windows is unavailable in this build."
            : "Choose a complete game data folder before launching.";
        AutomationProperties.SetHelpText(LaunchButton, LaunchReason.Text);

        HomeLoadout.Text = PaneLoadout.Text = "";
        HomeLoadout.Visibility = PaneLoadout.Visibility = Visibility.Collapsed;
        if (await session.ReadLoadoutAsync() is { } loadout)
        {
            HomeLoadout.Text = "Loadout · " + loadout.DisplayName;
            PaneLoadout.Text = HomeLoadout.Text;
            HomeLoadout.Visibility = Visibility.Visible;
            PaneLoadout.Visibility = IsVisible(Nav.IsPaneOpen);
        }
    }

    private Task ShowSettingsAsync()
    {
        ShowPage(SettingsPage);
        Nav.SelectedItem = Nav.SettingsItem;
        pendingData = null;
        session.Setup.ChooseData(session.Setup.DataPath);
        RenderSettingsData();
        return Task.CompletedTask;
    }

    private void ShowSetup()
    {
        ShowPage(SetupPage);
        var setup = session.Setup;
        (SetupTitle.Text, SetupDescription.Text) = setup.Step switch
        {
            SetupStep.Welcome => ("Welcome to CoD2 Unbound", "Set up your copy of Call of Duty 2 for multiplayer."),
            SetupStep.Data => ("Game Data", "CoD2 Unbound uses the game files from your Steam or retail copy."),
            SetupStep.Key => ("Your CD key", "Some servers need a Call of Duty 2 multiplayer key. You can add it now or skip this step."),
            SetupStep.Ready => ("Ready to play", "Your game data is ready. Finish setup to open Home."),
            _ => throw new InvalidOperationException("Unknown launcher setup step.")
        };
        bool keyStep = setup.Step == SetupStep.Key;
        SetupKeyCard.Visibility = SkipKey.Visibility = IsVisible(keyStep);
        StoredKey.Visibility = ReplaceKey.Visibility = IsVisible(keyStep && setup.HasStoredKey && !setup.ReplacingKey);
        KeyInput.Visibility = IsVisible(keyStep && (!setup.HasStoredKey || setup.ReplacingKey));
        SetupBack.Visibility = IsVisible(setup.Step != SetupStep.Welcome);
        SetupDataCard.Visibility = IsVisible(setup.Step == SetupStep.Data);
        SetupDataStatus.Text = DataSummary(setup.Data);
        SetupDataDetail.Text = setup.Data.Problem ?? (setup.Data.State == SetupDataState.Found ? SetupSession.FoundSummary : "");
        SetupDataDetail.Visibility = IsVisible(SetupDataDetail.Text.Length > 0);
        SetupDataPath.Text = setup.Data.Path;
        SetupDataPath.Visibility = IsVisible(SetupDataPath.Text.Length > 0);
        SetupNext.Content = setup.Step == SetupStep.Ready ? "Finish setup" : "Continue";
        UpdateContinue();
    }

    private void ShowPage(StackPanel active)
    {
        foreach (var page in new[] { SetupPage, HomePage, SettingsPage })
            page.Visibility = IsVisible(ReferenceEquals(active, page));
        Nav.IsPaneVisible = JumpButton.IsEnabled = session.SetupComplete;
        if (Nav.IsPaneVisible)
            UpdatePaneMode(Root.ActualWidth);
    }

    private void OnSetupBack(object sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        // The core exposes the hidden Personalize step for hosts with a profile editor.
        session.Setup.Back();
        if (session.Setup.Step == SetupStep.Personalize)
            session.Setup.Back();
        Problem.IsOpen = false;
        KeyInput.Password = "";
        ShowSetup();
    }

    private void OnReplaceKey(object sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        session.Setup.ReplaceKey();
        ShowSetup();
        KeyInput.Focus(FocusState.Programmatic);
    }

    private void OnKeyChanged(object sender, RoutedEventArgs args) => UpdateContinue();

    private void UpdateContinue() => SetupNext.IsEnabled = session.Setup.Step switch
    {
        SetupStep.Data => session.Setup.Data.State == SetupDataState.Found,
        SetupStep.Key => session.Setup.KeyReady(KeyInput.Password),
        _ => true
    };

    private async void OnContinueSetup(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var setup = session.Setup;
        await setup.ContinueAsync(KeyInput.Password);
        KeyInput.Password = "";
        if (setup.Step == SetupStep.Personalize)
            setup.PersonalizationSaved();
        if (setup.Step == SetupStep.Complete)
        {
            session.FinishSetup();
            await ShowHomeAsync();
        }
        else
        {
            ShowSetup();
        }
    });

    private void OnSkipKey(object sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        if (session.Setup.SkipKey())
            session.Setup.PersonalizationSaved();
        KeyInput.Password = "";
        ShowSetup();
    }

    private async void OnChooseSetupFolder(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (await PickFolderAsync() is { } path)
        {
            session.Setup.ChooseData(path);
            ShowSetup();
        }
    });

    private async void OnChooseSettingsFolder(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (await PickFolderAsync() is { } path)
        {
            pendingData ??= await session.ChangeGameDataAsync();
            pendingData.ChooseData(path);
            RenderSettingsData();
        }
    });

    private async void OnSaveFolder(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (pendingData is { } change && await change.ContinueAsync())
        {
            session.SetGameData(change.DataPath);
            pendingData = null;
        }
        RenderSettingsData();
    });

    private void OnCancelFolder(object sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        pendingData = null;
        Problem.IsOpen = false;
        RenderSettingsData();
    }

    private void RenderSettingsData()
    {
        var data = pendingData?.Data ?? session.Setup.Data;
        SettingsDataStatus.Text = DataSummary(data);
        SettingsDataDetail.Text = data.Problem ?? (data.State == SetupDataState.Found ? SetupSession.FoundSummary : "");
        SettingsDataDetail.Visibility = IsVisible(SettingsDataDetail.Text.Length > 0);
        SettingsDataPath.Text = data.Path;
        SettingsDataPath.Visibility = IsVisible(data.Path.Length > 0);
        SettingsDataActions.Visibility = IsVisible(pendingData is not null);
        SaveFolder.IsEnabled = data.State == SetupDataState.Found;
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(AppWindow.Id) { Title = "Choose the Call of Duty 2 folder" };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private void SyncSettings(GameSettings settings)
    {
        updatingSettings = true;
        try
        {
            EditionChoice.SelectedIndex = settings.Engine == GameEdition.Original ? 1 : 0;
            ThemeChoice.SelectedIndex = (int)settings.Appearance;
            Root.RequestedTheme = settings.Appearance switch
            {
                LauncherAppearance.Light => ElementTheme.Light,
                LauncherAppearance.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
        finally { updatingSettings = false; }
    }

    private async void OnSettingsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!loaded || updatingSettings || ThemeChoice.SelectedIndex < 0 || EditionChoice.SelectedIndex < 0)
            return;
        var settings = session.Settings;
        if (busy)
        {
            SyncSettings(settings.Working);
            return;
        }
        var choice = settings.Working with
        {
            Engine = EditionChoice.SelectedIndex == 1 ? GameEdition.Original : GameEdition.Cod2x,
            Appearance = (LauncherAppearance)ThemeChoice.SelectedIndex
        };
        await RunAsync(async () =>
        {
            settings.Replace(choice);
            try
            {
                await settings.FlushAsync();
            }
            catch
            {
                settings.Replace(settings.Saved);
                SyncSettings(settings.Saved);
                throw;
            }
            SyncSettings(choice);
        });
    }

    private async Task RunAsync(Func<Task> action)
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

    private static Visibility IsVisible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private static string DataSummary(SetupDataChoice choice) => choice.State switch
    {
        SetupDataState.Found => "Game data found",
        SetupDataState.Damaged or SetupDataState.UnfinishedSteam => "Game data is incomplete",
        _ => "Game data not found"
    };
}
