using Microsoft.UI.Xaml;

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
