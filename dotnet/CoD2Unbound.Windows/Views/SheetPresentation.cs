using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

/// Supplies the app's artwork and heading content for a standard ContentDialog.
internal static class SheetPresentation
{
    internal enum Tone { Accent, Negative, Positive }

    internal static global::Windows.Foundation.IAsyncOperation<ContentDialogResult> ShowSheetAsync(this ContentDialog dialog)
    {
        if (!new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            var theme = dialog.RequestedTheme;
            if (theme == ElementTheme.Default && dialog.XamlRoot?.Content is FrameworkElement root)
                theme = root.ActualTheme;
            var smoke = ThemeBrush("SheetSmokeBrush", theme, contrast: false);
            // WinUI hosts the smoke in a separate popup. Its template and its fallback
            // use these two resources, so scope the override to the sheet.
            dialog.Resources["ContentDialogSmokeFill"] = smoke;
            dialog.Resources["SystemControlPageBackgroundMediumAltMediumBrush"] = smoke;
        }
        else
        {
            dialog.Resources.Remove("ContentDialogSmokeFill");
            dialog.Resources.Remove("SystemControlPageBackgroundMediumAltMediumBrush");
        }
        return dialog.ShowAsync();
    }

    internal static T WithWidth<T>(this T dialog, string widthKey) where T : ContentDialog
    {
        var width = (double)Application.Current.Resources[widthKey];
        dialog.Resources["ContentDialogMinWidth"] = width;
        dialog.Resources["ContentDialogMaxWidth"] = width;
        return dialog;
    }

    /// Puts the symbol tile and the optional sentence beside the dialog's title. `glyphKey` names a Fluent or
    /// System glyph resource; "mark" uses the app's mark (update sheets).
    internal static T WithSymbol<T>(this T dialog, string glyphKey, string? message = null, Tone tone = Tone.Accent) where T : ContentDialog
    {
        string title = dialog.Title as string ?? AutomationProperties.GetName(dialog);
        dialog.Title = Heading(glyphKey, title, message, tone);
        AutomationProperties.SetName(dialog, title);
        return dialog;
    }

    internal static void SetSheetTitle(ContentDialog dialog, string glyphKey, string title, string? message = null, Tone tone = Tone.Accent)
    {
        dialog.Title = Heading(glyphKey, title, message, tone);
        AutomationProperties.SetName(dialog, title);
    }

    private static Grid Heading(string glyphKey, string title, string? message, Tone tone)
    {
        var row = Columns(14, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        FrameworkElement mark;
        if (glyphKey == "mark")
            mark = new Image { Width = 48, Height = 48, Source = new BitmapImage(new Uri("ms-appx:///Assets/mark-48.png")) };
        else
        {
            bool system = glyphKey.StartsWith("System", StringComparison.Ordinal);
            var resources = Application.Current.Resources;
            var icon = new FontIcon
            {
                FontFamily = (FontFamily)resources[system ? "SystemIcons" : "FluentRegular"],
                Glyph = (string)resources[glyphKey], FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var tile = new Border { Style = StyleFor("SheetIconTile"), Child = icon };
            string ink = tone switch { Tone.Negative => "negative", Tone.Positive => "positive", _ => "accent" };
            Follow(tile, theme => icon.Foreground = theme(ink));
            mark = tile;
        }
        mark.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAccessibilityView(mark, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        row.Children.Add(mark);
        var words = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Style = StyleFor("SubtitleTextBlockStyle") };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        words.Children.Add(heading);
        if (!string.IsNullOrEmpty(message))
        {
            var sentence = new TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap, Style = StyleFor("BodyTextBlockStyle")
            };
            Follow(sentence, theme => sentence.Foreground = theme("secondary"));
            words.Children.Add(sentence);
        }
        Put(row, 1, words);
        return row;
    }

    /// Keeps the brand mark in the alert's heading and lets the native command row lay out the choices.
    internal static T AsAlert<T>(this T dialog, bool destructive = false) where T : ContentDialog
    {
        dialog.WithSymbol("mark");
        if (destructive)
            dialog.PrimaryButtonStyle = StyleFor("AlertDestructiveButton");
        return dialog;
    }

    // Brushes made in code do not follow a theme change; these re-read the palette for the element's theme.
    private static void Follow(FrameworkElement element, Action<Func<string, Brush>> paint)
    {
        void Apply()
        {
            bool contrast = new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            paint(key => ThemeBrush(key, element.ActualTheme, contrast));
        }
        element.Loaded += (_, _) => Apply();
        element.ActualThemeChanged += (_, _) => Apply();
    }
}
