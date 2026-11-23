using System.Collections.Immutable;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsConfigShareStorage(WindowsSettingsFolder folder) : IConfigShareStorage
{
    public string Root => folder.Root;

    internal static Uri ServiceAddress()
    {
#if UPDATE_FIXTURES
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("COD2_SHARE_URL"), UriKind.Absolute, out var fixture) &&
            fixture.Host == "127.0.0.1" && ConfigShareClient.BaseFromEnvironment(fixture.AbsoluteUri) == fixture)
            return fixture;

        // Even without a stand-in, a fixture build must never publish to the live service.
        return new("http://127.0.0.1:9");
#else
        return ConfigShareClient.Production;
#endif
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) =>
        await WindowsGameSessionHost.OpenLeaseAsync(Root, ".shared-setups.lock", cancellationToken).ConfigureAwait(false);

    public ValueTask<ImmutableArray<byte>?> ReadAsync(string name, CancellationToken cancellationToken) =>
        folder.ReadAsync(name, cancellationToken);

    public ValueTask WriteAsync(string name, ImmutableArray<byte> data, CancellationToken cancellationToken) =>
        folder.WriteAsync(name, data, cancellationToken);

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken) => folder.DeleteAsync(name, cancellationToken);
}
