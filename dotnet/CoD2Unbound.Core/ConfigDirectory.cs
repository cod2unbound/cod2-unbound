using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace CoD2Unbound.Core;

public enum ConfigDirectoryFailure
{
    InvalidConfiguration, InvalidSignature, InvalidIndex, InvalidCache, UnsafeCache, IdentityMismatch,
    ProfileMismatch, MissingEntry, Rollback
}

public sealed class ConfigDirectoryException(ConfigDirectoryFailure failure, Exception? innerException = null)
    : Exception(failure switch
    {
        ConfigDirectoryFailure.InvalidConfiguration => "The directory has no valid publication or trust configuration.",
        ConfigDirectoryFailure.InvalidSignature => "The directory signature could not be verified.",
        ConfigDirectoryFailure.InvalidIndex => "The directory index has invalid metadata or an unsupported version.",
        ConfigDirectoryFailure.InvalidCache => "The cached directory could not be verified.",
        ConfigDirectoryFailure.UnsafeCache => "The directory cache path is not a regular private cache file.",
        ConfigDirectoryFailure.IdentityMismatch => "The downloaded file is not the selected directory resource.",
        ConfigDirectoryFailure.ProfileMismatch => "The config bytes differ from the signed directory entry.",
        ConfigDirectoryFailure.MissingEntry => "This entry is not in the verified directory.",
        ConfigDirectoryFailure.Rollback => "The directory response is older than the verified cache, or changes an existing revision.",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    }, innerException)
{
    public ConfigDirectoryFailure Failure { get; } = failure;
}

public sealed class ConfigDirectoryConfiguration
{
    public Uri IndexUrl { get; }
    public Uri SignatureUrl { get; }
    public ImmutableArray<byte> PublicKey { get; }
    internal Ed25519PublicKeyParameters Verifier { get; }

    public ConfigDirectoryConfiguration(Uri indexUrl, Uri signatureUrl, ImmutableArray<byte> publicKey)
    {
        if (!ConfigDirectoryIndexReader.PublicUrl(indexUrl) || !ConfigDirectoryIndexReader.PublicUrl(signatureUrl) ||
            ConfigFetchRequest.SameResource(indexUrl, signatureUrl) ||
            ConfigFetchRequest.Parent(indexUrl).AbsoluteUri != ConfigFetchRequest.Parent(signatureUrl).AbsoluteUri ||
            publicKey.IsDefault || publicKey.Length != Ed25519.PublicKeySize)
            throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidConfiguration);
        try { Verifier = new(publicKey.AsSpan()); }
        catch (ArgumentException error) { throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidConfiguration, error); }

        IndexUrl = indexUrl;
        SignatureUrl = signatureUrl;
        PublicKey = publicKey;
    }

    internal bool Matches(Uri indexUrl, Uri signatureUrl, ReadOnlySpan<byte> publicKey) =>
        ConfigFetchRequest.SameResource(IndexUrl, indexUrl) && ConfigFetchRequest.SameResource(SignatureUrl, signatureUrl) &&
        PublicKey.AsSpan().SequenceEqual(publicKey);
}

public enum ConfigDirectoryOrigin { Network, Cache }
public enum ConfigDirectoryFile { Cache, Saved, Popularity }

public interface IConfigDirectoryStorage
{
    // The lease covers reads, deletes and atomic publications, across processes and instances for this cache.
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
    // Return null for an absent file and at most limit + 1 bytes so the core can detect an oversized record.
    // Report unsafe paths as UnsafeCache, and leave them untouched.
    ValueTask<ImmutableArray<byte>?> ReadAsync(ConfigDirectoryFile file, int limit, CancellationToken cancellationToken);
    ValueTask<DateTimeOffset?> ModifiedAsync(ConfigDirectoryFile file, CancellationToken cancellationToken);
    ValueTask WriteAsync(ConfigDirectoryFile file, ImmutableArray<byte> data, CancellationToken cancellationToken);
    ValueTask DeleteAsync(ConfigDirectoryFile file, CancellationToken cancellationToken);
}

public sealed class ConfigDirectorySnapshot
{
    internal ConfigDirectorySnapshot(ConfigDirectoryIndex index, ConfigDirectoryOrigin origin, ConfigDirectoryConfiguration trust,
        ImmutableArray<byte> bytes, ImmutableArray<byte> signature)
    {
        Index = index;
        Origin = origin;
        Trust = trust;
        Bytes = bytes;
        Signature = signature;
    }

    public ConfigDirectoryIndex Index { get; }
    public ConfigDirectoryOrigin Origin { get; }
    internal ConfigDirectoryConfiguration Trust { get; }
    internal ImmutableArray<byte> Bytes { get; }
    internal ImmutableArray<byte> Signature { get; }
}

public sealed record ConfigDirectoryLoad(ConfigDirectorySnapshot? Snapshot, string? UnavailableReason = null, string? RefreshError = null);
public sealed record ConfigDirectorySavedEntry(string Id, ConfigDirectoryEntry? Entry);

public sealed partial class ConfigDirectory(ConfigDirectoryConfiguration? configuration, IConfigDirectoryStorage storage, IConfigTextFetcher fetcher)
{
    public const int MaximumIndexBytes = 1_048_576;
    internal const int MaximumEntries = 2048;
    private const int CacheLimit = MaximumIndexBytes * 2;
    private const int SavedLimit = 262_144;
    private static readonly Uri ProductionRoot = new("https://raw.githubusercontent.com/cod2unbound/cod2-configs/main/");
    public static Uri RepositoryUrl { get; } = new("https://github.com/cod2unbound/cod2-configs");
    public static Uri RemovalUrl { get; } = new("https://github.com/cod2unbound/cod2-configs/issues/new");
    public static ConfigDirectoryConfiguration Production { get; } = new(
        new Uri(ProductionRoot, "index.json"), new Uri(ProductionRoot, "index.sig"),
        ImmutableArray.Create(Convert.FromBase64String("iTOphG+NSxzT4k9BumMTP7h3Ui408zuH63b8S+3klSs=")));

    public async ValueTask<ConfigDirectoryLoad> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (configuration is not { } trust)
            return new(null, "Player setups aren’t available in this build.");

        ConfigDirectorySnapshot? cached;
        await using (await storage.AcquireAsync(cancellationToken).ConfigureAwait(false))
            cached = await ReadCacheOrDiscardAsync(trust, cancellationToken).ConfigureAwait(false);

        ConfigTextResponse index;
        ConfigTextResponse signature;
        try
        {
            index = await FetchAsync(trust.IndexUrl, MaximumIndexBytes, cancellationToken).ConfigureAwait(false);
            signature = await FetchAsync(trust.SignatureUrl, Ed25519.SignatureSize, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException error) when (error is ConfigFetchUnavailableException ||
            error is ConfigFetchHttpException http && (http.StatusCode >= 500 || cached is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(cached, cached is null ? "The directory is unavailable and no verified offline copy exists." : null,
                error.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = Verify(trust, index.Data, signature.Data, ConfigDirectoryOrigin.Network);
        await using (await storage.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            // Recheck the current cache after I/O. A concurrent refresh may have advanced it.
            var latest = await ReadCacheOrDiscardAsync(trust, cancellationToken).ConfigureAwait(false);
            if (latest is not null && (result.Index.Revision < latest.Index.Revision ||
                (result.Index.Revision == latest.Index.Revision && !result.Bytes.AsSpan().SequenceEqual(latest.Bytes.AsSpan()))))
                throw new ConfigDirectoryException(ConfigDirectoryFailure.Rollback);

            var record = new ConfigDirectoryCacheRecord(1, trust.IndexUrl, trust.SignatureUrl, trust.PublicKey.ToArray(),
                result.Bytes.ToArray(), result.Signature.ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, ConfigDirectoryJson.Default.ConfigDirectoryCacheRecord);
            await storage.WriteAsync(ConfigDirectoryFile.Cache, ImmutableArray.Create(bytes), cancellationToken).ConfigureAwait(false);
        }
        return new(result);
    }

    public async ValueTask<DateTimeOffset?> CacheDateAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await storage.ModifiedAsync(ConfigDirectoryFile.Cache, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConfigDirectorySnapshot?> ReadCachedSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (configuration is not { } trust)
            return null;
        await using var lease = await storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCacheOrDiscardAsync(trust, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConfigImportBundle> LoadProfileAsync(string entryId, ConfigDirectorySnapshot snapshot,
        ConfigDialect dialect, IConfigImportCatalog catalog, CancellationToken cancellationToken = default)
    {
        var verified = VerifySnapshot(snapshot);
        var entry = verified.Index.Entries.FirstOrDefault(entry => entry.Id == entryId) ??
            throw new ConfigDirectoryException(ConfigDirectoryFailure.MissingEntry);
        var response = await FetchAsync(entry.ConfigUrl, entry.ByteCount, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.Data.IsDefault || response.Data.Length != entry.ByteCount ||
            Convert.ToHexStringLower(SHA256.HashData(response.Data.AsSpan())) != entry.Sha256)
            throw new ConfigDirectoryException(ConfigDirectoryFailure.ProfileMismatch);

        // No unsigned exec targets are fetched. Review sees the same unresolved dependencies as pasted input.
        var bundle = await new ConfigImportLoader(catalog)
            .LoadAsync(new ConfigImportInput.Paste(response.Data), dialect, cancellationToken).ConfigureAwait(false);
        return bundle with { Source = new(ConfigImportSourceKind.Https, entry.SourceUrl,
            ConfigFetchRequest.Parent(entry.ConfigUrl), entry.ConfigUrl) };
    }

    public async ValueTask<ImmutableArray<ConfigDirectorySavedEntry>> SavedAsync(ConfigDirectorySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var verified = VerifySnapshot(snapshot);
        var entries = verified.Index.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        await using var lease = await storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var ids = await ReadSavedAsync(cancellationToken).ConfigureAwait(false);
        return ids.Order(StringComparer.Ordinal).Select(id => new ConfigDirectorySavedEntry(id, entries.GetValueOrDefault(id)))
            .ToImmutableArray();
    }

    public async ValueTask SetSavedAsync(string entryId, bool saved, ConfigDirectorySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var verified = VerifySnapshot(snapshot);
        if (!ConfigDirectoryIndexReader.ValidId(entryId) || saved && !verified.Index.Entries.Any(entry => entry.Id == entryId))
            throw new ConfigDirectoryException(ConfigDirectoryFailure.MissingEntry);

        await using var lease = await storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var ids = await ReadSavedAsync(cancellationToken).ConfigureAwait(false);
        if (saved)
            ids.Add(entryId);
        else
            ids.Remove(entryId);
        if (ids.Count > MaximumEntries)
            throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidCache);

        var record = new ConfigDirectorySavedRecord(1, ids.Order(StringComparer.Ordinal).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, ConfigDirectoryJson.Default.ConfigDirectorySavedRecord);
        await storage.WriteAsync(ConfigDirectoryFile.Saved, ImmutableArray.Create(bytes), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ConfigTextResponse> FetchAsync(Uri url, int limit, CancellationToken cancellationToken)
    {
        var response = await fetcher.FetchAsync(new(url, limit, ConfigFetchRequest.Parent(url)), cancellationToken).ConfigureAwait(false);
        if (!ConfigFetchRequest.SameResource(url, response.FinalUrl))
            throw new ConfigDirectoryException(ConfigDirectoryFailure.IdentityMismatch);
        return response;
    }

    private ConfigDirectorySnapshot VerifySnapshot(ConfigDirectorySnapshot snapshot)
    {
        if (configuration is not { } trust || !trust.Matches(snapshot.Trust.IndexUrl, snapshot.Trust.SignatureUrl,
            snapshot.Trust.PublicKey.AsSpan()))
            throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidConfiguration);

        return Verify(trust, snapshot.Bytes, snapshot.Signature, snapshot.Origin);
    }

    private static ConfigDirectorySnapshot Verify(ConfigDirectoryConfiguration trust, ImmutableArray<byte> bytes,
        ImmutableArray<byte> signature, ConfigDirectoryOrigin origin)
    {
        if (bytes.IsDefault || bytes.Length > MaximumIndexBytes || signature.IsDefault || signature.Length != Ed25519.SignatureSize ||
            !trust.Verifier.Verify(Ed25519.Algorithm.Ed25519, null, bytes.AsSpan(), signature.AsSpan()))
            throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidSignature);

        return new(ConfigDirectoryIndexReader.Read(bytes), origin, trust, bytes, signature);
    }

    private async ValueTask<ConfigDirectorySnapshot?> ReadCacheOrDiscardAsync(ConfigDirectoryConfiguration trust,
        CancellationToken cancellationToken)
    {
        var raw = await storage.ReadAsync(ConfigDirectoryFile.Cache, CacheLimit, cancellationToken).ConfigureAwait(false);
        if (raw is not { } data)
            return null;

        try
        {
            var record = ReadRecord(data, CacheLimit, ConfigDirectoryJson.Default.ConfigDirectoryCacheRecord);
            if (record is not { SchemaVersion: 1, IndexUrl: not null, SignatureUrl: not null, PublicKey: not null,
                Index: not null, Signature: not null })
                throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidCache);
            // Trust is always supplied by the launcher, including after a change of root.
            if (!trust.Matches(record.IndexUrl, record.SignatureUrl, record.PublicKey))
                return null;

            return Verify(trust, ImmutableArray.Create(record.Index), ImmutableArray.Create(record.Signature),
                ConfigDirectoryOrigin.Cache);
        }
        catch (ConfigDirectoryException error) when (error.Failure is ConfigDirectoryFailure.InvalidCache or
            ConfigDirectoryFailure.InvalidIndex or ConfigDirectoryFailure.InvalidSignature)
        {
            await storage.DeleteAsync(ConfigDirectoryFile.Cache, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private async ValueTask<HashSet<string>> ReadSavedAsync(CancellationToken cancellationToken)
    {
        var raw = await storage.ReadAsync(ConfigDirectoryFile.Saved, SavedLimit, cancellationToken).ConfigureAwait(false);
        if (raw is not { } data)
            return new(StringComparer.Ordinal);

        var record = ReadRecord(data, SavedLimit, ConfigDirectoryJson.Default.ConfigDirectorySavedRecord);
        if (record is not { SchemaVersion: 1, Ids: not null } ||
            record.Ids.Any(id => id is null || !ConfigDirectoryIndexReader.ValidId(id)))
            throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidCache);

        var ids = new HashSet<string>(record.Ids, StringComparer.Ordinal);
        if (ids.Count > MaximumEntries)
            throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidCache);
        return ids;
    }

    private static T? ReadRecord<T>(ImmutableArray<byte> bytes, int limit, JsonTypeInfo<T> type)
    {
        if (bytes.IsDefault || bytes.Length > limit)
            throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidCache);
        try { return JsonSerializer.Deserialize(bytes.AsSpan(), type); }
        catch (JsonException error) { throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidCache, error); }
    }
}

internal sealed record ConfigDirectoryCacheRecord(int SchemaVersion,
    [property: JsonPropertyName("indexURL")] Uri IndexUrl, [property: JsonPropertyName("signatureURL")] Uri SignatureUrl,
    byte[] PublicKey, byte[] Index, byte[] Signature);
internal sealed record ConfigDirectorySavedRecord(int SchemaVersion, string[] Ids);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigDirectoryCacheRecord))]
[JsonSerializable(typeof(ConfigDirectorySavedRecord))]
[JsonSerializable(typeof(ConfigPopularityPublication))]
internal sealed partial class ConfigDirectoryJson : JsonSerializerContext;
