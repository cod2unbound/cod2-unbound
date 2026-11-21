using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public enum MediaKind { Demo, Screenshot, Config }
public enum DemoPublishResult { Created, Identical, Occupied }
public sealed record MediaFile(string Name, DateTimeOffset? Modified, long? Bytes);

public sealed record MediaFacts(string? Map, string Stamp, string Size)
{
    public static MediaFacts From(MediaFile file, TimeZoneInfo timeZone) => new(MapHint(file.Name),
        file.Modified is { } date ? LauncherText.StencilTime(TimeZoneInfo.ConvertTime(date, timeZone)) : "",
        file.Bytes is { } bytes ? SizeLabel(bytes) : "");

    public static string? MapHint(string name) => Regex.Split(name.ToLowerInvariant(), @"[^\p{L}]+")
        .FirstOrDefault(MapCatalog.StockKeys.Contains);

    public static string SizeLabel(long bytes)
    {
        double value = Math.Max(0, bytes);
        return value switch
        {
            < 1_000 => FormattableString.Invariant($"{value:F0} B"),
            < 1_000_000 => FormattableString.Invariant($"{value / 1_000:F0} KB"),
            < 1_000_000_000 => FormattableString.Invariant($"{value / 1_000_000:F1} MB"),
            _ => FormattableString.Invariant($"{value / 1_000_000_000:F1} GB")
        };
    }
}

public interface IMediaFiles
{
    // Open the verified private media directories under the engine home without following reparse
    // points. List only direct regular children and exclude reparse points and alternate streams.
    ImmutableArray<MediaFile> List(MediaKind kind);
    // Validate the source regular-file handle, and compare or publish it at this basename as one
    // operation. Case-insensitive collisions use Occupied, including directories and reparse points.
    ValueTask<DemoPublishResult> PublishDemoAsync(Uri source, string fileName, CancellationToken cancellationToken);
}

public sealed class MediaLibrary(IMediaFiles files)
{
    public static string Folder(MediaKind kind) => kind switch
    {
        MediaKind.Demo => "main/demos",
        MediaKind.Screenshot => "main/screenshots",
        MediaKind.Config => "main",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public ImmutableArray<MediaFile> List(MediaKind kind, string? search = null) =>
        [.. files.List(kind).Where(file => Accepts(file.Name, kind) &&
            (string.IsNullOrWhiteSpace(search) || CultureInfo.InvariantCulture.CompareInfo.IndexOf(file.Name, search.Trim(),
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
            .OrderByDescending(file => file.Modified).ThenBy(file => file.Name, StringComparer.Ordinal)];

    public static bool Accepts(string name, MediaKind kind)
    {
        int dot = name.LastIndexOf('.');
        if (!MediaName.SafeComponent(name) || dot <= 0)
            return false;
        string extension = name[(dot + 1)..].ToLowerInvariant();
        return kind switch
        {
            MediaKind.Demo => extension == "dm_1",
            MediaKind.Screenshot => extension is "jpg" or "jpeg" or "png" or "tga",
            MediaKind.Config => extension == "cfg" && !ManagedConfig(name[..dot]),
            _ => false
        };
    }

    public static (string? FileName, string? Problem) Rename(MediaFile file, string value)
    {
        string name = value.Trim();
        if (name.StartsWith('.'))
            return (null, "Choose a name that doesn’t start with a dot, so the recording stays visible.");
        if (!MediaName.SafeComponent(name))
            return (null, "Enter a name without slashes or colons.");
        int extension = file.Name.LastIndexOf('.');
        if (extension <= 0 || !MediaName.SafeComponent(file.Name))
            return (null, "This recording couldn’t be renamed. Check that it’s still in the folder and try again.");
        string suffix = file.Name[extension..];
        if (suffix.Equals(".dm_1", StringComparison.OrdinalIgnoreCase) && !MediaName.Playable(name))
            return (null, "Use only letters, numbers, dots, underscores or hyphens, so the game can play it.");
        if (suffix.Equals(".cfg", StringComparison.OrdinalIgnoreCase) && ManagedConfig(name))
            return (null, "Choose a name that isn’t used by the launcher.");
        string target = name + suffix;
        return MediaName.SafeComponent(target) ? (target, null) : (null, "Enter a name without slashes or colons.");
    }

    private static bool ManagedConfig(string stem)
    {
        string name = stem.ToLowerInvariant();
        return name is "launcher" or "config_mp" ||
            (name.StartsWith("join_", StringComparison.Ordinal) && name.Length == 37 && name.AsSpan(5).IndexOfAnyExcept("0123456789abcdef") < 0) ||
            (name.StartsWith("cs_", StringComparison.Ordinal) && name.Length == 43 && name.AsSpan(3).IndexOfAnyExcept("0123456789abcdef") < 0);
    }

    public async ValueTask<string> PrepareDemoAsync(Uri source, string fileName, CancellationToken cancellationToken = default)
    {
        if (!source.IsAbsoluteUri || !source.IsFile || !Accepts(fileName, MediaKind.Demo))
            throw new InvalidDataException(MediaName.PlaybackProblem);
        string stem = MediaName.Alias(fileName[..fileName.LastIndexOf('.')]);
        for (int index = 1; index <= 10_000; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string alias = index == 1 ? stem : FormattableString.Invariant($"{stem}-{index}");
            if (await files.PublishDemoAsync(source, alias + ".dm_1", cancellationToken).ConfigureAwait(false)
                is DemoPublishResult.Created or DemoPublishResult.Identical)
                return alias;
        }
        throw new IOException("A recording already has that name. Choose another.");
    }
}

public static class MediaName
{
    public const string PlaybackProblem = "Rename this demo using letters, numbers, dots, underscores or hyphens before playback.";
    public static bool Playable(string name) => name.Length > 0 && name.All(IsEngineCharacter);

    public static string Alias(string name)
    {
        var result = new StringBuilder();
        foreach (var rune in name.EnumerateRunes().Take(230))
            result.Append(rune.IsAscii && IsEngineCharacter((char)rune.Value) ? (char)rune.Value : '_');
        string alias = result.ToString();
        return alias.Length == 0 || alias.StartsWith('.') || IsDevice(alias) ? "demo" + alias : alias;
    }

    internal static bool SafeComponent(string name) =>
        name.Length is > 0 and <= 255 && name is not ("." or "..") && !name.EndsWith(' ') && !name.EndsWith('.') &&
        !name.Any(character => char.IsControl(character) || "/\\:*?\"<>|".Contains(character)) && !IsDevice(name);

    private static bool IsEngineCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-';

    private static bool IsDevice(string name)
    {
        int dot = name.IndexOf('.');
        string stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                stem[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }
}
