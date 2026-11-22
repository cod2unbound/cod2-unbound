using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public sealed record ServerBrowserPreferences(ServerFilters Filters, ImmutableDictionary<string, DateTimeOffset> LaunchedAt)
{
    public const string FileName = "launcher-servers.json";
    public static ServerBrowserPreferences Default { get; } = new(new(), ImmutableDictionary<string, DateTimeOffset>.Empty);

    public ServerBrowserPreferences StampLaunch(string address, DateTimeOffset date) =>
        this with { LaunchedAt = LaunchedAt.SetItem(LaunchLink.CanonicalAddress(address), date) };

    public static ServerBrowserPreferences Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > 1_048_576)
            throw new InvalidDataException("The server preferences file is too large.");
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a server preferences object.");
        var root = document.RootElement;
        var filters = Value(root, "filters");
        var version = Value(filters, "version") is { ValueKind: JsonValueKind.String } label ? label.GetString() : null;
        var edition = version switch
        {
            "Original 1.3" => ServerEditionFilter.Stock,
            "CoD2x 1.4.6.8" => ServerEditionFilter.Cod2x,
            _ => ServerEditionFilter.All
        };
        var mode = Value(filters, "mode") is { ValueKind: JsonValueKind.String } name ? name.GetString() : null;
        var ping = Value(filters, "maxPing");
        int? maxPing = ping.ValueKind == JsonValueKind.Number && ping.TryGetInt32(out int integer) && ServerFilters.PingLimits.Contains(integer)
            ? integer : null;
        var dates = ImmutableDictionary.CreateBuilder<string, DateTimeOffset>(StringComparer.Ordinal);
        if (Value(root, "launchedAt") is { ValueKind: JsonValueKind.Object } map)
        {
            try
            {
                foreach (var item in map.EnumerateObject())
                {
                    var date = LauncherLibrary.ToDate(item.Value.GetDouble());
                    if (date is not null)
                        dates[item.Name] = date.Value;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or JsonException) { dates.Clear(); }
        }
        return new(new(edition, mode, maxPing, Flag("hideEmpty"), Flag("hideFull"), Flag("hidePassworded")), dates.ToImmutable());

        bool Flag(string key) => Value(filters, key).ValueKind == JsonValueKind.True;
    }

    public ImmutableArray<byte> Write()
    {
        var filter = new ServerFilterRecord(Filters.Version switch
        {
            ServerEditionFilter.Stock => "Original 1.3",
            ServerEditionFilter.Cod2x => "CoD2x 1.4.6.8",
            _ => "All"
        }, Filters.Mode, Filters.MaxPing, Filters.HideEmpty, Filters.HideFull, Filters.HidePassworded);
        var dates = LaunchedAt.ToDictionary(pair => pair.Key, pair => LauncherLibrary.Number(pair.Value)!.Value, StringComparer.Ordinal);
        return [.. JsonSerializer.SerializeToUtf8Bytes(new ServerBrowserRecord(filter, dates), ServerBrowserJson.Default.ServerBrowserRecord)];
    }

    public static async ValueTask<ServerBrowserPreferences> LoadAsync(ISettingsFolder files, CancellationToken cancellationToken = default)
    {
        try
        {
            return await files.ReadAsync(FileName, cancellationToken).ConfigureAwait(false) is { } bytes
                ? Read(bytes.AsMemory()) : Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return Default; }
    }

    public ValueTask SaveAsync(ISettingsFolder files, CancellationToken cancellationToken = default) =>
        files.WriteAsync(FileName, Write(), cancellationToken);

    private static JsonElement Value(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var item) ? item : default;
}

internal sealed record ServerFilterRecord(string Version, string? Mode, int? MaxPing, bool HideEmpty, bool HideFull, bool HidePassworded);
internal sealed record ServerBrowserRecord(ServerFilterRecord Filters, Dictionary<string, double> LaunchedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServerBrowserRecord))]
internal sealed partial class ServerBrowserJson : JsonSerializerContext;
