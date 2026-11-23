using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsInstalledProfiles : IWindowsProfileFiles
{
    internal static readonly WindowsInstalledProfiles Files = new();

    internal static (WindowsProfileDiscovery Found, string[] Searched) Find(
        string? gameData, string? chosen, CancellationToken cancellationToken)
    {
        string[] drives = chosen is not null ? [] : DriveInfo.GetDrives()
            .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
            .Select(drive => drive.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] extra = chosen is not null ? [chosen] :
            [.. new[] { gameData }.OfType<string>().Where(path => !string.IsNullOrWhiteSpace(path))
                .Concat(WindowsGameDiscovery.ProfileInstallPaths(cancellationToken)).Distinct(StringComparer.OrdinalIgnoreCase)];
        var result = WindowsProfiles.Find(Files, drives, extra,
            localAppData: chosen is null ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : null,
            cancellationToken: cancellationToken);
        return (result, [.. drives.Take(32).Select(drive => drive.TrimEnd('\\')), .. (chosen is null ? [] : new[] { chosen })]);
    }

    public bool IsDirectory(string root, string path)
    {
        try
        {
            string folder = Contained(root, path);
            if ((File.GetAttributes(folder) & FileAttributes.Directory) == 0)
                return false;
            using var handle = new WindowsFiles.DirectoryHandles(folder);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    public bool IsRegularFile(string root, string path)
    {
        try
        {
            if ((File.GetAttributes(Contained(root, path)) & FileAttributes.Directory) != 0)
                return false;
            using var file = WindowsFiles.Open(root + "/", Relative(root, path));
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    public IEnumerable<string> EnumerateNames(string root, string path)
    {
        string folder = Contained(root, path);
        using var handle = new WindowsFiles.DirectoryHandles(folder);
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            yield return entry.Name;
    }

    public byte[]? ReadPrefix(string root, string path, int maximumBytes)
    {
        string relative = Relative(root, path);
        if (!Path.GetFileName(relative).Equals("active.txt", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only the active profile marker can be read during discovery.");
        return WindowsConfigProfiles.ReadBounded(root + "/", relative, maximumBytes - 1, allowOversized: true)?.ToArray();
    }

    private static string Contained(string root, string path) => root.Equals(path, StringComparison.OrdinalIgnoreCase)
        ? WindowsFiles.NormalizeFolder(root + "/") : WindowsFiles.Under(root + "/", Relative(root, path));

    private static string Relative(string root, string path) =>
        path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? path[(root.Length + 1)..] : throw new IOException("The profile path is outside the chosen folder.");
}
