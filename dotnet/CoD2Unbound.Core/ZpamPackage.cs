using System.Buffers;
using System.IO.Compression;

namespace CoD2Unbound.Core;

public enum ZpamPackageFailure { Checksum, MissingFiles, UnsafeEntry, Unpack }

public sealed class ZpamPackageException(ZpamPackageFailure failure, Exception? innerException = null) : IOException(failure switch
{
    ZpamPackageFailure.Checksum => "The zPAM download doesn’t match the checksum its release publishes, so it wasn’t kept.",
    ZpamPackageFailure.MissingFiles => "The zPAM download doesn’t hold the files a server needs.",
    ZpamPackageFailure.UnsafeEntry => "The zPAM download holds something that isn’t a plain file, so it wasn’t kept.",
    ZpamPackageFailure.Unpack => "The zPAM download couldn’t be unpacked.",
    _ => throw new ArgumentOutOfRangeException(nameof(failure))
}, innerException)
{
    public ZpamPackageFailure Failure { get; } = failure;
}

public interface IZpamPackageStorage
{
    // Start private staging for this verified release. The release version identifies one directory.
    ValueTask<IZpamPackageStage> BeginAsync(ZpamRelease release, CancellationToken cancellationToken);
}

public interface IZpamPackageStage : IAsyncDisposable
{
    // Exclusively create one validated basename. Dispose streams before publishing the whole staging directory.
    ValueTask<Stream> CreateAsync(string name, CancellationToken cancellationToken);
    ValueTask PublishAsync(CancellationToken cancellationToken);
    // Disposal cleans every unpublished temporary file, including after cancellation or an I/O failure.
}

public static class ZpamPackage
{
    private const long MaximumUnpackedBytes = 1L << 30;
    private const int MaximumFiles = 64;
    private const int MaximumTemplateBytes = 1 << 20;

    public static async ValueTask InstallVerifiedAsync(ZpamRelease release, Stream archive, IZpamPackageStorage storage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!release.HasVersionedAsset())
            throw new ArgumentException("The zPAM release has an invalid version or asset name.", nameof(release));
        if (!archive.CanSeek)
            throw new ArgumentException("Use a private seekable copy of the zPAM archive.", nameof(archive));
        archive.Position = 0;
        if (!release.VerifyArchive(archive, cancellationToken))
            throw new ZpamPackageException(ZpamPackageFailure.Checksum);
        archive.Position = 0;
        try
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            var selected = Select(zip);
            await using var stage = await storage.BeginAsync(release, cancellationToken).ConfigureAwait(false);
            foreach (var (name, entry) in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var output = await stage.CreateAsync(name, cancellationToken).ConfigureAwait(false);
                await using var input = entry.Open();
                await CopyEntryAsync(input, output, entry.Length, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await stage.PublishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException error) { throw new ZpamPackageException(ZpamPackageFailure.Unpack, error); }
    }

    private static Dictionary<string, ZipArchiveEntry> Select(ZipArchive zip)
    {
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        bool hasArchive = false;
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("main/", StringComparison.Ordinal))
                continue;
            string name = entry.FullName["main/".Length..];
            bool config = name == "server.cfg";
            bool archive = name.StartsWith("zpam", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".iwd", StringComparison.Ordinal) &&
                name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.');
            if (!config && !archive)
                continue;

            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & 0x410) != 0 || unixType is not (0 or 0x8000) || name.Length > 255 ||
                entry.Length is < 1 or > MaximumUnpackedBytes || (config && entry.Length > MaximumTemplateBytes) ||
                entry.Length > MaximumUnpackedBytes - bytes || files.Count >= MaximumFiles || !files.TryAdd(name, entry))
                throw new ZpamPackageException(ZpamPackageFailure.UnsafeEntry);

            bytes += entry.Length;
            hasArchive |= archive;
        }
        if (!files.ContainsKey("server.cfg") || !hasArchive)
            throw new ZpamPackageException(ZpamPackageFailure.MissingFiles);
        return files;
    }

    private static async ValueTask CopyEntryAsync(Stream input, Stream output, long length, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - total + 1)),
                cancellationToken).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > length)
                    throw new ZpamPackageException(ZpamPackageFailure.UnsafeEntry);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (total != length)
                throw new ZpamPackageException(ZpamPackageFailure.Unpack);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
