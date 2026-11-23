using System.Collections.Immutable;
using System.Globalization;

namespace CoD2Unbound.Core;

public readonly record struct WindowsDisplayMode(DisplayResolution Size, int Hz);

public sealed record WindowsDisplay(string Id, string Name, string Adapter, int Monitor, DisplayResolution? Native,
    DisplayResolution Current, ImmutableArray<WindowsDisplayMode> Modes);

public static class WindowsDisplays
{
    public const int MaxMonitor = 8; // r_monitor's engine registration.

    public static ImmutableArray<WindowsDisplayMode> Offered(IEnumerable<WindowsDisplayMode> modes) =>
        [.. modes.Where(mode => mode.Size.Width is >= 640 and <= 32768 && mode.Size.Height is >= 480 and <= 32768)
            .GroupBy(mode => mode.Size).Select(group => group.MaxBy(mode => mode.Hz)).OrderBy(mode => mode.Size)];

    public static DisplayResolution? ForLaunch(string requested, ImmutableArray<WindowsDisplayMode> modes)
    {
        if (!DisplayResolution.TryParse(requested, out var size))
            return null;
        return modes.Select(mode => mode.Size).Where(mode => mode.Fits(size))
            .OrderByDescending(mode => (long)mode.Width * mode.Height).ThenByDescending(mode => mode.Width)
            .Select(mode => (DisplayResolution?)mode).FirstOrDefault();
    }

    public static DisplayResolution? Adjustment(string requested, WindowsDisplay? display) =>
        display is not null && DisplayResolution.TryParse(requested, out var size) &&
        ForLaunch(requested, display.Modes) is { } fitting && fitting != size ? fitting : null;

    public static WindowsDisplay? Selected(ImmutableArray<WindowsDisplay> displays, string? preferred) =>
        displays.FirstOrDefault(display => display.Id.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ??
        displays.FirstOrDefault();

    public static WindowsDisplay? ForLoadout(ImmutableArray<WindowsDisplay> displays, string? preferred, string? monitor) =>
        int.TryParse(monitor, NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) && ordinal is > 0 and <= MaxMonitor
            ? displays.FirstOrDefault(display => display.Monitor == ordinal) ?? displays.FirstOrDefault()
            : Selected(displays, preferred);

    public static string ModeName(string mode) => mode switch
    {
        "windowed" => "Window",
        "exclusive" => "Full screen",
        _ => "Borderless"
    };

    public static string GraphicsName(WindowsRenderMode renderer) =>
        renderer == WindowsRenderMode.DirectX7 ? "DirectX 7" : "Direct3D 9";
}
