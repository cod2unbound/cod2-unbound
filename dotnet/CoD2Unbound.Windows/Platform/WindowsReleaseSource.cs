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
    internal static string? Fixture => Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_RELEASES");

    public async ValueTask<ImmutableArray<byte>> ReleasesAsync(CancellationToken cancellationToken)
    {
        if (LocalOrigin is { } origin)
            return await ReadFeedAsync(new Uri(origin, "releases"), cancellationToken);
        if (!testMode)
            return await ReadFeedAsync(WindowsReleaseAssets.Feed, cancellationToken);
        if (Fixture is not { } fixture)
            throw new IOException("No local release fixture was supplied.");
        using var local = WindowsFiles.Open(Path.GetDirectoryName(fixture)!, Path.GetFileName(fixture));
        byte[] input = new byte[1_000_001];
        int length = await local.ReadAtLeastAsync(input, input.Length, false, cancellationToken);
        if (length == input.Length)
            throw new IOException("The release list is too large.");
        return [.. input.AsSpan(0, length)];
    }

    public ValueTask<ImmutableArray<byte>?> NewsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ImmutableArray<byte>?>(null);

    private static async ValueTask<ImmutableArray<byte>> ReadFeedAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await Download.CopyAsync(uri, output, 1_000_000, null, null, cancellationToken, feed: true);
        return [.. output.GetBuffer().AsSpan(0, (int)output.Length)];
    }

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
