using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CoD2Unbound.Windows;

public partial class App : Application
{
    private readonly Action<MainWindow> windowCreated;
    private MainWindow? window;

    internal App(Action<MainWindow> windowCreated)
    {
        this.windowCreated = windowCreated;
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
        window = new MainWindow();
        windowCreated(window);
        window.Activate();
    }
}
