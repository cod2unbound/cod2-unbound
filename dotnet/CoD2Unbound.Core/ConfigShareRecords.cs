using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CoD2Unbound.Core;

public sealed record ConfigShareRecord(long Request, Uri Url, string Token, string Sent, string Sha256, string Name)
{
    public override string ToString() => $"ConfigShareRecord {{ Request = {Request}, Url = {Url}, Sent = {Sent} }}";
}

public sealed record ConfigShareRecordFile(int Version, string? Name, ImmutableDictionary<string, ConfigShareRecord> Loadouts)
{
    public ConfigShareRecord? Record(string loadout) => Loadouts.GetValueOrDefault(loadout.ToLowerInvariant());
}

public interface IConfigShareStorage : ISettingsFolder
{
    // Hold an exclusive cross-process lease for a read-modify-write of shared-setups.json.
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}

public sealed class ConfigShareRecords(IConfigShareStorage storage)
{
    public const string Filename = "shared-setups.json";
    private static readonly ConfigShareRecordFile Empty = new(1, null, ImmutableDictionary<string, ConfigShareRecord>.Empty);

    public async ValueTask<ConfigShareRecordFile> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var (file, valid) = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        if (!valid)
            throw new InvalidDataException("The saved shared setup records could not be read.");
        return file;
    }

    public ValueTask RememberAsync(string loadout, ConfigShareRecord record, CancellationToken cancellationToken = default) =>
        ChangeAsync(loadout, record, cancellationToken);

    public ValueTask ForgetAsync(string loadout, CancellationToken cancellationToken = default) =>
        ChangeAsync(loadout, null, cancellationToken);

    public async ValueTask<bool> ForgetIfCurrentAsync(string loadout, ConfigShareRecord expected,
        CancellationToken cancellationToken = default)
    {
        if (!ConfigStudioLaunch.IsProfileId(loadout))
            throw new ArgumentException("The loadout identifier is invalid.", nameof(loadout));
        await using var lease = await storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var (file, valid) = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        if (!valid)
            throw new InvalidDataException("The saved shared setup records could not be read.");
        if (file.Record(loadout) != expected)
            return false;
        await WriteLockedAsync(file with { Loadouts = file.Loadouts.Remove(loadout.ToLowerInvariant()) }, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public static string Day(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async ValueTask ChangeAsync(string loadout, ConfigShareRecord? record, CancellationToken cancellationToken)
    {
        if (!ConfigStudioLaunch.IsProfileId(loadout) || (record is not null && !ValidRecord(record)))
            throw new ArgumentException("The shared setup receipt is invalid.");
        await using var lease = await storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var (file, valid) = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        if (!valid)
            throw new InvalidDataException("The saved shared setup records could not be read.");
        string id = loadout.ToLowerInvariant();
        file = record is null ? file with { Loadouts = file.Loadouts.Remove(id) } :
            file with { Name = record.Name, Loadouts = file.Loadouts.SetItem(id, record) };
        await WriteLockedAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteLockedAsync(ConfigShareRecordFile file, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, ConfigShareJson.Default.ConfigShareRecordFile);
        return storage.WriteAsync(Filename, ImmutableArray.Create(bytes), cancellationToken);
    }

    private async ValueTask<(ConfigShareRecordFile File, bool Valid)> ReadLockedAsync(CancellationToken cancellationToken)
    {
        var bytes = await storage.ReadAsync(Filename, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return (Empty, true);
        try
        {
            var file = JsonSerializer.Deserialize(bytes.Value.AsSpan(), ConfigShareJson.Default.ConfigShareRecordFile);
            if (file is { Version: 1, Loadouts: not null } && file.Loadouts.All(pair =>
                ConfigStudioLaunch.IsProfileId(pair.Key) && pair.Key == pair.Key.ToLowerInvariant() && ValidRecord(pair.Value)))
                return (file, true);
        }
        catch (JsonException) { }
        return (Empty, false);
    }

    private static bool ValidRecord(ConfigShareRecord? record) =>
        record is { Request: > 0, Url.IsAbsoluteUri: true, Name: not null } &&
        ConfigShareClient.PullUrl(record.Url.AbsoluteUri, record.Request) is not null &&
        ConfigShareClient.ValidDigest(record.Token) && ConfigShareClient.ValidDigest(record.Sha256) &&
        DateOnly.TryParseExact(record.Sent, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

public static class ConfigShareCredit
{
    public static string? Line(ConfigDirectoryEntry entry)
    {
        if (entry.Provenance.Attribution != ConfigDirectoryAttribution.SelfPublished ||
            entry.Provenance.PublishedBy != "CoD2 Unbound" || ConfigShareClient.PullUrl(entry.SourceUrl.AbsoluteUri) is null)
            return null;
        string day = entry.Provenance.RetrievedAt;
        string shown = DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")) : day;
        return "Shared by its player from CoD2 Unbound on " + shown;
    }
}
