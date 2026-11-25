using System.Collections.Immutable;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsReleaseSource(bool testMode) : ILauncherReleaseSource
{
    private static readonly HttpClient Production = ReleaseDownload.CreateClient();
#if LOCAL_UPDATE_FEED
    private static readonly HttpClient Local = ReleaseDownload.CreateClient(loopbackOnly: true);
    private static readonly string? MirrorAddress = Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_UPDATE_MIRROR_ORIGIN");
#endif
    internal static Uri? LocalOrigin { get; } = TestOrigin();
    internal static Uri? LocalAssetOrigin =>
#if LOCAL_UPDATE_FEED
        MirrorAddress is not null ? null : LocalOrigin;
#else
        null;
#endif
    internal static ReleaseDownload Download { get; } = CreateDownload();
    internal static string? Fixture =>
#if UPDATE_FIXTURES
        Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_RELEASES");
#else
        null;
#endif

    public async ValueTask<ReleaseFeed> ReleasesAsync(TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        if (LocalOrigin is not null || !testMode)
            return await ReleaseNotes.FetchAsync(Download, timeZone, cancellationToken);
        if (Fixture is not { } fixture)
            throw new IOException("No local release fixture was supplied.");
        using var local = WindowsFiles.Open(Path.GetDirectoryName(fixture)!, Path.GetFileName(fixture));
        byte[] input = new byte[ReleaseNotes.FeedLimit + 1];
        int length = await local.ReadAtLeastAsync(input, input.Length, false, cancellationToken);
        if (length == input.Length)
            throw new IOException("The release list is too large.");
        return ReleaseNotes.Parse(input.AsSpan(0, length), timeZone: timeZone) with { ListedByGitHub = false };
    }

    public ValueTask<ImmutableArray<byte>?> NewsAsync(CancellationToken cancellationToken) =>
        LocalOrigin is not null || !testMode ? NewsFeed.FetchAsync(Download, cancellationToken) :
            ValueTask.FromResult<ImmutableArray<byte>?>(null);

    private static Uri? TestOrigin()
    {
#if LOCAL_UPDATE_FEED
        var address = Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_UPDATE_ORIGIN");
        if (address is not null || MirrorAddress is not null)
        {
            if (LoopbackOrigin(address) is { } origin)
                return origin;
            // A malformed test override must never silently fall back to the public feed.
            return new Uri("http://invalid.invalid/");
        }
#endif
        return null;
    }

    private static ReleaseDownload CreateDownload()
    {
#if LOCAL_UPDATE_FEED
        if (LocalOrigin is { } origin)
        {
            if (MirrorAddress is not null)
                return new(new HttpClient(new LocalReleaseHosts(LoopbackOrigin(origin.AbsoluteUri), LoopbackOrigin(MirrorAddress)))
                    { Timeout = Timeout.InfiniteTimeSpan });
            return new(Local, origin);
        }
#endif
        return new(Production);
    }

#if LOCAL_UPDATE_FEED
    private static Uri? LoopbackOrigin(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && WindowsReleaseAssets.IsLoopbackOrigin(uri) ? uri : null;

    // Keep the real release URLs through validation and policy. Only the socket goes to a stand-in.
    private sealed class LocalReleaseHosts(Uri? primary, Uri? mirror) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (primary is null || mirror is null || primary == mirror ||
                request.RequestUri is not { Scheme: "https", Port: 443 } source)
                throw new HttpRequestException("Two distinct loopback update origins are required.");

            var origin = source.Host switch
            {
                "api.github.com" or "raw.githubusercontent.com" or "github.com" or "release-assets.githubusercontent.com" => primary,
                "cod2unbound.com" => mirror,
                _ => throw new HttpRequestException("No local stand-in is configured for this update host.")
            };
            var target = new Uri(origin.GetLeftPart(UriPartial.Authority) + source.PathAndQuery);
            using var localRequest = new HttpRequestMessage(request.Method, target);
            foreach (var accept in request.Headers.Accept)
                localRequest.Headers.Accept.Add(accept);
            return await Local.SendAsync(localRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
    }
#endif
}
