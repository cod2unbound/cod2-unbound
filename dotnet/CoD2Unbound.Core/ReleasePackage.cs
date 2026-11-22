using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public sealed record ReleaseOffer(string Version, ReleaseAsset Archive, Uri Checksums);
public enum UpdateBlock { GameRunning, ServersRunning }

public static partial class ReleasePackage
{
    private const long ArchiveLimit = 1L << 30;
    private const int SumsLimit = 64 * 1024;
    private static readonly string[] DownloadRoots =
    [
        "https://github.com/cod2unbound/cod2-unbound/releases/download/",
        "https://github.com/cod2unbound/cod2-silicon/releases/download/",
        "https://github.com/STiXzoOR/cod2-silicon/releases/download/"
    ];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ReleaseOffer? Offer(string version, string archiveName, IEnumerable<ReleaseAsset> assets)
    {
        if (!ReleaseVersion().IsMatch(version))
            return null;
        var list = assets.ToLookup(asset => asset.Name, StringComparer.Ordinal);
        var archive = TrustedAsset(list[archiveName]);
        var sums = TrustedAsset(list["SHA256SUMS"]);
        return archive is { Size: >= 1 and <= ArchiveLimit } && sums is not null ? new(version, archive, sums.Url) : null;
    }

    private static ReleaseAsset? TrustedAsset(IEnumerable<ReleaseAsset> assets)
    {
        var entries = assets.Take(2).ToArray();
        if (entries.Length != 1)
            return null;
        var entry = entries[0];
        var url = entry.Url;
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com")
            return null;
        return DownloadRoots.Any(root =>
            url.OriginalString.StartsWith(root, StringComparison.Ordinal) && url.AbsoluteUri.StartsWith(root, StringComparison.Ordinal))
            ? entry : null;
    }

    public static UpdateBlock? Block(bool gameRunning, bool serversRunning) =>
        gameRunning ? UpdateBlock.GameRunning : serversRunning ? UpdateBlock.ServersRunning : null;

    public static string? Checksum(ReadOnlySpan<byte> sums, string name)
    {
        if (sums.Length > SumsLimit)
            return null;
        string text;
        try { text = StrictUtf8.GetString(sums); }
        catch (DecoderFallbackException) { return null; }

        string? checksum = null;
        foreach (string raw in text.Split('\n'))
        {
            var line = raw.AsSpan();
            if (line.EndsWith("\r"))
                line = line[..^1];
            if (line.Length <= 66 || line[64] != ' ' || line[65] is not (' ' or '*') ||
                !line[66..].SequenceEqual(name) || !IsHex(line[..64]))
                continue;
            if (checksum is not null)
                return null;
            checksum = line[..64].ToString().ToLowerInvariant();
        }
        return checksum;
    }

    public static bool VerifyArchive(Stream stream, long size, string sha256, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (size is < 1 or > ArchiveLimit || sha256.Length != 64 || !IsHex(sha256))
            return false;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long bytes = 0;
            int read;
            while ((read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, size - bytes + 1))) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes += read;
                if (bytes > size)
                    return false;
                hasher.AppendData(buffer.AsSpan(0, read));
            }
            return bytes == size && Convert.ToHexString(hasher.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
            if (!char.IsAsciiHexDigit(character))
                return false;
        return true;
    }

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersion();
}
