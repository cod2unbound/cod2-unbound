using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsConfigImportFiles : IConfigImportFiles
{
    public async ValueTask<ConfigImportLocalFile> ResolveFileAsync(Uri file, CancellationToken cancellationToken)
    {
        Uri root = await ResolveFolderAsync(new Uri(Path.GetDirectoryName(file.LocalPath)!), cancellationToken);
        return new(root, Path.GetFileName(file.LocalPath));
    }

    public ValueTask<Uri> ResolveFolderAsync(Uri folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var directory = new WindowsFiles.DirectoryHandles(folder.LocalPath);
        return ValueTask.FromResult(folder);
    }

    public async ValueTask<ImmutableArray<byte>> ReadAsync(Uri root, string relativePath, int maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var file = WindowsFiles.Open(root.LocalPath, relativePath, FileShare.Read);
            if (file.Length > maximumBytes)
                throw new ConfigImportException(ConfigImportFailure.TooLarge);
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return ImmutableCollectionsMarshal.AsImmutableArray(bytes);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ConfigImportException(ConfigImportFailure.MissingEntry, innerException: error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ConfigImportException(ConfigImportFailure.OutsideRoot, innerException: error);
        }
    }
}
