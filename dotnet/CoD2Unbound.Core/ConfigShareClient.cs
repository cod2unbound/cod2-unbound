using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public enum ConfigShareReplyKind { Sent, Already, Network, Day, TooLarge, Unreachable, Cancelled }
public enum ConfigWithdrawReplyKind { Withdrawn, Removal, Forbidden, Unreachable, Cancelled }

public sealed record ConfigShareReceipt(long Request, Uri Url, string Id, string Token)
{
    public override string ToString() => $"ConfigShareReceipt {{ Request = {Request}, Url = {Url}, Id = {Id} }}";
}

public sealed record ConfigShareReply(ConfigShareReplyKind Kind, ConfigShareReceipt? Receipt = null);
public sealed record ConfigWithdrawReply(ConfigWithdrawReplyKind Kind, Uri? Url = null);
public sealed record ConfigShareResponse(int Status, ImmutableArray<byte> Body);

public interface IConfigShareTransport
{
    // Explicit JSON POST with a whole-request timeout. Reject redirects, ambient credentials and oversized replies.
    ValueTask<ConfigShareResponse> PostAsync(Uri destination, ImmutableArray<byte> payload, TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class ConfigShareClient
{
    internal const int ReplyLimit = 16 * 1024;
    public const string Pulls = "https://github.com/cod2unbound/cod2-configs/pull/";
    public static Uri Production { get; } = new("https://cod2unbound.com");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly IConfigShareTransport transport;
    private readonly Uri baseUrl;

    public ConfigShareClient(IConfigShareTransport transport, Uri? baseUrl = null)
    {
        this.transport = transport;
        this.baseUrl = baseUrl ?? BaseFromEnvironment(Environment.GetEnvironmentVariable("COD2_SHARE_URL"));
        if (!IsShareBase(this.baseUrl))
            throw new ArgumentException("Use HTTPS or a loopback HTTP address for the Share service.", nameof(baseUrl));
    }

    public static Uri BaseFromEnvironment(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsShareBase(uri)
        ? uri : Production;

    public async ValueTask<ConfigShareReply> ShareAsync(ConfigShareRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(ConfigShareReplyKind.Cancelled);
        if (ConfigShare.ValidateRequest(request) is { } invalid)
            return new(invalid);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ConfigShareJson.Default.ConfigShareRequest);
        var response = await PostAsync("share", bytes, cancellationToken).ConfigureAwait(false);
        return response is not null ? ShareReply(response.Status, response.Body) :
            new(cancellationToken.IsCancellationRequested ? ConfigShareReplyKind.Cancelled : ConfigShareReplyKind.Unreachable);
    }

    public async ValueTask<ConfigWithdrawReply> WithdrawAsync(long request, string token, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(ConfigWithdrawReplyKind.Cancelled);
        if (request <= 0 || !ValidDigest(token))
            return new(ConfigWithdrawReplyKind.Unreachable);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ConfigWithdrawRequest(1, request, token), ConfigShareJson.Default.ConfigWithdrawRequest);
        var response = await PostAsync("withdraw", bytes, cancellationToken).ConfigureAwait(false);
        return response is not null ? WithdrawReply(response.Status, response.Body) :
            new(cancellationToken.IsCancellationRequested ? ConfigWithdrawReplyKind.Cancelled : ConfigWithdrawReplyKind.Unreachable);
    }

    public static ConfigShareReply ShareReply(int status, ImmutableArray<byte> body)
    {
        using var document = Parse(body);
        var value = document?.RootElement ?? default;
        switch (status)
        {
            case 201:
                long request = Number(value, "request");
                string? id = Text(value, "id");
                string? token = Text(value, "token");
                if (request > 0 && !string.IsNullOrEmpty(id) && ValidDigest(token) && PullUrl(Text(value, "url"), request) is { } url)
                    return new(ConfigShareReplyKind.Sent, new(request, url, id, token!));
                break;
            case 409 when Text(value, "error") == "already":
                return new(ConfigShareReplyKind.Already);
            case 413:
                return new(ConfigShareReplyKind.TooLarge);
            case 429 when Text(value, "error") == "limit":
                return Text(value, "scope") switch
                {
                    "day" => new(ConfigShareReplyKind.Day),
                    "network" => new(ConfigShareReplyKind.Network),
                    _ => new(ConfigShareReplyKind.Unreachable)
                };
        }
        return new(ConfigShareReplyKind.Unreachable);
    }

    public static ConfigWithdrawReply WithdrawReply(int status, ImmutableArray<byte> body)
    {
        using var document = Parse(body);
        var value = document?.RootElement ?? default;
        return status switch
        {
            200 when Text(value, "state") == "withdrawn" => new(ConfigWithdrawReplyKind.Withdrawn),
            200 when Text(value, "state") == "removal" && PullUrl(Text(value, "url")) is { } url =>
                new(ConfigWithdrawReplyKind.Removal, url),
            403 when Text(value, "error") == "forbidden" => new(ConfigWithdrawReplyKind.Forbidden),
            _ => new(ConfigWithdrawReplyKind.Unreachable)
        };
    }

    internal static Uri? PullUrl(string? address, long? number = null)
    {
        if (address is null || !address.StartsWith(Pulls, StringComparison.Ordinal))
            return null;
        var suffix = address.AsSpan(Pulls.Length);
        if (!long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed <= 0 || (number is { } requested && parsed != requested))
            return null;
        // Return the validated numeric path, independent of URI normalization.
        return suffix.SequenceEqual(parsed.ToString(CultureInfo.InvariantCulture)) ? new Uri(address) : null;
    }

    internal static bool ValidDigest(string? token) => token is { Length: 64 } && token.All(char.IsAsciiHexDigitLower);

    private async ValueTask<ConfigShareResponse?> PostAsync(string method, byte[] bytes, CancellationToken cancellationToken)
    {
        var destination = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/api/configs/" + method);
        try
        {
            var reply = await transport.PostAsync(destination, ImmutableArray.Create(bytes), Timeout, cancellationToken).ConfigureAwait(false);
            return reply.Body.IsDefault || reply.Body.Length > ReplyLimit ? null : reply;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { return null; }
    }

    private static bool IsShareBase(Uri url) => ConfigFetchRequest.IsPermitted(url) && url.Query.Length == 0 && url.Fragment.Length == 0;

    private static JsonDocument? Parse(ImmutableArray<byte> bytes)
    {
        if (bytes.IsDefault || bytes.Length > ReplyLimit)
            return null;
        try { return JsonDocument.Parse(bytes.AsMemory()); }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static long Number(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long number) ? number : 0;
}

public sealed class HttpConfigShareTransport : IConfigShareTransport, IDisposable
{
    private const int ReplyLimit = ConfigShareClient.ReplyLimit;
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    public async ValueTask<ConfigShareResponse> PostAsync(Uri destination, ImmutableArray<byte> payload, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!ConfigFetchRequest.IsPermitted(destination))
            throw new ArgumentException("The destination does not support private Share requests.", nameof(destination));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        using var message = new HttpRequestMessage(HttpMethod.Post, destination)
        {
            Content = new ReadOnlyMemoryContent(payload.AsMemory())
        };
        message.Content.Headers.ContentType = new("application/json");
        message.Headers.Accept.Add(new("application/json"));
        message.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        using var reply = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (reply.Content.Headers.ContentLength > ReplyLimit)
            throw new IOException("The Share response exceeded its byte limit.");
        await using var stream = await reply.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[ReplyLimit + 1];
        int used = 0;
        while (used < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(used), token).ConfigureAwait(false);
            if (read == 0)
                return new((int)reply.StatusCode, ImmutableArray.Create(buffer.AsSpan(0, used)));
            used += read;
        }
        throw new IOException("The Share response exceeded its byte limit.");
    }

    public void Dispose() => client.Dispose();
}

internal sealed record ConfigWithdrawRequest(int V, long Request, string Token);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConfigShareRequest))]
[JsonSerializable(typeof(ConfigWithdrawRequest))]
[JsonSerializable(typeof(ConfigShareRecordFile))]
internal sealed partial class ConfigShareJson : JsonSerializerContext;
