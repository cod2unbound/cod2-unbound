using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using CoD2Unbound.Core;
using Microsoft.Win32;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsGameDiscovery(WindowsEngineInventory inventory) : IGameDiscovery
{
    private const string RetailKey = @"SOFTWARE\Activision\Call of Duty 2";
    private const string SteamKey = @"SOFTWARE\Valve\Steam";
    private static readonly RegistryView[] Views = [RegistryView.Registry32, RegistryView.Registry64];
    private static readonly RegistryHive[] Hives = [RegistryHive.CurrentUser, RegistryHive.LocalMachine];

    public ValueTask<ImmutableArray<GameInstallation>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var copies = new List<GameInstallation>();
        var binaries = inventory.Installed;
        foreach (string steam in SteamRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSteam(steam);
        }
        foreach (var view in Views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var hive in Hives)
                Add(RegistryValue(hive, view, RetailKey, "InstallPath"), GameInstallSource.Disc);
        }

        foreach (string programs in ProgramDirectories())
            Add(Path.Join(programs, @"Activision\Call of Duty 2"), GameInstallSource.Disc);
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            Add(Path.Join(drive.Name, @"Games\Call of Duty 2"), GameInstallSource.Disc);
            Add(Path.Join(drive.Name, @"Activision\Call of Duty 2"), GameInstallSource.Disc);
            AddLibrary(Path.Join(drive.Name, "SteamLibrary"));
        }
        return ValueTask.FromResult<ImmutableArray<GameInstallation>>(
            [.. copies.DistinctBy(copy => copy.DataPath, StringComparer.OrdinalIgnoreCase)]);

        void Add(string? path, GameInstallSource source)
        {
            if (Normalize(path) is { } normalized)
                copies.Add(new(normalized, binaries, source));
        }

        void AddLibrary(string library)
        {
            var common = Path.Join(library, @"steamapps\common");
            try
            {
                var manifest = ReadSteamFile(library, "steamapps/appmanifest_2630.acf", 64 * 1024);
                if (SetupDiscovery.SteamInstallDirectory(manifest) is { } directory)
                    Add(Path.Join(common, directory), GameInstallSource.Steam);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException) { }
            Add(Path.Join(common, "Call of Duty 2"), GameInstallSource.Steam);
        }

        void AddSteam(string root)
        {
            AddLibrary(root);
            try
            {
                foreach (string library in SetupDiscovery.SteamLibraryPaths(ReadSteamFile(root, "steamapps/libraryfolders.vdf", 1024 * 1024)))
                    if (Normalize(library) is { } normalized)
                        AddLibrary(normalized);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException) { }
        }
    }

    internal static bool SteamInstalled() => SteamRoots().Any(root => File.Exists(Path.Join(root, "steam.exe")));

    private static IEnumerable<string> SteamRoots()
    {
        var roots = Hives.SelectMany(hive => Views.Select(view =>
                RegistryValue(hive, view, SteamKey, hive == RegistryHive.CurrentUser ? "SteamPath" : "InstallPath")))
            .Concat(ProgramDirectories().Select(path => Path.Join(path, "Steam")));
        return roots.Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ProgramDirectories() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
    }.Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    // Only called in response to the explicit action on the CD key step.
    internal static string? ReadDiscKey()
    {
        foreach (string virtualKey in new[]
        {
            @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\Activision\Call of Duty 2",
            @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\Activision\Call of Duty 2"
        })
            if (ValidKey(RegistryValue(RegistryHive.CurrentUser, RegistryView.Registry64, virtualKey, "codkey")) is { } key)
                return key;

        foreach (var view in Views)
            foreach (var hive in Hives)
                if (ValidKey(RegistryValue(hive, view, RetailKey, "codkey")) is { } key)
                    return key;
        return null;

        static string? ValidKey(string? value) => value is { Length: <= 32 } && CdKey.IsValid(value) ? CdKey.Normalize(value) : null;
    }

    internal async ValueTask<(WindowsProfileDiscovery Found, string[] Searched)> DiscoverProfilesAsync(
        string? gameData, CancellationToken cancellationToken)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var copies = await DiscoverAsync(cancellationToken);
        var folders = copies.Select(copy => copy.DataPath).Prepend(gameData).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var sources = folders.SelectMany(path => new[] { SetupDiscovery.VirtualStoreCopy(local, path), path })
            .OfType<string>().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return WindowsInstalledProfiles.Find(null, sources, cancellationToken);
    }

    private static string? RegistryValue(RegistryHive hive, RegistryView view, string key, string name)
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

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;
        try { return WindowsFiles.NormalizeFolder(path); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static string ReadSteamFile(string root, string relative, int maximum)
    {
        using var input = WindowsFiles.Open(root, relative);
        // Steam can replace this file while installing. Re-read it on the next poll.
        if (input.Length > maximum)
            return "";
        using var reader = new StreamReader(input, Encoding.UTF8);
        char[] buffer = new char[maximum + 1];
        int count = reader.ReadBlock(buffer);
        return count <= maximum ? new string(buffer, 0, count) : "";
    }
}
