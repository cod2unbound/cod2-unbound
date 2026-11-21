using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using CoD2Unbound.Core;
using Microsoft.Win32;

namespace CoD2Unbound.Windows.Platform;

internal sealed partial class WindowsGameDiscovery : IGameDiscovery
{
    private const string RetailKey = @"SOFTWARE\Activision\Call of Duty 2";
    private const string SteamKey = @"SOFTWARE\Valve\Steam";

    public ValueTask<ImmutableArray<GameInstallation>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(paths, RegistryPath(RegistryHive.LocalMachine, view, RetailKey, "InstallPath"));
            Add(paths, RegistryPath(RegistryHive.CurrentUser, view, RetailKey, "InstallPath"));
            AddSteam(paths, RegistryPath(RegistryHive.CurrentUser, view, SteamKey, "SteamPath"));
            AddSteam(paths, RegistryPath(RegistryHive.LocalMachine, view, SteamKey, "InstallPath"));
        }

        return ValueTask.FromResult<ImmutableArray<GameInstallation>>(
            [.. paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new GameInstallation(path, []))]);
    }

    private static string? RegistryPath(RegistryHive hive, RegistryView view, string key, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var entry = root.OpenSubKey(key);
            return entry?.GetValue(name) as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void Add(List<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { paths.Add(WindowsFiles.NormalizeFolder(path)); }
        catch (Exception error) when (error is IOException or ArgumentException) { }
    }

    private static void AddSteam(List<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        const string game = @"steamapps\common\Call of Duty 2";
        Add(paths, Path.Join(path, game));
        try
        {
            // Steam's current libraryfolders.vdf records each library under a "path" key.
            using var input = WindowsFiles.Open(path, "steamapps/libraryfolders.vdf");
            if (input.Length > 1024 * 1024)
                return;
            using var reader = new StreamReader(input, Encoding.UTF8);
            char[] buffer = new char[1024 * 1024 + 1];
            int count = reader.ReadBlock(buffer);
            if (count == buffer.Length)
                return;
            foreach (Match match in LibraryPath().Matches(new string(buffer, 0, count)))
            {
                string library = match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal);
                Add(paths, Path.Join(library, game));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException) { }
    }

    [GeneratedRegex("""(?m)^\s*"path"\s+"([^"\r\n]+)"\s*$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex LibraryPath();
}
