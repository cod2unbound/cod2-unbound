using Microsoft.UI.Xaml;

namespace CoD2Unbound.Windows;

public partial class App : Application
{
    private readonly Action<MainWindow> windowCreated;
    private MainWindow? window;

    internal App(Action<MainWindow> windowCreated)
    {
        this.windowCreated = windowCreated;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        windowCreated(window);
        window.Activate();
    }
}
