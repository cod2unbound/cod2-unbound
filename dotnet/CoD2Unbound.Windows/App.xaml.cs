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
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            foreach (var resource in Resources.MergedDictionaries)
            {
                foreach (var theme in resource.ThemeDictionaries.Values.OfType<ResourceDictionary>())
                {
                    foreach (string key in new[] { "GlassAcrylicBrush", "ContentDialogBackground", "FlyoutPresenterBackground" })
                        if (theme.TryGetValue(key, out var value) && value is AcrylicBrush acrylic)
                            acrylic.AlwaysUseFallback = true;
                }
            }
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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
}
