using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

/// The Mac's sheet anatomy (SheetScaffold, AllSheets board) for WinUI's ContentDialog: a symbol on a 44-point
/// tile, a title that names the thing and one sentence of consequence; and its single-decision alert, the
/// app's mark over a centred title and message. LauncherSheet and LauncherAlert supply the rest.
internal static class SheetPresentation
{
    internal enum Tone { Accent, Negative, Positive }

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
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        words.Children.Add(heading);
        if (!string.IsNullOrEmpty(message))
        {
            var sentence = new TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                LineHeight = 18
            };
            Follow(sentence, theme => sentence.Foreground = theme("secondary"));
            words.Children.Add(sentence);
        }
        Put(row, 1, words);
        return row;
    }

    /// The single-decision alert: the app's mark, a centred title and message; buttons side by side, or
    /// stacked with the destructive action first.
    internal static T AsAlert<T>(this T dialog, bool destructive = false) where T : ContentDialog
    {
        string title = dialog.Title as string ?? AutomationProperties.GetName(dialog);
        dialog.Title = new TextBlock { Text = title, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(dialog, title);
        if (dialog.Content is TextBlock text)
        {
            text.TextAlignment = TextAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.MinWidth = 0;
        }
        dialog.Style = StyleFor(destructive ? "LauncherDestructiveAlert" : "LauncherAlert");
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
