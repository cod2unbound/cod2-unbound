using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CoD2Unbound.Core;
using Microsoft.Win32.SafeHandles;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsFiles : IGameFiles
{
    internal const int MaximumStudioFileBytes = 16 * 1024 * 1024;

    public bool IsRegularFile(string root, string relativePath)
    {
        try
        {
            using var file = Open(root, relativePath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static FileStream Open(string root, string relative, FileShare share = FileShare.ReadWrite | FileShare.Delete)
    {
        string path = Under(root, relative);
        using var parent = new DirectoryHandles(Path.GetDirectoryName(path)!);
        var handle = OpenHandle(path, directory: false, share);
        try
        {
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static string Under(string root, string relative)
    {
        root = NormalizeFolder(root);
        if (relative.Split('/', '\\').Any(part => part.Length == 0 || part is "." or ".." ||
                part.Contains(':') || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new IOException("The file name is outside the selected folder.");
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static string NormalizeFolder(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] is not ('\\' or '/') || path[2..].Contains(':'))
            throw new IOException("Choose a folder on a local drive.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    // Hold each parent without FILE_SHARE_DELETE until the final file has been opened or published.
    internal sealed class DirectoryHandles : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];

        internal DirectoryHandles(string folder, bool create = false)
        {
            string fullFolder = NormalizeFolder(folder);
            string root = Path.GetPathRoot(fullFolder)!;
            string current = root;
            try
            {
                handles.Add(OpenHandle(current, directory: true));
                foreach (string name in fullFolder[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Join(current, name);
                    if (create)
                        Directory.CreateDirectory(current);
                    handles.Add(OpenHandle(current, directory: true));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            for (int index = handles.Count - 1; index >= 0; index--)
                handles[index].Dispose();
        }
    }

    internal static FileStream OpenExclusiveLock(string path)
    {
        var handle = CreateFile(@"\\?\" + path, 0xC0000000, FileShare.None, 0, 4, 0x00200000, 0);
        try
        {
            if (handle.IsInvalid)
                throw FileError(Marshal.GetLastPInvokeError());
            if (!GetFileInformationByHandleEx(handle, 9, out var info, 8))
                throw FileError(Marshal.GetLastPInvokeError());
            if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("The launcher lock is not a regular file.");
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenHandle(string path, bool directory, FileShare share = FileShare.ReadWrite | FileShare.Delete)
    {
        const uint genericRead = 0x80000000;
        const uint readAttributes = 0x80;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        const uint backupSemantics = 0x02000000;
        const int fileAttributeTagInfo = 9;

        var file = CreateFile(@"\\?\" + path, directory ? readAttributes : genericRead,
            directory ? FileShare.ReadWrite : share,
            0, openExisting, openReparsePoint | backupSemantics, 0);
        try
        {
            if (file.IsInvalid)
                throw FileError(Marshal.GetLastPInvokeError());
            if (!GetFileInformationByHandleEx(file, fileAttributeTagInfo, out var info, 8))
                throw FileError(Marshal.GetLastPInvokeError());
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                info.Attributes.HasFlag(FileAttributes.Directory) != directory)
                throw new IOException("The selected path contains a link or an unexpected file type.");
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static Exception FileError(int error) => error switch
    {
        2 => new FileNotFoundException(),
        3 => new DirectoryNotFoundException(),
        5 => new UnauthorizedAccessException(),
        _ => new IOException("Windows could not open a file in the selected folder.", unchecked((int)0x80070000) | error)
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        internal FileAttributes Attributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTagInfo info, uint size);
}

internal sealed class WindowsSettingsFolder(string root, bool allowNestedWrites = false) : ISettingsFolder
{
    public string Root { get; } = WindowsFiles.NormalizeFolder(root);

    public async ValueTask<ImmutableArray<byte>?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        int maxBytes = name == ConfigStudioSession.ArchivePath ? WindowsFiles.MaximumStudioFileBytes : 1024 * 1024;
        try
        {
            using var input = WindowsFiles.Open(Root, name);
            long length = input.Length;
            if (length > maxBytes)
                throw new IOException("A launcher settings file is too large.");
            byte[] bytes = new byte[(int)length];
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (input.ReadByte() != -1)
                throw new IOException("A launcher settings file changed while being read.");
            return [.. bytes];
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async ValueTask WriteAsync(string name, ImmutableArray<byte> data, CancellationToken cancellationToken)
    {
        string path = FileSlot(name);
        string directory = Path.GetDirectoryName(path)!;
        using var parent = new WindowsFiles.DirectoryHandles(directory, create: true);
        CheckExisting(name);
        string temporary = Path.Join(directory, $".launcher-{Guid.NewGuid():N}.tmp");
        bool ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                await stream.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
                File.Delete(temporary);
        }
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = FileSlot(name);
        try
        {
            using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!);
            CheckExisting(name);
            File.Delete(path);
        }
        catch (DirectoryNotFoundException) { }
        return ValueTask.CompletedTask;
    }

    private string FileSlot(string name)
    {
        if (!allowNestedWrites && (name.Contains('/') || name.Contains('\\')))
            throw new IOException("Only top-level launcher settings can be written here.");
        return WindowsFiles.Under(Root, name);
    }

    private void CheckExisting(string name)
    {
        try
        {
            using var existing = WindowsFiles.Open(Root, name);
        }
        catch (FileNotFoundException) { }
    }
}
