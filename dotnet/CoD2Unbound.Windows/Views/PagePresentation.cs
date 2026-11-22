using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using System.ComponentModel;
using System.Diagnostics;

namespace CoD2Unbound.Windows.Views;

internal static class PagePresentation
{
    internal static Visibility IsVisible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    internal static Style StyleFor(string key) => (Style)Application.Current.Resources[key];

    internal static TextBlock Caption(string text) =>
        new() { Text = text, Style = StyleFor("SecondaryText"), FontSize = 12 };

    internal static TextBlock Body(string text) => new() { Text = text, Style = StyleFor("SecondaryText") };

    internal static Grid Columns(double spacing, params GridLength[] widths)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }

    internal static void Put(Grid grid, int column, FrameworkElement element)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    internal static TextBlock MonoCaption(string text) => new() { Text = text, Style = StyleFor("MonoCaption") };
    internal static FontIcon Glyph(string glyph, double size) => new() { Glyph = glyph, FontSize = size };

    internal static Brush ThemeBrush(string key, ElementTheme theme, bool contrast)
    {
        string themeKey = contrast ? "HighContrast" : theme == ElementTheme.Light ? "Light" : "Dark";
        foreach (var resource in Application.Current.Resources.MergedDictionaries)
        {
            if (resource.ThemeDictionaries.TryGetValue(themeKey, out object? entry) &&
                entry is ResourceDictionary dictionary && dictionary.TryGetValue(key, out object? brush))
                return (Brush)brush!;
        }
        throw new InvalidOperationException($"Missing launcher theme brush {key}.");
    }

    // In contrast mode the native row presenter supplies both the normal and selected ink.
    internal static void SetRowTextBrush(TextBlock text, string key, bool contrast)
    {
        if (contrast)
            text.ClearValue(TextBlock.ForegroundProperty);
        else
            text.Foreground = ThemeBrush(key, text.ActualTheme, false);
    }

    internal static void AddGameName(TextBlock text, string name, bool contrast)
    {
        foreach (var color in QuakeColors.Runs(name))
        {
            var run = new Run { Text = color.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            string? key = color.Code switch
            {
                0 => "TertiaryBrush", 1 => "GameRedBrush", 2 => "GameGreenBrush", 3 => "GameYellowBrush",
                4 => "GameBlueBrush", 5 => "GameCyanBrush", 6 => "GameVioletBrush", _ => null
            };
            if (key is not null && !contrast)
                run.Foreground = ThemeBrush(key, text.ActualTheme, contrast);
            text.Inlines.Add(run);
        }
    }

    internal static StackPanel EmptyState(string glyph, string title, string detail)
    {
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(new FontIcon { Glyph = glyph, Style = StyleFor("EmptyIcon") });
        panel.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Style = StyleFor("EmptyHeading") });
        if (!string.IsNullOrEmpty(detail))
            panel.Children.Add(Body(detail));
        return panel;
    }

    internal static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons)
            panel.Children.Add(button);
        return panel;
    }

    internal static SelectorBar Tabs(string[] labels, int selection, Action<int> change)
    {
        var bar = new SelectorBar { Style = StyleFor("AlignedSelector") };
        foreach (string label in labels)
            bar.Items.Add(new SelectorBarItem { Text = label });
        bar.SelectedItem = bar.Items[selection];
        bar.SelectionChanged += (_, _) =>
        {
            int index = bar.Items.IndexOf(bar.SelectedItem);
            if (index >= 0)
                change(index);
        };
        return bar;
    }

    internal static Border SettingRow(string label, string? detail, FrameworkElement? action = null)
    {
        var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
            lines.Children.Add(Caption(detail));
        var layout = new Grid { MinHeight = 34, ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(lines);
        if (action is not null)
        {
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(action, 1);
            action.VerticalAlignment = VerticalAlignment.Center;
            layout.Children.Add(action);
        }
        return new Border { Style = StyleFor("FieldCard"), Child = layout };
    }

    internal static async Task<SoftwareBitmapSource> MapBitmapAsync(MapPixels art)
    {
        var bgra = art.Rgba.ToArray();
        for (int i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            bgra[i + 3] = 255;
        }
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(bgra),
            BitmapPixelFormat.Bgra8, art.Width, art.Height, BitmapAlphaMode.Premultiplied);
        var image = new SoftwareBitmapSource();
        await image.SetBitmapAsync(bitmap);
        return image;
    }

    internal static Task OpenAsync(string target)
    {
        try { using var shell = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Win32Exception error) { throw new IOException(error.Message, error); }
        return Task.CompletedTask;
    }

    internal static string DataSummary(SetupDataChoice choice) => choice.State switch
    {
        SetupDataState.Found => "Game data found",
        SetupDataState.Damaged or SetupDataState.UnfinishedSteam => "Game data is incomplete",
        _ => "Game data not found"
    };
}
