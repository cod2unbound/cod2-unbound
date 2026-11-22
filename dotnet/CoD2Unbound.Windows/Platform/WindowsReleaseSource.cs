using System.Collections.Immutable;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsReleaseSource(bool testMode) : ILauncherReleaseSource
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal static string? Fixture => Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_RELEASES");

    public async ValueTask<ImmutableArray<byte>> ReleasesAsync(CancellationToken cancellationToken)
    {
        if (Fixture is { } fixture)
        {
            using var local = WindowsFiles.Open(Path.GetDirectoryName(fixture)!, Path.GetFileName(fixture));
            return await ReadAsync(local, cancellationToken);
        }
        if (testMode)
            throw new IOException("No local release fixture was supplied.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/cod2unbound/cod2-unbound/releases?per_page=10");
        request.Headers.UserAgent.ParseAdd("CoD2-Unbound-Launcher");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        return await ReadAsync(stream, deadline.Token);
    }

    public ValueTask<ImmutableArray<byte>?> NewsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<ImmutableArray<byte>?>(null);

    private static async ValueTask<ImmutableArray<byte>> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] input = new byte[1024 * 1024 + 1];
        int length = await stream.ReadAtLeastAsync(input, input.Length, false, cancellationToken);
        if (length == input.Length)
            throw new IOException("The release list is too large.");
        return [.. input.AsSpan(0, length)];
    }
}
