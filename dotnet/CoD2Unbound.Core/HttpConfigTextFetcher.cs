using System.Net;
using System.Net.Sockets;

namespace CoD2Unbound.Core;

/// <summary>Bounded, anonymous transport for user-supplied config links.</summary>
public sealed class HttpConfigTextFetcher : IConfigTextFetcher
{
    private static readonly HttpClient Internet = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly HttpClient Local = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectCallback = ConnectLocalAsync
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async ValueTask<ConfigTextResponse> FetchAsync(ConfigFetchRequest request, CancellationToken cancellationToken)
    {
        if (request.MaximumBytes is < 0 or > 16 * 1024 * 1024)
            throw new ConfigImportException(ConfigImportFailure.TooLarge);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var url = request.Url;
        try
        {
            for (int redirects = 0; ; redirects++)
            {
                if (!request.Allows(url) || ConfigImportLoader.IsCredentialFile(Uri.UnescapeDataString(url.AbsolutePath)))
                    throw new ConfigImportException(ConfigImportFailure.OutsideRoot);

                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.UserAgent.ParseAdd("CoD2-Unbound-Launcher");
                var client = url.IsLoopback ? Local : Internet;
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 &&
                    response.Headers.Location is { } location)
                {
                    if (redirects == 4 || !Uri.TryCreate(url, location, out var next))
                        throw new ConfigImportException(ConfigImportFailure.OutsideRoot);
                    url = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new ConfigFetchHttpException(status);
                if (response.Content.Headers.ContentLength > request.MaximumBytes)
                    throw new ConfigImportException(ConfigImportFailure.TooLarge);
                using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var bytes = new byte[request.MaximumBytes + 1];
                int length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token).ConfigureAwait(false);
                if (length > request.MaximumBytes)
                    throw new ConfigImportException(ConfigImportFailure.TooLarge);
                return new([.. bytes.AsSpan(0, length)], url, response.Content.Headers.ContentType?.MediaType);
            }
        }
        catch (Exception error) when (error is HttpRequestException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ConfigFetchUnavailableException("The config server could not be reached.", error);
        }
    }

    private static async ValueTask<Stream> ConnectLocalAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        IPAddress[] addresses = context.DnsEndPoint.Host switch
        {
            "localhost" => [IPAddress.IPv6Loopback, IPAddress.Loopback],
            "127.0.0.1" => [IPAddress.Loopback],
            "::1" => [IPAddress.IPv6Loopback],
            _ => throw new IOException("The config server is outside this PC.")
        };
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
