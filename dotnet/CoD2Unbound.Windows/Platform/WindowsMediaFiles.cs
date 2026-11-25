using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Platform;

internal sealed partial class WindowsMediaFiles(string homePath) : IMediaFiles
{
    private readonly string home = homePath;

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

    internal FileStream OpenScreenshot(string name) => WindowsFiles.Open(home, Relative(MediaKind.Screenshot, name));

    internal async Task<ImageSource?> ReadScreenshotPreviewAsync(string name)
    {
        const int previewWidth = 1440;
        using var input = OpenScreenshot(name);
        if (Path.GetExtension(name).Equals(".tga", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = await Task.Run(() => TgaThumbnail.Read(input, previewWidth));
            return BitmapFromBgra(decoded.Bgra, decoded.Width, decoded.Height);
        }

        using var source = input.AsRandomAccessStream();
        var bitmap = new BitmapImage { DecodePixelWidth = previewWidth };
        await bitmap.SetSourceAsync(source);
        return bitmap.PixelWidth > 0 ? bitmap : null;
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
        string destination = WindowsFiles.Under(home, to);
        try { File.Move(path, destination, overwrite: false); }
        catch (IOException) when (kind == MediaKind.Demo &&
            !path.Equals(destination, StringComparison.OrdinalIgnoreCase) && Path.Exists(destination))
        {
            throw new MediaNameInUseException();
        }
    }

    internal string Recycle(MediaKind kind, string name, nint owner)
    {
        string relative = Relative(kind, name);
        string path = WindowsFiles.Under(home, relative);
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!);
        using var verified = WindowsFiles.Open(home, relative);
        return WindowsRecycleBin.Recycle(path, owner, singleFile: kind != MediaKind.Config);
    }

    internal void Restore(MediaKind kind, string name, string token, nint owner)
    {
        string path = WindowsFiles.Under(home, Relative(kind, name));
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!);
        WindowsRecycleBin.RestoreFile(token, path, owner);
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

internal sealed class MediaNameInUseException : IOException;
