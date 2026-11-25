using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CoD2Unbound.Windows;

public partial class App : Application
{
    private readonly Action<MainWindow> windowCreated;
    private Window? window;
#if UPDATE_FIXTURES
    private readonly ElementTheme? partsTheme;

    internal App(ElementTheme partsTheme) : this(static _ => { })
    {
        this.partsTheme = partsTheme;
    }
#endif

    internal App(Action<MainWindow> windowCreated)
    {
        this.windowCreated = windowCreated;
#if UPDATE_FIXTURES
        UnhandledException += (_, failure) => StartupTrace.Mark($"XAML error: {failure.Message} {failure.Exception}");
#endif
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!WindowsVersion.IsWindows11OrLater)
            ApplyAcrylicFallback();

#if UPDATE_FIXTURES
        if (partsTheme is { } theme)
        {
            window = new Fixtures.StylePartsWindow(theme);
            window.Activate();
            return;
        }
#endif
        var mainWindow = new MainWindow();
        window = mainWindow;
        windowCreated(mainWindow);
        window.Activate();
    }

    private void ApplyAcrylicFallback()
    {
        // WinUI may reject Application.Resources in the constructor, even after InitializeComponent.
        // Set the shared brushes before building any window, including the brushes for the inactive theme.
        try
        {
            string[] keys = ["GlassAcrylicBrush", "ContentDialogBackground", "FlyoutPresenterBackground"];
            int launcherBrushes = 0;
            foreach (var resource in Resources.MergedDictionaries)
            {
                bool designTokens = resource.Source?.OriginalString.EndsWith("/Themes/DesignTokens.xaml", StringComparison.Ordinal) == true;
                foreach (var theme in resource.ThemeDictionaries.Values.OfType<ResourceDictionary>())
                {
                    foreach (string key in keys)
                    {
                        if (theme.TryGetValue(key, out var value) && value is AcrylicBrush acrylic)
                        {
                            acrylic.AlwaysUseFallback = true;
                            if (designTokens)
                                launcherBrushes++;
                        }
                    }
                }
            }
            if (launcherBrushes == 2 * keys.Length)
                StartupTrace.Mark("Windows 10 acrylic fallback applied");
        }
        catch (Exception error)
        {
            // Glass is optional. A resource error must not prevent the launcher from opening.
            System.Diagnostics.Trace.TraceWarning($"Windows 10 acrylic fallback unavailable (0x{error.HResult:X8})");
            StartupTrace.Mark($"Windows 10 acrylic fallback unavailable (0x{error.HResult:X8})");
        }
    }
}
