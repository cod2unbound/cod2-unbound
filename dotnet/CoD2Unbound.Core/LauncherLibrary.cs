using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public sealed record LauncherLibrary
{
    public const string FileName = "launcher-library.json";
    private static readonly DateTimeOffset SwiftEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public ImmutableHashSet<string> Favorites { get; init; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    public ImmutableArray<string> Recent { get; init; } = [];
    public ImmutableArray<GameServer> Cache { get; init; } = [];
    public DateTimeOffset? CachedAt { get; init; }
    public ImmutableArray<ReleaseDispatch> Dispatches { get; init; } = [];
    public DateTimeOffset? DispatchesCheckedAt { get; init; }
    public ImmutableArray<NewsDispatch> News { get; init; } = [];
    public NewsDispatch? NewsNext { get; init; }
    public ImmutableArray<ReleaseDetails> Releases { get; init; } = [];
    public ImmutableArray<string> NotifiedUpdates { get; init; } = [];
    public ImmutableDictionary<string, DateTimeOffset> Played { get; init; } = ImmutableDictionary<string, DateTimeOffset>.Empty;

    public LauncherLibrary Joined(string address, DateTimeOffset now)
    {
        ImmutableArray<string> recent = [.. Recent.Where(item => item != address).Prepend(address).Take(30)];
        var kept = recent.ToHashSet(StringComparer.Ordinal);
        return this with { Recent = recent, Played = Played.SetItem(address, now).Where(pair => kept.Contains(pair.Key))
            .ToImmutableDictionary(StringComparer.Ordinal) };
    }

    public LauncherLibrary ClearRecent() => this with { Recent = [], Played = Played.Clear() };

    public static LauncherLibrary Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The launcher library is too large.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a launcher library object.");
        var recent = ReadValue(root, "recent", LibraryJson.Default.ImmutableArrayString, ImmutableArray<string>.Empty);
        var played = TryRead(() => ReadValue(root, "played", LibraryJson.Default.DictionaryStringDouble,
            new Dictionary<string, double>()).Where(pair => recent.Contains(pair.Key)).ToImmutableDictionary(pair => pair.Key,
                pair => ToDate(pair.Value)!.Value, StringComparer.Ordinal), ImmutableDictionary<string, DateTimeOffset>.Empty);
        var cache = ReadValue(root, "cache", LibraryJson.Default.ImmutableArrayLibraryServer, ImmutableArray<LibraryServer>.Empty);
        var dispatch = ReadValue(root, "dispatches", LibraryJson.Default.ImmutableArrayLibraryDispatch, ImmutableArray<LibraryDispatch>.Empty);
        return new()
        {
            Favorites = ReadValue(root, "favorites", LibraryJson.Default.ImmutableArrayString, ImmutableArray<string>.Empty)
                .ToImmutableHashSet(StringComparer.Ordinal),
            Recent = recent,
            Cache = [.. cache.Select(item => item?.ToServer() ?? throw new JsonException("Invalid server cache record."))],
            CachedAt = Date(root, "cachedAt"),
            Dispatches = [.. dispatch.Take(5).Select(item => item?.ToRelease() ?? throw new JsonException("Invalid release record."))],
            DispatchesCheckedAt = Date(root, "dispatchesCheckedAt"),
            News = TryRead(() => ToNews(ReadValue(root, "news", LibraryJson.Default.ImmutableArrayLibraryDispatch,
                ImmutableArray<LibraryDispatch>.Empty)), ImmutableArray<NewsDispatch>.Empty),
            NewsNext = TryRead(() => ReadValue(root, "newsNext", LibraryJson.Default.LibraryDispatch, (LibraryDispatch?)null)?.ToNews(),
                null),
            Releases = TryRead(() => ReadValue(root, "releases", LibraryJson.Default.ImmutableArrayReleaseDetails,
                ImmutableArray<ReleaseDetails>.Empty).Where(item => item is { Version: not null, Notes.IsDefault: false } &&
                    item.Notes.All(note => note is { Title: not null, Tag: not null, Detail: not null })).Take(2).ToImmutableArray(),
                ImmutableArray<ReleaseDetails>.Empty),
            NotifiedUpdates = TryRead(() => ReadValue(root, "notifiedUpdates", LibraryJson.Default.ImmutableArrayString,
                ImmutableArray<string>.Empty).Where(UpdateNotification.IsVersion).Distinct(StringComparer.Ordinal).ToImmutableArray(),
                ImmutableArray<string>.Empty),
            Played = played
        };
    }

    public ImmutableArray<byte> Write() => [.. JsonSerializer.SerializeToUtf8Bytes(new LibraryState(
        [.. Favorites.Order(StringComparer.Ordinal)], Recent, [.. Cache.Select(LibraryServer.From)], Number(CachedAt),
        [.. Dispatches.Take(5).Select(LibraryDispatch.From)], Number(DispatchesCheckedAt),
        [.. News.Take(12).Select(LibraryDispatch.From)], NewsNext is { } next ? LibraryDispatch.From(next) : null,
        Played.ToDictionary(pair => pair.Key, pair => Number(pair.Value)!.Value, StringComparer.Ordinal), Releases, NotifiedUpdates),
        LibraryJson.Default.LibraryState)];

    public static async ValueTask<LauncherLibrary> LoadAsync(ISettingsFolder files, CancellationToken cancellationToken = default)
    {
        try
        {
            return await files.ReadAsync(FileName, cancellationToken).ConfigureAwait(false) is { } bytes ? Read(bytes.AsMemory()) : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public ValueTask SaveAsync(ISettingsFolder files, CancellationToken cancellationToken = default) =>
        files.WriteAsync(FileName, Write(), cancellationToken);

    private static T ReadValue<T>(JsonElement root, string key, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, T fallback) =>
        root.TryGetProperty(key, out var item) && item.ValueKind != JsonValueKind.Null
            ? JsonSerializer.Deserialize(item, type) ?? fallback : fallback;

    private static DateTimeOffset? Date(JsonElement root, string key) =>
        root.TryGetProperty(key, out var item) && item.ValueKind != JsonValueKind.Null ? ToDate(item.GetDouble()) : null;

    private static ImmutableArray<NewsDispatch> ToNews(ImmutableArray<LibraryDispatch> rows) =>
        [.. rows.Take(12).Select(item => item?.ToNews() ?? throw new JsonException("Invalid news record."))];

    private static T TryRead<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return fallback; }
    }

    internal static double? Number(DateTimeOffset? date) => date is { } value ? (value - SwiftEpoch).TotalSeconds : null;

    internal static DateTimeOffset? ToDate(double? number)
    {
        if (number is null)
            return null;
        try { return SwiftEpoch.AddSeconds(number.Value); }
        catch (ArgumentOutOfRangeException error) { throw new JsonException("Invalid library timestamp.", error); }
    }
}

internal sealed record LibraryServer(string Address, ImmutableDictionary<string, string> Fields, int Ping,
    ImmutableArray<ServerPlayer> Players, double? ObservedAt, ImmutableArray<string>? QueryConflicts)
{
    public static LibraryServer From(GameServer item) => new(item.Address, item.Fields, item.Ping, item.Players,
        LauncherLibrary.Number(item.ObservedAt), item.QueryConflicts.IsDefault ? null : item.QueryConflicts);

    public GameServer ToServer()
    {
        if (Address is null || Fields is null || Players.IsDefault || Fields.Values.Any(value => value is null) ||
            Players.Any(player => player is null || player.Name is null))
            throw new JsonException("Invalid server cache entry.");
        return new(Address, Fields, Ping, Players, LauncherLibrary.ToDate(ObservedAt), QueryConflicts ?? []);
    }
}
internal sealed record LibraryDispatch(string Stamp, string Title, string Summary, string? Link,
    double? Date, string? Version, string? Open)
{
    public static LibraryDispatch From(ReleaseDispatch item) =>
        new(item.Stamp, item.Title, item.Summary, item.Link, LauncherLibrary.Number(item.Date), item.Version, null);

    public static LibraryDispatch From(NewsDispatch item) =>
        new(item.Stamp, item.Title, item.Summary, item.Link, LauncherLibrary.Number(item.Date), item.Version, item.Open);

    public ReleaseDispatch ToRelease()
    {
        Check();
        return new(Stamp, Title, Summary, Link, LauncherLibrary.ToDate(Date), Version);
    }

    public NewsDispatch ToNews()
    {
        Check();
        return new(Stamp, Title, Summary, Link, LauncherLibrary.ToDate(Date), Version, Open);
    }

    private void Check()
    {
        if (Stamp is null || Title is null || Summary is null)
            throw new JsonException("Invalid dispatch entry.");
    }
}
internal sealed record LibraryState(ImmutableArray<string> Favorites, ImmutableArray<string> Recent,
    ImmutableArray<LibraryServer> Cache, double? CachedAt, ImmutableArray<LibraryDispatch> Dispatches,
    double? DispatchesCheckedAt, ImmutableArray<LibraryDispatch> News, LibraryDispatch? NewsNext,
    Dictionary<string, double> Played, ImmutableArray<ReleaseDetails> Releases, ImmutableArray<string> NotifiedUpdates);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LibraryState))]
[JsonSerializable(typeof(LibraryDispatch))]
internal sealed partial class LibraryJson : JsonSerializerContext;
