using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public static partial class SetupDiscovery
{
    public static ImmutableArray<GameInstallation> Complete(IGameFiles files, string? preferred,
        IEnumerable<GameInstallation> installations) =>
        [.. installations.Where(copy => GameSettings.SafeEngineText(copy.DataPath) && GameArchives.Survey(files, copy.DataPath).IsComplete)
            .DistinctBy(copy => copy.DataPath, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(copy => string.Equals(copy.DataPath, preferred, StringComparison.OrdinalIgnoreCase))];

    public static async Task<GameInstallation> WaitForSteamAsync(IGameFiles files,
        Func<CancellationToken, ValueTask<ImmutableArray<GameInstallation>>> discover,
        TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copies = await discover(cancellationToken).ConfigureAwait(false);
            if (Complete(files, null, copies.Where(copy => copy.Source == GameInstallSource.Steam)).FirstOrDefault() is { } ready)
                return ready;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    public static IEnumerable<string> SteamLibraryPaths(string vdf) =>
        LibraryPath().Matches(vdf).Select(match => match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal));

    public static string? SteamInstallDirectory(string manifest)
    {
        string directory = InstallDirectory().Match(manifest).Groups[1].Value;
        return MediaName.SafeComponent(directory) ? directory : null;
    }

    public static string? VirtualStoreCopy(string localAppData, string install)
    {
        // VirtualStore mirrors the path below a local drive root, without the drive letter.
        if (localAppData.Length == 0 || install.Length < 4 || !char.IsAsciiLetter(install[0]) ||
            install[1] != ':' || install[2] != '\\')
            return null;
        return localAppData.TrimEnd('\\') + @"\VirtualStore\" + install[3..];
    }

    [GeneratedRegex("""(?m)^\s*"(?:path|[0-9]+)"\s+"([^"\r\n]+)"[ \t]*\r?$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex LibraryPath();

    [GeneratedRegex("""(?m)^\s*"installdir"\s+"([^"\r\n]+)"[ \t]*\r?$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex InstallDirectory();
}
