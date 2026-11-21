using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public enum ConfigProfileFailure { Conflict, GameRunning, InvalidMetadata, CannotPublish }

public sealed class ConfigProfileException(ConfigProfileFailure failure) : IOException(failure switch
{
    ConfigProfileFailure.Conflict => "The config, metadata or selection changed on disk. Refresh before saving.",
    ConfigProfileFailure.GameRunning => "Quit the game before changing loadouts.",
    ConfigProfileFailure.InvalidMetadata => "This loadout's metadata is invalid or uses an unsupported version.",
    ConfigProfileFailure.CannotPublish => "Cannot publish this loadout.",
    _ => throw new ArgumentOutOfRangeException(nameof(failure))
})
{
    public ConfigProfileFailure Failure { get; } = failure;
}

public enum ConfigOriginKind { Created, Copied, Imported, Migration }
public sealed record ConfigOrigin(ConfigOriginKind Kind = ConfigOriginKind.Created, string? Source = null, string? Credit = null);

public sealed record ConfigStudioMetadata(string DisplayName)
{
    public int SchemaVersion { get; init; } = 1;
    public ImmutableHashSet<string> Locks { get; init; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    public ImmutableDictionary<string, string> Notes { get; init; } = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);
    public ConfigOrigin Origin { get; init; } = new();
    public double? Dpi { get; init; }
    public ImmutableDictionary<string, string> LastImportedValues { get; init; } =
        ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);
    public ImmutableArray<byte>? ChainDefinitions { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(DisplayName) ||
            (Dpi is { } dpi && (!double.IsFinite(dpi) || dpi <= 0)))
            throw new ConfigProfileException(ConfigProfileFailure.InvalidMetadata);
    }
}

public sealed record ConfigProfile(string Id, string DisplayName, bool IsActive);

public sealed record ConfigProfileVersion(ImmutableArray<byte> Config, ImmutableArray<byte>? Metadata, ImmutableArray<byte>? Chain = null)
{
    public bool Matches(ConfigProfileVersion? other) => other is not null &&
        Config.AsSpan().SequenceEqual(other.Config.AsSpan()) &&
        EqualOptional(Metadata, other.Metadata) && EqualOptional(Chain, other.Chain);

    private static bool EqualOptional(ImmutableArray<byte>? left, ImmutableArray<byte>? right) =>
        left.HasValue == right.HasValue && (left is not { } bytes || bytes.AsSpan().SequenceEqual(right!.Value.AsSpan()));
}

public sealed record ConfigProfileRead(ConfigProfile Profile, ConfigDocument Document, ConfigStudioMetadata Metadata,
    ConfigProfileVersion Version);

public enum ConfigHistorySource { You, Import, Checkup, GameSession, Restore, Migration }
public sealed record ConfigHistoryEntry(string Id, DateTimeOffset Date, ConfigHistorySource Source, string Summary,
    ConfigProfileVersion Before, ConfigProfileVersion After);
