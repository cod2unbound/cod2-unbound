using System.Collections.Immutable;
using System.Text.Json;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsConfigDirectoryStorage(string root) : IConfigDirectoryStorage
{
    private readonly WindowsSettingsFolder files = new(root);
    private static string Name(ConfigDirectoryFile file) => file == ConfigDirectoryFile.Cache ? "directory-cache.json" : "saved-setups.json";

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) =>
        await WindowsGameSessionHost.OpenLeaseAsync(root, ".directory.lock", cancellationToken);

    public ValueTask<ImmutableArray<byte>?> ReadAsync(ConfigDirectoryFile file, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(WindowsConfigProfiles.ReadBounded(root, Name(file), limit, allowOversized: true));
    }

    public ValueTask<DateTimeOffset?> ModifiedAsync(ConfigDirectoryFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!new WindowsFiles().IsRegularFile(root, Name(file)))
            return ValueTask.FromResult<DateTimeOffset?>(null);
        return ValueTask.FromResult<DateTimeOffset?>(File.GetLastWriteTimeUtc(WindowsFiles.Under(root, Name(file))));
    }

    public ValueTask WriteAsync(ConfigDirectoryFile file, ImmutableArray<byte> data, CancellationToken cancellationToken) =>
        files.WriteAsync(Name(file), data, cancellationToken);

    public ValueTask DeleteAsync(ConfigDirectoryFile file, CancellationToken cancellationToken) =>
        files.DeleteAsync(Name(file), cancellationToken);
}

internal sealed class WindowsConfigFetch : IConfigTextFetcher
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly IReadOnlyDictionary<Uri, ImmutableArray<byte>>? fixtures;

    private WindowsConfigFetch(IReadOnlyDictionary<Uri, ImmutableArray<byte>>? fixtures) => this.fixtures = fixtures;

    internal static ConfigDirectory Create(string root, bool offline)
    {
        var storage = new WindowsConfigDirectoryStorage(Path.Join(root, "catalogue"));
        string? fixture = Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_CONFIG_DIRECTORY");
        if (offline && fixture is not null)
        {
            var bytes = WindowsConfigProfiles.ReadBounded(Path.GetDirectoryName(fixture)!, Path.GetFileName(fixture), 1024 * 1024)
                ?? throw new FileNotFoundException("The directory fixture is unavailable.");
            using var json = JsonDocument.Parse(bytes.AsMemory());
            var data = json.RootElement;
            var index = new Uri(data.GetProperty("indexURL").GetString()!);
            var signature = new Uri(data.GetProperty("signatureURL").GetString()!);
            var signed = data.GetProperty("cases").EnumerateArray().First(row => row.GetProperty("id").GetString() == "signed");
            var indexBytes = signed.GetProperty("index").GetBytesFromBase64();
            using var indexJson = JsonDocument.Parse(indexBytes);
            var profile = new Uri(indexJson.RootElement.GetProperty("entries")[0].GetProperty("configURL").GetString()!);
            return new(new(index, signature, [.. data.GetProperty("publicKey").GetBytesFromBase64()]), storage,
                new WindowsConfigFetch(new Dictionary<Uri, ImmutableArray<byte>>
                {
                    [index] = [.. indexBytes],
                    [signature] = [.. signed.GetProperty("signature").GetBytesFromBase64()],
                    [profile] = [.. data.GetProperty("profile").GetBytesFromBase64()]
                }));
        }
        return new(offline ? null : ConfigDirectory.Production, storage, new WindowsConfigFetch(null));
    }

    public async ValueTask<ConfigTextResponse> FetchAsync(ConfigFetchRequest request, CancellationToken cancellationToken)
    {
        if (!request.Allows(request.Url))
            throw new ConfigImportException(ConfigImportFailure.OutsideRoot);

        if (fixtures is not null)
        {
            if (!fixtures.TryGetValue(request.Url, out var data))
                throw new ConfigFetchHttpException(404);
            return new(data, request.Url, "text/plain");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
            message.Headers.UserAgent.ParseAdd("CoD2-Unbound-Launcher");
            using var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new ConfigFetchHttpException((int)response.StatusCode);
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[request.MaximumBytes + 1];
            int length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token);
            if (length > request.MaximumBytes)
                throw new ConfigImportException(ConfigImportFailure.TooLarge);
            return new([.. bytes.AsSpan(0, length)], request.Url, response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ConfigFetchUnavailableException("The player directory could not be reached.", error);
        }
    }
}
