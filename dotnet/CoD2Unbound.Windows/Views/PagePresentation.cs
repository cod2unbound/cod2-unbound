using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    internal static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons)
            panel.Children.Add(button);
        return panel;
    }

    internal static SelectorBar Tabs(string[] labels, int selection, Action<int> change)
    {
        var bar = new SelectorBar();
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
