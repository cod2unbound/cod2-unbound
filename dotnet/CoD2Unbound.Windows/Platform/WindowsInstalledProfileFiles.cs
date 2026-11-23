using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsInstalledProfileFiles : IWindowsProfileFiles
{
    private readonly WindowsFiles files = new();

    public bool IsDirectory(string root, string path)
    {
        try
        {
            using var directory = new WindowsFiles.DirectoryHandles(Contained(root, path));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool IsRegularFile(string root, string path) =>
        files.IsRegularFile(root, Path.GetRelativePath(root, Contained(root, path)));

    public IEnumerable<string> EnumerateNames(string root, string path)
    {
        string folder = Contained(root, path);
        using var directory = new WindowsFiles.DirectoryHandles(folder);
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            yield return Path.GetFileName(entry);
    }

    public byte[]? ReadPrefix(string root, string path, int maximumBytes)
    {
        try
        {
            using var input = WindowsFiles.Open(root, Path.GetRelativePath(root, Contained(root, path)));
            var buffer = new byte[maximumBytes];
            return buffer[..input.ReadAtLeast(buffer, maximumBytes, throwOnEndOfStream: false)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Contained(string root, string path) => string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
        ? WindowsFiles.NormalizeFolder(root)
        : WindowsFiles.Under(root, Path.GetRelativePath(root, path).Replace('\\', '/'));
}
