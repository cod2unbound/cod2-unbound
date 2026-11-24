using System.Globalization;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private static TextBlock CheckStrong(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = StyleFor("BodyStrongTextBlockStyle") };

    private static Border CheckCard(UIElement child) => new() { Style = StyleFor("FieldCard"), Child = child };

    private static InfoBar CheckNotice(InfoBarSeverity severity, string title, string message) => new()
    {
        IsOpen = true, IsClosable = false, Severity = severity, Title = title, Message = message
    };

    private TextBlock CheckTone(string text, ConfigCheckupTone tone)
    {
        var label = Body(text);
        ColorCheckTone(label, tone);
        label.ActualThemeChanged += (_, _) => ColorCheckTone(label, tone);
        return label;
    }

    private void ColorCheckTone(TextBlock label, ConfigCheckupTone tone)
    {
        if (tone != ConfigCheckupTone.Neutral)
            label.Foreground = ThemeBrush(CheckBrush(tone), label.ActualTheme, shell.IsHighContrast);
    }

    private static string CheckBrush(ConfigCheckupTone tone) => tone switch
    {
        ConfigCheckupTone.Positive => "PositiveBrush",
        ConfigCheckupTone.Caution => "GameYellowBrush",
        ConfigCheckupTone.Negative => "NegativeBrush",
        _ => "HeroAccentBrush"
    };

    private static Grid CheckPair(FrameworkElement left, FrameworkElement right, double stackBelow, double leftNarrow = 0, bool rightAuto = false, double rightWidth = 0)
    {
        var rightColumn = rightWidth > 0 ? new GridLength(rightWidth) :
            rightAuto ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        var layout = Columns(rightWidth > 0 ? 16 : 12, new GridLength(1, GridUnitType.Star), rightColumn);
        layout.RowSpacing = 12;
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.VerticalAlignment = right.VerticalAlignment = VerticalAlignment.Top;
        Put(layout, 0, left);
        Put(layout, 1, right);
        layout.SizeChanged += (_, args) =>
        {
            double width = args.NewSize.Width;
            bool stacked = width < stackBelow;
            layout.ColumnDefinitions[0].Width = !stacked && leftNarrow > 0 && width < 730
                ? new GridLength(leftNarrow) : new GridLength(1, GridUnitType.Star);
            layout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : rightColumn;
            Grid.SetColumn(right, stacked ? 0 : 1);
            Grid.SetRow(right, stacked ? 1 : 0);
        };
        return layout;
    }

    private static string HistoryDate(DateTimeOffset time) =>
        time.LocalDateTime.ToString("d MMM 'at' HH:mm", CultureInfo.CurrentCulture);

    internal static string CheckTimingCopy(string text) => text
        .Replace("Your frame cap becomes 250 fps. Your Mac’s actual frame rate may be lower.",
            "The saved timing limit becomes 250. How smoothly the game runs depends on this PC.", StringComparison.Ordinal)
        .Replace("125–250 frames per second", "timing limits from 125 to 250", StringComparison.Ordinal)
        .Replace("Your frame rate also affects how often they are sent.", "Game timing also affects when updates are sent.", StringComparison.Ordinal)
        .Replace("frame rate cap", "game timing setting", StringComparison.OrdinalIgnoreCase)
        .Replace("frame cap", "timing limit", StringComparison.OrdinalIgnoreCase)
        .Replace(" fps", "", StringComparison.Ordinal)
        .Replace("your Mac", "this PC", StringComparison.OrdinalIgnoreCase);
}
