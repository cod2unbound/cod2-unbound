using System.Collections.Immutable;
using System.Globalization;

namespace CoD2Unbound.Core;

public enum JoinCheck { Go, NeedsCoD2x, LookUp }

public static class GameCapabilities
{
    public static JoinCheck CheckJoin(GameEdition edition, GameServer? server, bool lookedUp) => edition switch
    {
        GameEdition.Cod2x => JoinCheck.Go,
        GameEdition.Original when server is not null => server.IsCod2x ? JoinCheck.NeedsCoD2x : JoinCheck.Go,
        GameEdition.Original => lookedUp ? JoinCheck.Go : JoinCheck.LookUp,
        _ => throw new ArgumentOutOfRangeException(nameof(edition))
    };

    public static ImmutableHashSet<GameEdition> InstalledEditions(GameInstallation installation, EngineRole role) =>
        installation.Binaries.Where(binary => binary.Role == role).Select(binary => binary.Edition).ToImmutableHashSet();
}

public readonly record struct DisplayResolution(int Width, int Height) : IComparable<DisplayResolution>
{
    public static bool TryParse(string value, out DisplayResolution result)
    {
        result = default;
        int separator = value.IndexOf('x');
        if (separator < 0 || !int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int height) ||
            width is < 640 or > 32768 || height is < 480 or > 32768 || value != FormattableString.Invariant($"{width}x{height}"))
            return false;
        result = new(width, height);
        return true;
    }

    public bool Fits(DisplayResolution other) => Width <= other.Width && Height <= other.Height;
    public int CompareTo(DisplayResolution other) => Width == other.Width ? Height.CompareTo(other.Height) : Width.CompareTo(other.Width);
    public override string ToString() => FormattableString.Invariant($"{Width}x{Height}");
}

public static class DisplayResolutions
{
    private static readonly DisplayResolution[] Standard =
    [
        new(640, 480), new(800, 600), new(1024, 768), new(1280, 720), new(1920, 1080), new(2560, 1440),
        new(3008, 1692), new(3840, 2160), new(5120, 2880), new(6016, 3384)
    ];

    public static ImmutableArray<DisplayResolution> Offered(DisplayResolution native, IEnumerable<DisplayResolution> modes) =>
        [.. Standard.Concat(modes).Append(native).Where(size => size.Width >= 640 && size.Height >= 480 && size.Fits(native)).Distinct().Order()];

    public static DisplayResolution Fitting(string saved, ImmutableArray<DisplayResolution> sizes)
    {
        var fallback = sizes.IsEmpty ? new DisplayResolution(640, 480) : sizes[0];
        if (!DisplayResolution.TryParse(saved, out var requested))
            return fallback;
        return sizes.LastOrDefault(size => size.Fits(requested), fallback);
    }

    public static DisplayResolution Initial(DisplayResolution native) =>
        native.Fits(new(3840, 2160)) ? native : new(native.Width / 2, native.Height / 2);
}

public static class MouseDistance
{
    public static double? Centimetres(double dpi, double sensitivity, double yaw = 0.022) =>
        double.IsFinite(dpi) && double.IsFinite(sensitivity) && double.IsFinite(yaw) && dpi > 0 && sensitivity > 0 && yaw > 0
            ? 360 / (yaw * sensitivity) / dpi * 2.54
            : null;
}
