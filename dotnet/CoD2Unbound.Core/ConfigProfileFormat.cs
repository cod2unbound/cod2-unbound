using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CoD2Unbound.Core;

internal static class ConfigProfileFormat
{
    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static ConfigStudioMetadata Metadata(ImmutableArray<byte>? bytes, string id)
    {
        if (bytes is not { } data)
            return new(id);
        var record = Read(data, ProfileJson.Default.MetadataFile, ConfigProfileFailure.InvalidMetadata);
        if (record.DisplayName is null || record.Locks is null || record.Locks.Any(value => value is null) || record.Notes is null ||
            record.Notes.Values.Any(value => value is null) || record.LastImportedValues is null ||
            record.LastImportedValues.Values.Any(value => value is null) || record.Origin is null)
            throw new ConfigProfileException(ConfigProfileFailure.InvalidMetadata);
        var origin = record.Origin.Kind switch
        {
            "created" => ConfigOriginKind.Created,
            "copied" => ConfigOriginKind.Copied,
            "imported" => ConfigOriginKind.Imported,
            "migration" => ConfigOriginKind.Migration,
            _ => throw new ConfigProfileException(ConfigProfileFailure.InvalidMetadata)
        };
        var metadata = new ConfigStudioMetadata(record.DisplayName)
        {
            SchemaVersion = record.SchemaVersion,
            Locks = record.Locks.ToImmutableHashSet(StringComparer.Ordinal),
            Notes = record.Notes.ToImmutableDictionary(StringComparer.Ordinal),
            Dpi = record.Dpi,
            LastImportedValues = record.LastImportedValues.ToImmutableDictionary(StringComparer.Ordinal),
            Origin = new(origin, record.Origin.Source, record.Origin.Credit),
            ChainDefinitions = Optional(record.ChainDefinitions)
        };
        metadata.Validate();
        return metadata;
    }

    internal static ImmutableArray<byte> Encode(ConfigStudioMetadata metadata)
    {
        metadata.Validate();
        return Encode(new MetadataFile(metadata.SchemaVersion, metadata.DisplayName, metadata.Locks.Order(StringComparer.Ordinal).ToArray(),
            new(metadata.Notes, StringComparer.Ordinal), new(metadata.Origin.Kind.ToString().ToLowerInvariant(),
                metadata.Origin.Source, metadata.Origin.Credit), metadata.Dpi, new(metadata.LastImportedValues, StringComparer.Ordinal),
            metadata.ChainDefinitions?.ToArray()), ProfileJson.Default.MetadataFile);
    }

    internal static ConfigHistoryEntry History(ImmutableArray<byte> bytes) =>
        FromRecord(Read(bytes, ProfileJson.Default.HistoryFile, ConfigProfileFailure.InvalidHistory));

    internal static ImmutableArray<byte> Encode(ConfigHistoryEntry entry) => Encode(ToRecord(entry), ProfileJson.Default.HistoryFile);

    internal static ConfigProfileTransaction Transaction(ImmutableArray<byte> bytes)
    {
        var record = Read(bytes, ProfileJson.Default.TransactionFile, ConfigProfileFailure.InvalidHistory);
        if (record.SchemaVersion != 1 || record.Entry is null || record.Before is null || record.After is null)
            throw InvalidHistory();
        var entry = FromRecord(record.Entry);
        var before = FromRecord(record.Before);
        var after = FromRecord(record.After);
        if (!entry.Before.Matches(before) || !entry.After.Matches(after))
            throw InvalidHistory();
        return new(before, after, entry);
    }

    internal static ImmutableArray<byte> Encode(ConfigProfileTransaction transaction) =>
        Encode(new TransactionFile(1, ToRecord(transaction.Before), ToRecord(transaction.After), ToRecord(transaction.Entry)),
            ProfileJson.Default.TransactionFile);

    internal static ConfigCompanionCreation Companion(ImmutableArray<byte> bytes)
    {
        var record = Read(bytes, ProfileJson.Default.CompanionFile, ConfigProfileFailure.InvalidHistory);
        if (record.SchemaVersion != 1 || record.ProfileId is null || record.Version is null || record.Entry is null)
            throw InvalidHistory();
        var version = FromRecord(record.Version);
        var entry = FromRecord(record.Entry);
        if (version.Chain is null || !entry.Before.Matches(version) || !entry.After.Matches(version))
            throw InvalidHistory();
        return new(record.ProfileId, version, entry);
    }

    internal static ImmutableArray<byte> Encode(ConfigCompanionCreation companion) =>
        Encode(new CompanionFile(1, companion.ProfileId, ToRecord(companion.Version), ToRecord(companion.Entry)),
            ProfileJson.Default.CompanionFile);

    internal static ConfigMigrationRecord Migration(ImmutableArray<byte> bytes)
    {
        var record = Read(bytes, ProfileJson.Default.MigrationFile, ConfigProfileFailure.InvalidMigration);
        if (record.SchemaVersion != 1 || record.Source is null || record.Original is null || record.TargetId is null ||
            record.Target is not { Config: not null, Metadata: not null, Chain: null } || record.Completed is null)
            throw new ConfigProfileException(ConfigProfileFailure.InvalidMigration);
        var source = record.Source switch
        {
            { EmptyHome: not null, FlattenedLegacy: null, Profile: null } => (ConfigMigrationSource)new ConfigMigrationSource.EmptyHome(),
            { EmptyHome: null, FlattenedLegacy: not null, Profile: null } => new ConfigMigrationSource.FlattenedLegacy(),
            { EmptyHome: null, FlattenedLegacy: null, Profile.Id: not null } => new ConfigMigrationSource.Profile(record.Source.Profile.Id),
            _ => throw new ConfigProfileException(ConfigProfileFailure.InvalidMigration)
        };
        return new(source, ImmutableArray.Create(record.Original), record.TargetId, Optional(record.ExpectedActive),
            FromRecord(record.Target), record.Completed.Value);
    }

    internal static ImmutableArray<byte> Encode(ConfigMigrationRecord migration)
    {
        var source = migration.Source switch
        {
            ConfigMigrationSource.EmptyHome => new MigrationSourceFile(EmptyHome: new()),
            ConfigMigrationSource.FlattenedLegacy => new MigrationSourceFile(FlattenedLegacy: new()),
            ConfigMigrationSource.Profile profile => new MigrationSourceFile(Profile: new(profile.Id)),
            _ => throw new ConfigProfileException(ConfigProfileFailure.InvalidMigration)
        };
        return Encode(new MigrationFile(1, source, migration.Original.ToArray(), migration.TargetId,
            migration.ExpectedActive?.ToArray(), ToRecord(migration.Target), migration.Completed), ProfileJson.Default.MigrationFile);
    }

    internal static bool SameHistory(ConfigHistoryEntry left, ConfigHistoryEntry right) =>
        left.Id == right.Id && left.Date == right.Date && left.Source == right.Source && left.Summary == right.Summary &&
        left.Before.Matches(right.Before) && left.After.Matches(right.After);

    internal static bool ValidHistoryId(string id) => Guid.TryParseExact(id, "D", out _);

    private static HistoryFile ToRecord(ConfigHistoryEntry entry) => new(entry.Id,
        (entry.Date - ReferenceDate).TotalSeconds, entry.Source == ConfigHistorySource.GameSession ? "gameSession" :
            entry.Source.ToString().ToLowerInvariant(), entry.Summary, entry.Before.Config.ToArray(), entry.After.Config.ToArray(),
        entry.Before.Metadata?.ToArray(), entry.After.Metadata?.ToArray(), entry.Before.Chain?.ToArray(), entry.After.Chain?.ToArray());

    private static ConfigHistoryEntry FromRecord(HistoryFile file)
    {
        if (file.Id is null || !ValidHistoryId(file.Id) || file.Date is not { } seconds || !double.IsFinite(seconds) ||
            file.Summary is null || file.BeforeData is null || file.Data is null)
            throw InvalidHistory();
        var source = file.Source switch
        {
            "you" => ConfigHistorySource.You,
            "import" => ConfigHistorySource.Import,
            "checkup" => ConfigHistorySource.Checkup,
            "gameSession" => ConfigHistorySource.GameSession,
            "restore" => ConfigHistorySource.Restore,
            "migration" => ConfigHistorySource.Migration,
            _ => throw InvalidHistory()
        };
        DateTimeOffset date;
        try { date = ReferenceDate.AddSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { throw InvalidHistory(); }
        return new(file.Id, date, source, file.Summary,
            new(ImmutableArray.Create(file.BeforeData), Optional(file.BeforeMetadata), Optional(file.BeforeChain)),
            new(ImmutableArray.Create(file.Data), Optional(file.Metadata), Optional(file.Chain)));
    }

    private static VersionFile ToRecord(ConfigProfileVersion version) =>
        new(version.Config.ToArray(), version.Metadata?.ToArray(), version.Chain?.ToArray());

    private static ConfigProfileVersion FromRecord(VersionFile version) =>
        version.Config is { } config ? new(ImmutableArray.Create(config), Optional(version.Metadata), Optional(version.Chain)) :
            throw InvalidHistory();

    private static ImmutableArray<byte>? Optional(byte[]? bytes) => bytes is null ? null : ImmutableArray.Create(bytes);

    private static T Read<T>(ImmutableArray<byte> data, JsonTypeInfo<T> type, ConfigProfileFailure failure)
    {
        try { return JsonSerializer.Deserialize(data.AsSpan(), type) ?? throw new ConfigProfileException(failure); }
        catch (JsonException) { throw new ConfigProfileException(failure); }
    }

    private static ImmutableArray<byte> Encode<T>(T value, JsonTypeInfo<T> type) =>
        ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(value, type));

    private static ConfigProfileException InvalidHistory() => new(ConfigProfileFailure.InvalidHistory);
}

internal sealed record ConfigProfileTransaction(ConfigProfileVersion Before, ConfigProfileVersion After, ConfigHistoryEntry Entry);
internal sealed record ConfigCompanionCreation(string ProfileId, ConfigProfileVersion Version, ConfigHistoryEntry Entry);
internal sealed record MetadataFile(int SchemaVersion, string? DisplayName, string[]? Locks, SortedDictionary<string, string>? Notes,
    OriginFile? Origin, double? Dpi, SortedDictionary<string, string>? LastImportedValues, byte[]? ChainDefinitions);
internal sealed record OriginFile(string? Kind, string? Source, string? Credit);
internal sealed record HistoryFile(string? Id, double? Date, string? Source, string? Summary, byte[]? BeforeData, byte[]? Data,
    byte[]? BeforeMetadata, byte[]? Metadata, byte[]? BeforeChain, byte[]? Chain);
internal sealed record VersionFile(byte[]? Config, byte[]? Metadata, byte[]? Chain);
internal sealed record TransactionFile(int SchemaVersion, VersionFile? Before, VersionFile? After, HistoryFile? Entry);
internal sealed record CompanionFile(int SchemaVersion, [property: JsonPropertyName("profileID")] string? ProfileId,
    VersionFile? Version, HistoryFile? Entry);

internal sealed record MigrationFile(int SchemaVersion, MigrationSourceFile? Source, byte[]? Original,
    [property: JsonPropertyName("targetID")] string? TargetId, byte[]? ExpectedActive, VersionFile? Target, bool? Completed);
internal sealed record MigrationSourceFile(EmptyMigrationCase? EmptyHome = null, EmptyMigrationCase? FlattenedLegacy = null,
    MigrationProfileCase? Profile = null);
internal sealed record EmptyMigrationCase;
internal sealed record MigrationProfileCase([property: JsonPropertyName("_0")] string? Id);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MetadataFile))]
[JsonSerializable(typeof(HistoryFile))]
[JsonSerializable(typeof(TransactionFile))]
[JsonSerializable(typeof(CompanionFile))]
[JsonSerializable(typeof(MigrationFile))]
internal sealed partial class ProfileJson : JsonSerializerContext;
