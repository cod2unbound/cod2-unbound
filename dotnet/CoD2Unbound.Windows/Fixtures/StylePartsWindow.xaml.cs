#if UPDATE_FIXTURES
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Fixtures;

public sealed partial class StylePartsWindow : Window
{
    internal StylePartsWindow(ElementTheme theme)
    {
        InitializeComponent();
        PartsRoot.RequestedTheme = theme;
        SampleWordmark.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
            new Uri(theme == ElementTheme.Light ? "ms-appx:///Assets/wordmark-ink.svg" : "ms-appx:///Assets/wordmark.svg"));
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(CaptionRegion);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            ApplyCaptionTheme(AppWindow.TitleBar, theme,
                Microsoft.UI.System.ThemeSettings.CreateForWindowId(AppWindow.Id).HighContrast);
        }
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(work.Width, 1400), Math.Min(work.Height, 1100)));
    }

    private async void OpenSheet(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = PartsRoot.XamlRoot, RequestedTheme = PartsRoot.RequestedTheme,
            Title = "Rename “Main”", PrimaryButtonText = "Rename", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Body("Each loadout is its own Call of Duty 2 player profile."),
                    new TextBox { Text = "Main", Style = StyleFor("CapsuleTextBox") }
                }
            }
        };
        await dialog.ShowAsync();
    }
}
#endif
