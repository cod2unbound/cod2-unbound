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
        Resources["LauncherIconFont"] = new FontFamily(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? "Segoe Fluent Icons" : "Segoe MDL2 Assets");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        windowCreated(window);
        window.Activate();
    }
}
