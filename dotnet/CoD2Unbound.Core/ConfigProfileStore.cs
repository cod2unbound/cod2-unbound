using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CoD2Unbound.Core;

/// <summary>Services scoped to a validated private game home. Names use forward-slash relative paths.</summary>
public interface IConfigProfileFiles
{
    // Hold the cross-process main/players/.studio.lock. Reject reparse escapes and unsafe file slots.
    IDisposable Acquire(CancellationToken cancellationToken);
    ImmutableArray<byte>? Read(string path);
    bool DirectoryExists(string path);
    ImmutableArray<string> ListDirectories(string path);
    ImmutableArray<string> ListFiles(string path);

    // Atomic, durable operations. WriteAtomic also creates private parent directories as needed.
    void WriteAtomic(string path, ImmutableArray<byte> data);
    void DeleteFile(string path);
    // Move a complete private staging directory to an absent target on the same volume.
    void PublishDirectory(string staging, string target);
    // Remove only a launcher-owned staging or verified disposable connection directory without traversing links.
    void DeleteOwnedDirectory(string path);
}

public static class LoadoutName
{
    public static string Identifier(string name, IEnumerable<string> existing)
    {
        var result = new StringBuilder();
        foreach (char character in name.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsAsciiLetterOrDigit(character))
                result.Append(character);
            else if ((character is '.' or '-' or '_' || char.IsWhiteSpace(character)) &&
                result.Length > 0 && result[^1] != '_')
                result.Append(character is '.' or '-' ? character : '_');
        }
        string stem = result.ToString().TrimEnd('.', '-', '_');
        if (stem.Length == 0)
            stem = "Loadout";
        if (stem.Length > 36)
            stem = stem[..36];
        var reserved = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidate = stem;
        for (int suffix = 2; reserved.Contains(candidate) || !MediaName.SafeComponent(candidate); suffix++)
        {
            string tail = FormattableString.Invariant($"_{suffix}");
            candidate = stem[..Math.Min(stem.Length, 41 - tail.Length)] + tail;
        }
        return candidate;
    }

    public static string CopyOf(string name, IEnumerable<string> existing)
    {
        string stem = "Copy of " + name;
        var names = existing.ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        string result = stem;
        for (int suffix = 2; names.Contains(result); suffix++)
            result = FormattableString.Invariant($"{stem} {suffix}");
        return result;
    }

    public static string? Problem(string name, IEnumerable<string> existing, bool readOnly)
    {
        if (readOnly)
            return "Configs are read-only while Call of Duty 2 is running.";
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "Type a name for the loadout.";
        return existing.Any(value => CultureInfo.InvariantCulture.CompareInfo.Compare(value, trimmed, CompareOptions.IgnoreCase) == 0)
            ? $"You already have a loadout called “{trimmed}”." : null;
    }
}

public sealed record ConfigActiveSelection(string? ProfileId, ImmutableArray<byte>? OriginalBytes);
public sealed record ConfigTransactionRecovery(string ProfileId, ConfigProfileVersion Current, ImmutableArray<byte> Journal);
public enum ConfigSnapshotRevision { Before, After }

/// <summary>Loadout state and journal policy for the shared engine filesystem layout.</summary>
public sealed partial class ConfigProfileStore(
    IConfigProfileFiles files, ConfigDialect dialect, Func<bool>? gameIsRunning = null, Func<bool>? hasDraft = null,
    TimeProvider? timeProvider = null) : IConfigImportStore
{
    private const string Players = "main/players";
    private const string Active = Players + "/active.txt";
    private const string ConfigName = "/config_mp.cfg";
    private const string MetadataName = "/studio.json";
    private const string JournalName = "/.studio-transaction.json";
    private const string HistoryName = "/history";
    public const int HistoryRetention = 50;

    public ConfigDialect Dialect { get; } = dialect;

    public ImmutableArray<ConfigProfile> List() => List(null);

    internal ImmutableArray<ConfigProfile> List(Action<string, Exception>? onUnreadable)
    {
        using var lease = files.Acquire(default);
        var active = Selection().ProfileId;
        var result = ImmutableArray.CreateBuilder<ConfigProfile>();
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        foreach (string id in files.ListDirectories(Players).Where(ValidIdentifier).Order(comparer))
        {
            string directory = DirectoryName(id);
            ConfigProfile? description = null;
            try
            {
                if (onUnreadable is not null)
                    description = ToProfile(id, ConfigProfileFormat.Metadata(files.Read(directory + MetadataName), id), active);
                Recover(id, directory);
                if (files.Read(directory + ConfigName) is not null)
                    result.Add(ToProfile(id, ConfigProfileFormat.Metadata(files.Read(directory + MetadataName), id), active));
            }
            catch (Exception error) when (onUnreadable is not null && error is IOException or UnauthorizedAccessException)
            {
                result.Add(description ?? new(id, id, id.Equals(active, StringComparison.OrdinalIgnoreCase)));
                onUnreadable(id, error);
            }
        }
        return result.ToImmutable();
    }

    public ConfigProfileRead Read(string id)
    {
        using var lease = files.Acquire(default);
        return ReadLocked(id);
    }

    public ConfigActiveSelection ActiveSelection()
    {
        using var lease = files.Acquire(default);
        return Selection();
    }

    // For read-only launcher surfaces. Launch and edit operations still take the profile lease.
    public static async ValueTask<ConfigProfile?> ReadActiveForDisplayAsync(ISettingsFolder home,
        CancellationToken cancellationToken = default)
    {
        var selected = ParseSelection(await home.ReadAsync(Active, cancellationToken).ConfigureAwait(false));
        return selected.ProfileId is { } id
            ? await ReadForDisplayAsync(home, id, id, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public static async ValueTask<ImmutableArray<ConfigProfile>> ReadChoicesAsync(ISettingsFolder home, IEnumerable<string> ids,
        CancellationToken cancellationToken = default, ConfigDialect dialect = ConfigDialect.Stock)
    {
        var selected = ParseSelection(await home.ReadAsync(Active, cancellationToken).ConfigureAwait(false));
        var result = ImmutableArray.CreateBuilder<ConfigProfile>();
        foreach (string id in ids.Where(ValidIdentifier).Distinct(StringComparer.OrdinalIgnoreCase))
            if (await ReadForDisplayAsync(home, id, selected.ProfileId, cancellationToken, dialect).ConfigureAwait(false) is { } profile)
                result.Add(profile);
        return [.. result.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    // The native host holds both launch and profile leases and checks for a live game before calling this.
    public static async ValueTask SelectForNextLaunchAsync(ISettingsFolder home, string id, CancellationToken cancellationToken = default)
    {
        if (await ReadForDisplayAsync(home, id, null, cancellationToken).ConfigureAwait(false) is null)
            throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
        await home.WriteAsync(Active, [.. Encoding.UTF8.GetBytes(id)], cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ConfigProfile?> ReadForDisplayAsync(ISettingsFolder home, string id, string? active,
        CancellationToken cancellationToken, ConfigDialect? displayDialect = null)
    {
        string directory = DirectoryName(id);
        if (await home.ReadAsync(directory + ConfigName, cancellationToken).ConfigureAwait(false) is not { } config)
            return null;
        var metadata = await home.ReadAsync(directory + MetadataName, cancellationToken).ConfigureAwait(false);
        var profile = ToProfile(id, ConfigProfileFormat.Metadata(metadata, id), active);
        return displayDialect is { } dialect
            ? profile with { RequestedResolution = new ConfigDocument(config.AsSpan(), dialect).EffectiveSetting("r_mode") }
            : profile;
    }

    public static async ValueTask<string?> ReadPlayerNameAsync(ISettingsFolder home, ConfigDialect dialect,
        CancellationToken cancellationToken = default)
    {
        var selected = ParseSelection(await home.ReadAsync(Active, cancellationToken).ConfigureAwait(false));
        if (selected.ProfileId is not { } id ||
            await home.ReadAsync(DirectoryName(id) + ConfigName, cancellationToken).ConfigureAwait(false) is not { } config)
            return null;
        string? name = new ConfigDocument(config.AsSpan(), dialect).EffectiveSetting("name");
        return string.IsNullOrWhiteSpace(name) ? null : QuakeColors.Plain(LauncherText.Prefix(name, 64));
    }

    // The native host holds the same profile lease used by an editor until spawn completes.
    public static async ValueTask<(ConfigStudioLaunch Studio, ImmutableDictionary<string, string> Values)?> ReadActiveForLaunchAsync(
        ISettingsFolder home, ConfigDialect dialect, CancellationToken cancellationToken = default)
    {
        var selected = ParseSelection(await home.ReadAsync(Active, cancellationToken).ConfigureAwait(false));
        if (selected.ProfileId is not { } id)
            return null;
        string directory = DirectoryName(id);
        if (await home.ReadAsync(directory + ConfigName, cancellationToken).ConfigureAwait(false) is not { } config)
            return null;
        if (await home.ReadAsync(directory + JournalName, cancellationToken).ConfigureAwait(false) is not null)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);

        var document = new ConfigDocument(config.AsSpan(), dialect);
        return (new(id, ConfigStudioLaunch.ChainBasename(id)), LauncherValues(document));
    }

    internal static ImmutableDictionary<string, string> LauncherValues(ConfigDocument document) =>
        GameSettings.LoadoutDefaultNames.Select(name => (Name: name, Value: document.EffectiveSetting(name)))
            .Where(entry => entry.Value is not null)
            .ToImmutableDictionary(entry => entry.Name, entry => entry.Value!, StringComparer.OrdinalIgnoreCase);

    public void Select(string id, ImmutableArray<byte>? expectedActive)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        ReadLocked(id);
        if (!SameBytes(Selection().OriginalBytes, expectedActive))
            throw Conflict();
        Write(Active, ImmutableArray.Create(Encoding.UTF8.GetBytes(id)));
    }

    public ConfigProfileRead Create(string id, string displayName, ConfigDocument document, ConfigStudioMetadata? metadata = null)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        ValidateDialect(document);
        metadata = (metadata ?? new(displayName)) with { DisplayName = displayName };
        CreateLocked(id, new(document.Serialized, ConfigProfileFormat.Encode(metadata)), ConfigHistorySource.You, "Created loadout");
        return ReadLocked(id);
    }

    public ConfigProfileRead Duplicate(string id, string newId, string displayName, ConfigProfileVersion expected)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        var original = ReadLocked(id);
        if (!original.Version.Matches(expected))
            throw Conflict();

        var metadata = original.Metadata with { DisplayName = displayName, Origin = new(ConfigOriginKind.Copied, id) };
        var version = new ConfigProfileVersion(original.Version.Config, ConfigProfileFormat.Encode(metadata), original.Version.Chain);
        CreateLocked(newId, version, ConfigHistorySource.You, "Copied loadout");
        return ReadLocked(newId);
    }

    public ConfigHistoryEntry Update(string id, ConfigDocument document, ConfigProfileVersion expected, ConfigStudioMetadata? metadata,
        string summary, ConfigHistorySource source = ConfigHistorySource.You)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        ValidateDialect(document);
        var after = new ConfigProfileVersion(document.Serialized, metadata is null ? expected.Metadata : ConfigProfileFormat.Encode(metadata),
            expected.Chain);
        return UpdateLocked(id, expected, after, source, summary);
    }

    public ConfigHistoryEntry UpdateChain(string id, ConfigDocument document, ConfigProfileVersion expected, ConfigStudioMetadata metadata,
        ImmutableArray<byte>? chain, string summary)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        ValidateDialect(document);
        var after = new ConfigProfileVersion(document.Serialized, ConfigProfileFormat.Encode(metadata), chain);
        return UpdateLocked(id, expected, after, ConfigHistorySource.You, summary);
    }

    public ValueTask<ConfigImportReceipt> CommitReviewedAsync(ConfigImportWrite write, CancellationToken cancellationToken)
    {
        using var lease = files.Acquire(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CheckWritable();
        if (hasDraft?.Invoke() == true)
            throw Conflict();
        ValidateDialect(write.Document);
        if (!ConfigProfileFormat.ValidHistoryId(write.OperationId))
            throw new ConfigProfileException(ConfigProfileFailure.InvalidHistory);

        string id = write.ProfileId;
        string directory = DirectoryName(id);
        var target = new ConfigProfileVersion(write.Document.Serialized, ConfigProfileFormat.Encode(write.Metadata), write.Expected?.Chain);
        if (files.DirectoryExists(directory))
        {
            Recover(id, directory);
            var receipt = ReadHistory(directory).FirstOrDefault(entry => entry.Id == write.OperationId);
            if (receipt is not null)
            {
                if (receipt.Source != ConfigHistorySource.Import || receipt.Summary != write.Summary ||
                    !receipt.Before.Matches(write.Expected ?? target) || !receipt.After.Matches(target))
                    throw Conflict();
                return ValueTask.FromResult(new ConfigImportReceipt(id, receipt));
            }
        }

        var result = write.Expected is { } expected
            ? UpdateLocked(id, expected, target, ConfigHistorySource.Import, write.Summary, write.OperationId)
            : CreateLocked(id, target, ConfigHistorySource.Import, write.Summary, write.OperationId);
        return ValueTask.FromResult(new ConfigImportReceipt(id, result));
    }

    private ConfigHistoryEntry CreateLocked(string id, ConfigProfileVersion version, ConfigHistorySource source,
        string summary, string? entryId = null, ConfigProfileVersion? before = null)
    {
        string target = DirectoryName(id);
        string companionPath = ChainPath(id);
        string journalPath = Players + "/.studio-companion-" + ConfigStudioLaunch.ChainBasename(id) + ".json";
        var intent = files.Read(journalPath) is { } record ? ConfigProfileFormat.Companion(record) : null;
        if (intent is not null && (intent.ProfileId != id || !intent.Version.Matches(version) ||
            intent.Entry.Source != source || intent.Entry.Summary != summary))
            throw Conflict();

        if (files.DirectoryExists(target))
        {
            if (intent is null)
                throw new ConfigProfileException(ConfigProfileFailure.AlreadyExists);
            if (!ReadVersion(id, target).Matches(version) || !ReadHistory(target).Any(entry => ConfigProfileFormat.SameHistory(entry, intent.Entry)))
                throw Conflict();
            Delete(journalPath);
            return intent.Entry;
        }
        if (files.ListDirectories(Players).Contains(id, StringComparer.OrdinalIgnoreCase))
            throw new ConfigProfileException(ConfigProfileFailure.AlreadyExists);
        var priorChain = files.Read(companionPath);
        if (priorChain is not null && (intent is null || !SameBytes(priorChain, version.Chain)))
            throw Conflict();

        string staging = Players + "/.studio-create-" + Guid.NewGuid().ToString("N");
        var entry = intent?.Entry ?? Entry(source, summary, before ?? version, version, entryId);
        bool published = false;
        try
        {
            Write(SnapshotPath(staging, entry.Id), ConfigProfileFormat.Encode(entry));
            Write(staging + ConfigName, version.Config);
            Write(staging + MetadataName, version.Metadata!.Value);
            if (version.Chain is { } chain)
            {
                if (intent is null)
                    Write(journalPath, ConfigProfileFormat.Encode(new ConfigCompanionCreation(id, version, entry)));
                if (!SameBytes(priorChain, chain))
                    Write(companionPath, chain);
                if (!SameBytes(files.Read(companionPath), chain))
                    throw Conflict();
            }
            CheckWritable();
            files.PublishDirectory(staging, target);
            published = true;
            if (version.Chain is not null)
                Delete(journalPath);
        }
        finally
        {
            if (!published)
            {
                try { files.DeleteOwnedDirectory(staging); }
                catch (IOException) { /* Keep the original publication error. */ }
            }
        }
        return entry;
    }

    private ConfigProfileRead ReadLocked(string id)
    {
        string directory = DirectoryName(id);
        Recover(id, directory);
        var version = ReadVersion(id, directory);
        var metadata = ConfigProfileFormat.Metadata(version.Metadata, id);
        return new(ToProfile(id, metadata, Selection().ProfileId), new(version.Config.AsSpan(), Dialect), metadata, version);
    }

    private ConfigProfileVersion ReadVersion(string id, string directory) =>
        new(files.Read(directory + ConfigName) ?? throw new ConfigProfileException(ConfigProfileFailure.MissingProfile),
            files.Read(directory + MetadataName), files.Read(ChainPath(id)));

    private ConfigActiveSelection Selection() => ParseSelection(files.Read(Active));

    private static ConfigActiveSelection ParseSelection(ImmutableArray<byte>? bytes)
    {
        var tokens = ConfigDocument.Tokenize(bytes.GetValueOrDefault().AsSpan(), ConfigDialect.Stock).Tokens;
        string? name = tokens.IsEmpty ? null : tokens[0].Text;
        return new(name is not null && ValidIdentifier(name) ? name : null, bytes);
    }

    private static ConfigProfile ToProfile(string id, ConfigStudioMetadata metadata, string? active) =>
        new(id, metadata.DisplayName, id.Equals(active, StringComparison.OrdinalIgnoreCase));

    private ConfigHistoryEntry Entry(ConfigHistorySource source, string summary, ConfigProfileVersion before,
        ConfigProfileVersion after, string? id = null) =>
        new(id ?? Guid.NewGuid().ToString(), (timeProvider ?? TimeProvider.System).GetUtcNow(), source, summary, before, after);

    private void Write(string path, ImmutableArray<byte> data)
    {
        CheckWritable();
        files.WriteAtomic(path, data);
    }

    private void Delete(string path)
    {
        CheckWritable();
        files.DeleteFile(path);
    }

    private void CheckWritable()
    {
        if (gameIsRunning?.Invoke() == true)
            throw new ConfigProfileException(ConfigProfileFailure.GameRunning);
    }

    private void ValidateDialect(ConfigDocument document)
    {
        if (document.Dialect != Dialect)
            throw new ConfigImportSessionException(ConfigImportSessionFailure.InvalidBundle);
    }

    private static string DirectoryName(string id) => ValidIdentifier(id)
        ? Players + '/' + id : throw new ConfigProfileException(ConfigProfileFailure.InvalidIdentifier);
    private static bool ValidIdentifier(string id) => ConfigStudioLaunch.IsProfileId(id);
    private static string ChainPath(string id) => "main/" + ConfigStudioLaunch.ChainBasename(id);
    private static string SnapshotPath(string directory, string id) => directory + HistoryName + '/' + id + ".json";

    private static bool SameBytes(ImmutableArray<byte>? a, ImmutableArray<byte>? b) =>
        a.HasValue == b.HasValue && (!a.HasValue || a.Value.AsSpan().SequenceEqual(b.GetValueOrDefault().AsSpan()));

    private static ConfigProfileException Conflict() => new(ConfigProfileFailure.Conflict);
}
