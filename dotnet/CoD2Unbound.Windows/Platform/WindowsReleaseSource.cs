using System.Collections.Immutable;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsReleaseSource(bool testMode) : ILauncherReleaseSource
{
    private static readonly HttpClient Production = ReleaseDownload.CreateClient();
#if LOCAL_UPDATE_FEED
    private static readonly HttpClient Local = ReleaseDownload.CreateClient(loopbackOnly: true);
#endif
    internal static Uri? LocalOrigin { get; } = TestOrigin();
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
        if (address is not null)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var origin) && WindowsReleaseAssets.IsLoopbackOrigin(origin))
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
            return new(Local, origin);
#endif
        return new(Production);
    }
}
