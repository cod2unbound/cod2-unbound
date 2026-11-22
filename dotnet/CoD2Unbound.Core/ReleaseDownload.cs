using System.Buffers;
using System.Net;

namespace CoD2Unbound.Core;

public sealed class LauncherUpdateException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Streams release data without credentials or implicit redirects.</summary>
public sealed class ReleaseDownload(HttpClient client, Uri? loopbackOrigin = null)
{
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

    public async Task CopyAsync(Uri uri, Stream output, long limit, long? exactBytes, Action<long>? progress,
        CancellationToken cancellationToken, bool feed = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(feed ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(15));
        var token = deadline.Token;
        for (int hop = 0; hop <= 4; hop++)
        {
            if (!Allowed(uri, feed, hop != 0))
                throw new LauncherUpdateException("The update server tried to send the download to an unexpected address.");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (feed && loopbackOrigin is null)
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is { } location && Uri.TryCreate(uri, location, out var next))
                {
                    uri = next;
                    continue;
                }
                break;
            }
            if (response.StatusCode != HttpStatusCode.OK)
                throw new LauncherUpdateException("The update server did not send the requested file.");
            var length = response.Content.Headers.ContentLength;
            if (length > limit || (length is not null && exactBytes is { } expected && length != expected))
                throw SizeError();
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await CopyBoundedAsync(input, output, limit, exactBytes, progress, token).ConfigureAwait(false);
            return;
        }
        throw new LauncherUpdateException("The update server sent an invalid redirect.");
    }

    private bool Allowed(Uri uri, bool feed, bool redirected)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return false;
        if (loopbackOrigin is not null)
            return WindowsReleaseAssets.IsLoopbackOrigin(loopbackOrigin) &&
                uri.Scheme == loopbackOrigin.Scheme && uri.Host == loopbackOrigin.Host && uri.Port == loopbackOrigin.Port;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
            return false;
        if (feed)
            return uri == WindowsReleaseAssets.Feed;
        return uri.Host == "github.com"
            ? uri.AbsolutePath.StartsWith("/cod2unbound/cod2-unbound/releases/download/", StringComparison.Ordinal) && uri.Query.Length == 0
            : redirected && uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, long? exactBytes,
        Action<long>? progress, CancellationToken token)
    {
        long maximum = Math.Min(exactBytes ?? limit, limit);
        long total = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum - total + 1)),
                token).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > maximum)
                    throw SizeError();
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                progress?.Invoke(total);
            }
            if (exactBytes is { } expected && total != expected)
                throw SizeError();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static LauncherUpdateException SizeError() => new("The download size did not match the release.");
}
