using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

// Settings as its own window, as on the Mac: one at a time, opened with Ctrl+comma, the sidebar's Settings
// entry, Jump to and every "open Settings" path. It takes the chosen pane's height (300 at the least, the
// screen at the most), keeping its top edge where it is; Ctrl+Tab and Ctrl+Shift+Tab step through the panes.
public sealed partial class SettingsWindow : Window
{
    private const double PaneWidth = 900, FittedMinimum = 300;
    private readonly MainWindow shell;
    private readonly Microsoft.UI.System.ThemeSettings systemTheme;

    internal SettingsWindow(MainWindow shell, Platform.WindowsLauncherSession session, ElementTheme theme, string? pane)
    {
        this.shell = shell;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "mark.ico"));
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(SettingsCaption);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }
        else
            CaptionTitle.Visibility = Visibility.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }
        systemTheme = Microsoft.UI.System.ThemeSettings.CreateForWindowId(AppWindow.Id);
        systemTheme.Changed += OnSystemThemeChanged;
        SettingsRoot.ActualThemeChanged += (_, _) => UpdateCaption();
        SettingsRoot.RequestedTheme = theme;
        UpdateCaption();
        Page = new SettingsPage(shell, session, pane);
        Page.PaneChanged += OnPaneChanged;
        Page.ContentResized += FitToPane;
        SettingsFrame.Content = Page;
        OnPaneChanged(Page.PaneTitle);
        foreach (var (key, shift) in new[] { (VirtualKey.Tab, false), (VirtualKey.Tab, true) })
        {
            var step = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control | (shift ? VirtualKeyModifiers.Shift : 0) };
            step.Invoked += (_, args) => { args.Handled = true; Page.StepPane(shift ? -1 : 1); };
            SettingsRoot.KeyboardAccelerators.Add(step);
        }
        var close = new KeyboardAccelerator { Key = VirtualKey.W, Modifiers = VirtualKeyModifiers.Control };
        close.Invoked += (_, args) => { args.Handled = true; Close(); };
        SettingsRoot.KeyboardAccelerators.Add(close);
        SettingsRoot.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        Activated += (_, args) => IsInFront = args.WindowActivationState != WindowActivationState.Deactivated;
        Closed += (_, _) => systemTheme.Changed -= OnSystemThemeChanged;
        Place();
    }

    internal SettingsPage Page { get; }
    internal bool IsInFront { get; private set; }
    internal XamlRoot DialogRoot => SettingsRoot.XamlRoot;

    internal void Show(string? pane)
    {
        if (pane is not null)
            Page.OpenJumpPane(pane);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    internal void ApplyTheme(ElementTheme theme) => SettingsRoot.RequestedTheme = theme;

    private void OnPaneChanged(string title)
    {
        Title = title;
        CaptionTitle.Text = title;
    }

    private void OnSystemThemeChanged(Microsoft.UI.System.ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { UpdateCaption(); Page.UpdateContrast(); });

    private void UpdateCaption()
    {
        if (AppWindowTitleBar.IsCustomizationSupported())
            ApplyCaptionTheme(AppWindow.TitleBar, SettingsRoot.ActualTheme, systemTheme.HighContrast);
    }

    // Centred over the launcher window the first time, 900 wide and 700 high until the pane is measured.
    private void Place()
    {
        var owner = shell.AppWindow;
        var work = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = WindowScale(owner);
        int width = (int)Math.Ceiling(PaneWidth * scale), height = Math.Min((int)Math.Ceiling(700 * scale), work.Height);
        int x = owner.Position.X + (owner.Size.Width - width) / 2;
        int y = owner.Position.Y + Math.Max(0, (owner.Size.Height - height) / 3);
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private static double WindowScale(AppWindow window)
    {
        uint dpi = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id));
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private double lastFitted;

    // The Mac window takes each pane's height, scrolling only when a pane is taller than the screen.
    private void FitToPane()
    {
        if (SettingsRoot.XamlRoot is null || Page.PaneContentHeight <= 0)
            return;
        double scale = SettingsRoot.XamlRoot.RasterizationScale;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        double chrome = AppWindow.Size.Height / scale - SettingsRoot.ActualHeight;
        double content = Math.Max(FittedMinimum, SettingsRoot.RowDefinitions[0].ActualHeight + Page.HeaderHeight + Page.PaneContentHeight);
        double height = Math.Min(content + Math.Max(0, chrome), work.Height / scale - 24);
        if (Math.Abs(height - lastFitted) < 1)
            return;
        lastFitted = height;
        int pixels = (int)Math.Ceiling(height * scale);
        if (Math.Abs(AppWindow.Size.Height - pixels) <= 1)
            return;
        var position = AppWindow.Position;
        int top = Math.Min(position.Y, work.Y + work.Height - pixels);
        AppWindow.MoveAndResize(new RectInt32(position.X, Math.Max(work.Y, top), AppWindow.Size.Width, pixels));
    }
}
