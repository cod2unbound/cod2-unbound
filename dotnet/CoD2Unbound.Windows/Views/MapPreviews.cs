using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
        if (image.Tag is not PreviewRequest)
        {
            image.ActualThemeChanged -= OnPreviewThemeChanged;
            image.ActualThemeChanged += OnPreviewThemeChanged;
        }
        var request = new PreviewRequest(map);
        image.Tag = request;
        OnPreviewThemeChanged(image, null);
        var source = await GetAsync(map);
        if (ReferenceEquals(image.Tag, request) && source is not null)
        {
            request.HasArtwork = true;
            image.Source = source;
        }
    }

    internal static SvgImageSource Fallback(string map, bool light = false)
    {
        var region = MapCatalog.Scenery(map).ToString().ToLowerInvariant();
        return new(new Uri($"ms-appx:///Assets/scene-{region}-{(light ? "light" : "dark")}.svg"));
    }

    private static void OnPreviewThemeChanged(FrameworkElement sender, object? args)
    {
        if (sender is Image { Tag: PreviewRequest { HasArtwork: false } request } image)
            image.Source = Fallback(request.Map, image.ActualTheme == ElementTheme.Light);
    }

    private sealed record PreviewRequest(string Map)
    {
        internal bool HasArtwork { get; set; }
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
