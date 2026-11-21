using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsMediaFiles(string home) : IMediaFiles
{
    public ImmutableArray<MediaFile> List(MediaKind kind)
    {
        string folder = WindowsFiles.Under(home, MediaLibrary.Folder(kind));
        try
        {
            using var verified = new WindowsFiles.DirectoryHandles(folder);
            return [.. new DirectoryInfo(folder).EnumerateFiles()
                .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0 && MediaLibrary.Accepts(file.Name, kind))
                .Select(file => new MediaFile(file.Name, file.LastWriteTimeUtc, file.Length))];
        }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) { return []; }
    }

    public async ValueTask<DemoPublishResult> PublishDemoAsync(Uri source, string fileName, CancellationToken cancellationToken)
    {
        string folder = WindowsFiles.Under(home, MediaLibrary.Folder(MediaKind.Demo));
        using var destination = new WindowsFiles.DirectoryHandles(folder, create: true);
        using var input = WindowsFiles.Open(Path.GetDirectoryName(source.LocalPath)!, Path.GetFileName(source.LocalPath));
        string target = WindowsFiles.Under(folder, fileName);
        if (await ExistingAsync() is { } existing)
            return existing;

        string temporary = Path.Join(folder, $".launcher-{Guid.NewGuid():N}.tmp");
        bool ownsTemporary = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, target);
                ownsTemporary = false;
                return DemoPublishResult.Created;
            }
            catch (IOException) when (Path.Exists(target))
            {
                return await ExistingAsync() ?? DemoPublishResult.Occupied;
            }
        }
        finally
        {
            if (ownsTemporary)
                File.Delete(temporary);
        }

        async ValueTask<DemoPublishResult?> ExistingAsync()
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(target); }
            catch (FileNotFoundException) { return null; }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return DemoPublishResult.Occupied;
            using var existing = WindowsFiles.Open(folder, fileName);
            if (existing.Length != input.Length)
                return DemoPublishResult.Occupied;
            input.Position = 0;
            var left = new byte[64 * 1024];
            var right = new byte[left.Length];
            while (true)
            {
                int read = await input.ReadAtLeastAsync(left, left.Length, false, cancellationToken).ConfigureAwait(false);
                int other = await existing.ReadAtLeastAsync(right, right.Length, false, cancellationToken).ConfigureAwait(false);
                if (read != other || !left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, other)))
                    return DemoPublishResult.Occupied;
                if (read < left.Length)
                    return DemoPublishResult.Identical;
            }
        }
    }

    internal string Folder(MediaKind kind)
    {
        string folder = WindowsFiles.Under(home, MediaLibrary.Folder(kind));
        using var verified = new WindowsFiles.DirectoryHandles(folder, create: true);
        return folder;
    }

    internal string Existing(MediaKind kind, string name)
    {
        string relative = Relative(kind, name);
        using var verified = WindowsFiles.Open(home, relative);
        return WindowsFiles.Under(home, relative);
    }

    internal void Rename(MediaKind kind, MediaFile file, string target)
    {
        string from = Relative(kind, file.Name);
        string to = Relative(kind, target);
        string path = WindowsFiles.Under(home, from);
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!);
        using var verified = WindowsFiles.Open(home, from);
        File.Move(path, WindowsFiles.Under(home, to), overwrite: false);
    }

    internal void Recycle(MediaKind kind, string name, nint owner)
    {
        string relative = Relative(kind, name);
        string path = WindowsFiles.Under(home, relative);
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!);
        using var verified = WindowsFiles.Open(home, relative);
        WindowsRecycleBin.Recycle(path, owner);
    }

    internal void Reveal(MediaKind kind, string name)
    {
        string path = Existing(kind, name);
        try { using var process = Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Win32Exception error) { throw new IOException(error.Message, error); }
    }

    private static string Relative(MediaKind kind, string name)
    {
        if (!MediaLibrary.Accepts(name, kind))
            throw new IOException("This file is unavailable.");
        return MediaLibrary.Folder(kind) + "/" + name;
    }
}

internal static class WindowsRecycleBin
{
    private static readonly Guid FileOperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    internal static void Recycle(string path, nint owner)
    {
        const uint flags = 0x00080000 | 0x00000400 | 0x00000040 | 0x00000010 | 0x00000004;
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClass, throwOnError: true)!)!;
        object? item = null;
        try
        {
            int result = SHCreateItemFromParsingName(path, 0, ShellItemInterface, out item);
            Marshal.ThrowExceptionForHR(result);
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(flags);
            operation.DeleteItem(item!, 0);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted || File.Exists(path))
                throw new IOException("Windows couldn’t move this file to the Recycle Bin.");
        }
        finally
        {
            if (item is not null)
                Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint binding, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object? item);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    private interface IFileOperation
    {
        void Advise();
        void Unadvise();
        void SetOperationFlags(uint flags);
        void SetProgressMessage();
        void SetProgressDialog();
        void SetProperties();
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem();
        void ApplyPropertiesToItems();
        void RenameItem();
        void RenameItems();
        void MoveItem();
        void MoveItems();
        void CopyItem();
        void CopyItems();
        void DeleteItem([MarshalAs(UnmanagedType.Interface)] object item, nint progressSink);
        void DeleteItems();
        void NewItem();
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
