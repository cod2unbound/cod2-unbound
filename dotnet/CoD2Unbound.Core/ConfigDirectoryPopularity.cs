using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace CoD2Unbound.Core;

public enum ConfigSetupOrder { FeaturedFirst, MostImported, MostViewed }

public sealed record ConfigSetupCounts(long? Imports, long? Views)
{
    public long? Figure(ConfigSetupOrder order) => order switch
    {
        ConfigSetupOrder.MostImported => Imports,
        ConfigSetupOrder.MostViewed => Views,
        _ => null
    };
}

public sealed record ConfigDirectoryPopularity(int PeriodDays, string GeneratedDate,
    ImmutableDictionary<string, ConfigSetupCounts> Setups)
{
    public string Period => PeriodDays == 1 ? "day" : $"{PeriodDays} days";
}

public sealed partial class ConfigDirectory
{
    private const int MaximumPopularityBytes = 262_144;
    private const int PopularityCacheLimit = MaximumPopularityBytes * 2;

    public async ValueTask<ConfigDirectoryPopularity?> LoadPopularityAsync(CancellationToken cancellationToken = default)
    {
        if (configuration is not { } trust)
            return null;

        var root = ConfigFetchRequest.Parent(trust.IndexUrl);
        var url = new Uri(root, "popularity.json");
        var signatureUrl = new Uri(root, "popularity.sig");
        ConfigTextResponse data;
        ConfigTextResponse signature;
        ConfigDirectoryPopularity? verified;
        try
        {
            data = await FetchAsync(url, MaximumPopularityBytes, cancellationToken).ConfigureAwait(false);
            signature = await FetchAsync(signatureUrl, Ed25519.SignatureSize, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            verified = VerifyPopularity(trust, data.Data, signature.Data);
        }
        catch (Exception error) when (error is IOException or ConfigDirectoryException or UnauthorizedAccessException)
        {
            return await ReadOfflineAsync().ConfigureAwait(false);
        }

        if (verified is null)
            return await ReadOfflineAsync().ConfigureAwait(false);
        try
        {
            await using var lease = await storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var cached = await ReadVerifiedCacheAsync().ConfigureAwait(false);
            if (cached is not null && string.CompareOrdinal(cached.GeneratedDate, verified.GeneratedDate) > 0)
                return cached;
            var record = new ConfigDirectoryCacheRecord(1, url, signatureUrl, trust.PublicKey.ToArray(),
                data.Data.ToArray(), signature.Data.ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, ConfigDirectoryJson.Default.ConfigDirectoryCacheRecord);
            await storage.WriteAsync(ConfigDirectoryFile.Popularity, ImmutableArray.Create(bytes), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ConfigDirectoryException or UnauthorizedAccessException)
        {
            // Verified network figures still work without a writable cache.
        }
        return verified;

        async ValueTask<ConfigDirectoryPopularity?> ReadOfflineAsync()
        {
            try
            {
                await using var lease = await storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
                return await ReadVerifiedCacheAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ConfigDirectoryException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        async ValueTask<ConfigDirectoryPopularity?> ReadVerifiedCacheAsync()
        {
            var raw = await storage.ReadAsync(ConfigDirectoryFile.Popularity, PopularityCacheLimit, cancellationToken).ConfigureAwait(false);
            if (raw is not { } bytes)
                return null;
            ConfigDirectoryCacheRecord? record;
            try { record = ReadRecord(bytes, PopularityCacheLimit, ConfigDirectoryJson.Default.ConfigDirectoryCacheRecord); }
            catch (ConfigDirectoryException error) when (error.Failure == ConfigDirectoryFailure.InvalidCache) { return null; }
            if (record is not { SchemaVersion: 1, IndexUrl: not null, SignatureUrl: not null, PublicKey: not null, Index: not null, Signature: not null } ||
                !ConfigFetchRequest.SameResource(url, record.IndexUrl) || !ConfigFetchRequest.SameResource(signatureUrl, record.SignatureUrl) ||
                !trust.PublicKey.AsSpan().SequenceEqual(record.PublicKey))
                return null;
            return VerifyPopularity(trust, ImmutableArray.Create(record.Index), ImmutableArray.Create(record.Signature));
        }
    }

    private static ConfigDirectoryPopularity? VerifyPopularity(ConfigDirectoryConfiguration trust, ImmutableArray<byte> bytes,
        ImmutableArray<byte> signature)
    {
        if (bytes.IsDefault || bytes.Length > MaximumPopularityBytes || signature.IsDefault || signature.Length != Ed25519.SignatureSize ||
            !trust.Verifier.Verify(Ed25519.Algorithm.Ed25519, null, bytes.AsSpan(), signature.AsSpan()))
            return null;

        ConfigPopularityPublication? payload;
        try { payload = JsonSerializer.Deserialize(bytes.AsSpan(), ConfigDirectoryJson.Default.ConfigPopularityPublication); }
        catch (JsonException) { return null; }
        if (payload is not { SchemaVersion: 1, PeriodDays: >= 1 and <= 366, GeneratedDate.Length: 10, Setups: not null } ||
            !DateOnly.TryParseExact(payload.GeneratedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            payload.Setups.Count > MaximumEntries)
            return null;

        const long maximumCount = 9_007_199_254_740_991;
        var setups = ImmutableDictionary.CreateBuilder<string, ConfigSetupCounts>(StringComparer.Ordinal);
        foreach (var (id, counts) in payload.Setups)
        {
            if (!ConfigDirectoryIndexReader.ValidId(id) || counts is null || counts.Imports > maximumCount || counts.Views > maximumCount)
                return null;
            long? imports = counts.Imports >= 5 ? counts.Imports : null;
            long? views = counts.Views >= 5 ? counts.Views : null;
            if (imports is not null || views is not null)
                setups.Add(id, new(imports, views));
        }
        return new(payload.PeriodDays, payload.GeneratedDate, setups.ToImmutable());
    }
}

internal sealed record ConfigPopularityPublication(int SchemaVersion, int PeriodDays, string GeneratedDate,
    Dictionary<string, ConfigSetupCounts> Setups);
