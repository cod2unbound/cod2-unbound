using System.Buffers;
using System.Collections.Immutable;
using System.Net;

namespace CoD2Unbound.Core;

public sealed class LauncherUpdateException(string message, Exception? inner = null, UpdateIssue issue = UpdateIssue.Refused)
    : IOException(message, inner)
{
    public UpdateIssue Issue { get; } = issue;
}

/// <summary>Streams release data without credentials or implicit redirects.</summary>
public sealed class ReleaseDownload(HttpClient client, Uri? loopbackOrigin = null)
{
    internal Uri? LoopbackOrigin => loopbackOrigin;

    public static HttpClient CreateClient(bool loopbackOnly = false)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = !loopbackOnly,
            ConnectTimeout = TimeSpan.FromSeconds(15), MaxResponseHeadersLength = 32
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "User-Agent", "CoD2-Unbound-Launcher" } }
        };
    }

    internal async Task<(HttpStatusCode Status, ImmutableArray<byte> Bytes)> ReadFeedAsync(Uri uri, int limit,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var status = await CopyResponseAsync(uri, output, limit, null, null, cancellationToken, feed: true).ConfigureAwait(false);
        return (status, [.. output.GetBuffer().AsSpan(0, (int)output.Length)]);
    }

    public async Task<string> ReadSetupChecksumAsync(ReleaseOffer offer, CancellationToken cancellationToken)
    {
        using var sums = new MemoryStream();
        await CopyAssetAsync(offer, offer.Checksums, WindowsReleaseAssets.ChecksumsName, sums,
            WindowsReleaseAssets.ChecksumsLimit, offer.ChecksumsSize, null, cancellationToken).ConfigureAwait(false);
        return ReleasePackage.Checksum(sums.GetBuffer().AsSpan(0, (int)sums.Length), offer.Archive.Name)
            ?? throw new LauncherUpdateException("The release did not provide a usable checksum for Setup.");
    }

    public Task CopySetupAsync(ReleaseOffer offer, Stream output, Action<long>? progress, CancellationToken cancellationToken) =>
        CopyAssetAsync(offer, offer.Archive.Url, offer.Archive.Name, output, WindowsReleaseAssets.SetupLimit,
            offer.Archive.Size, progress, cancellationToken);

    public async Task<ImmutableArray<byte>> ReadZpamListingAsync(CancellationToken cancellationToken)
    {
        var (status, bytes) = await ReadFeedAsync(ZpamRelease.ReleasesUrl, 1_000_000, cancellationToken).ConfigureAwait(false);
        return status == HttpStatusCode.OK ? bytes : throw Unavailable();
    }

    public async Task CopyZpamAsync(ZpamRelease release, Stream output, Action<long> progress, CancellationToken cancellationToken)
    {
        if (!release.HasVersionedAsset() || !ZpamRelease.IsDownloadAddress(release.Url) || release.Bytes is < 1 or > 1_073_741_824)
            throw Unavailable();
        if (await CopyResponseAsync(release.Url, output, release.Bytes, release.Bytes, progress, cancellationToken,
            feed: false).ConfigureAwait(false) != HttpStatusCode.OK)
            throw Unavailable();
    }

    private async Task CopyAssetAsync(ReleaseOffer offer, Uri url, string name, Stream output, long limit, long size,
        Action<long>? progress, CancellationToken cancellationToken)
    {
        bool canRetry = loopbackOrigin is null && offer.ListedByGitHub && url.IsAbsoluteUri && url.Host == "github.com";
        long start = canRetry ? output.Position : 0;
        try
        {
            var status = await CopyResponseAsync(url, output, limit, size, progress, cancellationToken, feed: false).ConfigureAwait(false);
            if (status == HttpStatusCode.OK)
                return;
            if (!canRetry)
                throw Unavailable();
        }
        catch (Exception error) when (canRetry && IsTransportFailure(error, cancellationToken)) { }

        cancellationToken.ThrowIfCancellationRequested();
        // A failed transfer may have written part of the asset. Start the second copy at the same offset.
        output.SetLength(start);
        output.Position = start;
        progress?.Invoke(0);
        var mirror = new Uri($"{ReleasePackage.MirrorDownloads}v{offer.Version}/{name}");
        if (await CopyResponseAsync(mirror, output, limit, size, progress, cancellationToken, feed: false).ConfigureAwait(false) != HttpStatusCode.OK)
            throw Unavailable();
    }

    internal static bool IsTransportFailure(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException or HttpIOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    internal static LauncherUpdateException Unavailable() => new("The update server did not send the requested file.");

    private async Task<HttpStatusCode> CopyResponseAsync(Uri uri, Stream output, long limit, long? exactBytes, Action<long>? progress,
        CancellationToken cancellationToken, bool feed)
    {
        if (!AllowedStart(uri, feed))
            throw new LauncherUpdateException("The update server tried to send the download to an unexpected address.");
        bool zpam = ZpamRelease.IsDownloadAddress(uri);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(feed ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(15));
        var token = deadline.Token;
        for (int redirectsLeft = 4; ; redirectsLeft--)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (feed && (uri == WindowsReleaseAssets.Feed || uri == ZpamRelease.ReleasesUrl))
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (!feed && redirectsLeft > 0 && response.Headers.Location is { } location && Uri.TryCreate(uri, location, out var next)
                    && next is { Scheme: "https", UserInfo.Length: 0, Fragment.Length: 0 } &&
                    (!zpam || next.Port == 443 && (ZpamRelease.IsDownloadAddress(next) || next.Host == "release-assets.githubusercontent.com")) &&
                    (loopbackOrigin is null || IsOnLoopback(next)))
                {
                    uri = next;
                    continue;
                }
            }
            if (response.StatusCode != HttpStatusCode.OK)
                return response.StatusCode;
            var length = response.Content.Headers.ContentLength;
            if (length > limit || (length is not null && exactBytes is { } expected && length != expected))
                throw SizeError();
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await CopyBoundedAsync(input, output, limit, exactBytes, progress, token, cancellationToken).ConfigureAwait(false);
            return response.StatusCode;
        }
    }

    private bool AllowedStart(Uri uri, bool feed)
    {
        if (loopbackOrigin is not null)
            return IsOnLoopback(uri);
        return feed ? uri == WindowsReleaseAssets.Feed || uri == ReleaseNotes.MirrorUrl || uri == NewsFeed.Url ||
            uri == NewsFeed.MirrorUrl || uri == ZpamRelease.ReleasesUrl
            : ReleasePackage.IsDownloadAddress(uri) || ZpamRelease.IsDownloadAddress(uri);
    }

    private bool IsOnLoopback(Uri uri) => WindowsReleaseAssets.IsLoopbackOrigin(loopbackOrigin) &&
        uri is { IsAbsoluteUri: true, UserInfo.Length: 0, Fragment.Length: 0 } &&
        uri.Scheme == loopbackOrigin!.Scheme && uri.Host == loopbackOrigin.Host && uri.Port == loopbackOrigin.Port;

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, long? exactBytes,
        Action<long>? progress, CancellationToken readToken, CancellationToken cancellationToken)
    {
        long maximum = Math.Min(exactBytes ?? limit, limit);
        long total = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum - total + 1)),
                readToken).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > maximum)
                    throw SizeError();
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                progress?.Invoke(total);
            }
            if (exactBytes is { } expected && total != expected)
                throw SizeError();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static LauncherUpdateException SizeError() => new("The download size did not match the release.");
}
