using System.Collections.Immutable;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsConfigProfiles(string home, nint owner, FileStream? heldLease = null) : IConfigProfileFiles, IConfigProfileTrash
{
    private readonly WindowsSettingsFolder storage = new(home, allowNestedWrites: true);

    public IDisposable Acquire(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (heldLease is null)
            return WindowsGameSessionHost.OpenLeaseAsync(home, "main/players/.studio.lock", cancellationToken)
                .AsTask().GetAwaiter().GetResult();
        ObjectDisposedException.ThrowIf(heldLease.SafeFileHandle.IsClosed, heldLease);
        return BorrowedLease.Instance;
    }

    private sealed class BorrowedLease : IDisposable
    {
        internal static BorrowedLease Instance { get; } = new();
        public void Dispose() { }
    }

    public ImmutableArray<byte>? Read(string path) => ReadBounded(home, path, 16 * 1024 * 1024);

    internal static ImmutableArray<byte>? ReadBounded(string root, string path, int maximum, bool allowOversized = false)
    {
        try
        {
            using var input = WindowsFiles.Open(root, path);
            if (input.Length > maximum && !allowOversized)
                throw new IOException("The launcher file is too large.");
            var bytes = new byte[(int)Math.Min(input.Length, (long)maximum + 1)];
            input.ReadExactly(bytes);
            if (!allowOversized && input.ReadByte() != -1)
                throw new IOException("The launcher file changed while being read.");
            return [.. bytes];
        }
        catch (Exception error) when (Missing(error)) { return null; }
    }

    public bool DirectoryExists(string path)
    {
        try
        {
            using var handles = new WindowsFiles.DirectoryHandles(Full(path));
            return true;
        }
        catch (Exception error) when (Missing(error)) { return false; }
    }

    public ImmutableArray<string> ListDirectories(string path) => List(path, directories: true);
    public ImmutableArray<string> ListFiles(string path) => List(path, directories: false);

    public void WriteAtomic(string path, ImmutableArray<byte> data) =>
        storage.WriteAsync(path, data, default).AsTask().GetAwaiter().GetResult();

    public void DeleteFile(string path) => storage.DeleteAsync(path, default).GetAwaiter().GetResult();

    public void PublishDirectory(string staging, string target) => MoveDirectory(staging, target);

    public void DeleteOwnedDirectory(string path)
    {
        string full = Full(path);
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(full)!);
        if (!DirectoryExists(path))
            return;
        // .NET's recursive Directory.Delete treats linked directories as leaf entries.
        Directory.Delete(full, recursive: true);
    }

    public ConfigProfileTrashHandle Move(string path)
    {
        string source = Full(path);
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(source)!);
        using (new WindowsFiles.DirectoryHandles(source)) { }
        return new(WindowsRecycleBin.Recycle(source, owner));
    }

    public void Restore(ConfigProfileTrashHandle handle, string target)
    {
        string destination = Full(target);
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(destination)!);
        WindowsRecycleBin.Restore(handle.Token, destination, owner);
    }

    private void MoveDirectory(string from, string to)
    {
        string source = Full(from), target = Full(to);
        using var sourceParent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(source)!);
        using var targetParent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(target)!);
        using (new WindowsFiles.DirectoryHandles(source)) { }
        Directory.Move(source, target);
    }

    private ImmutableArray<string> List(string path, bool directories)
    {
        string full = Full(path);
        try
        {
            using var handles = new WindowsFiles.DirectoryHandles(full);
            return [.. new DirectoryInfo(full).EnumerateFileSystemInfos().Where(entry =>
                (entry.Attributes & FileAttributes.ReparsePoint) == 0 &&
                entry.Attributes.HasFlag(FileAttributes.Directory) == directories).Select(entry => entry.Name)];
        }
        catch (Exception error) when (Missing(error)) { return []; }
    }

    private string Full(string path) => WindowsFiles.Under(home, path);
    private static bool Missing(Exception error) => error is FileNotFoundException or DirectoryNotFoundException;
}
