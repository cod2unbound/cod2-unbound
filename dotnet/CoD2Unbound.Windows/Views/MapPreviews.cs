using CoD2Unbound.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class MapPreviews(Func<string, Task<MapPixels?>> read)
{
    private readonly Dictionary<string, Task<ImageSource?>> sources = new(StringComparer.Ordinal);

    internal async Task<ImageSource?> GetAsync(string map)
    {
        if (MapCatalog.Key(map) is not { } key)
            return null;
        if (!sources.TryGetValue(key, out var pending))
        {
            if (sources.Count >= 24)
                sources.Remove(sources.Keys.First());
            sources.Add(key, pending = ReadAsync(key));
        }
        return await pending;
    }

    internal Border Tile(string map, double width, double height)
    {
        var art = new Image { Stretch = Stretch.UniformToFill };
        _ = FillAsync(art, map);
        return new Border { Width = width, Height = height, Style = StyleFor("MediaTile"), Child = art };
    }

    internal async Task FillAsync(Image image, string map)
    {
        image.Tag = map;
        image.Source = null;
        var source = await GetAsync(map);
        if (Equals(image.Tag, map))
            image.Source = source;
    }

    private async Task<ImageSource?> ReadAsync(string map)
    {
        try { return await read(map) is { } pixels ? MapBitmap(pixels) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
