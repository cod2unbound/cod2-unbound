using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public sealed record ConfigStudioDraft(string ProfileId, ConfigProfileVersion Expected, ConfigDocument Document);
public sealed record ConfigStudioState(ImmutableArray<ConfigProfile> Profiles, ConfigProfileRead? Current,
    ImmutableArray<ConfigHistoryEntry> History, ConfigStudioDraft? Draft, bool ReadOnly, bool LaunchReserved,
    string? FocusedProfileId, ConfigTransactionRecovery? Recovery);

/// <summary>Coordinates Studio state on the launcher's serialized command queue.</summary>
public sealed partial class ConfigStudioSession
{
    private const string ArchivePath = "main/players/.studio-game-session.json";
    private readonly IConfigProfileFiles files;
    private readonly Func<bool> verifiedGameRunning;
    private readonly TimeProvider clock;
    private readonly Func<Guid> newIdentifier;
    private ConfigProfileStore store;
    private ConfigStudioArchive? archive;
    private bool reserved;
    private ImmutableHashSet<GameProcessIdentity> games = ImmutableHashSet<GameProcessIdentity>.Empty;
    private ImmutableArray<ConfigProfile> profiles = [];
    private ConfigProfileRead? current;
    private ImmutableArray<ConfigHistoryEntry> history = [];
    private ConfigStudioDraft? draft;
    private string? focused;
    private ConfigTransactionRecovery? recovery;

    public ConfigStudioSession(IConfigProfileFiles files, ConfigDialect dialect, Func<bool> verifiedGameRunning, TimeProvider clock,
        Func<Guid>? newIdentifier = null)
    {
        this.files = files;
        this.verifiedGameRunning = verifiedGameRunning;
        this.clock = clock;
        this.newIdentifier = newIdentifier ?? Guid.NewGuid;
        store = Open(dialect);
        ReloadArchive();
    }

    public bool IsReadOnly => reserved || !games.IsEmpty || verifiedGameRunning();
    public ConfigDialect Dialect => store.Dialect;
    public ConfigStudioState State => new(profiles, current, history, draft, IsReadOnly, reserved, focused, recovery);

    public void ChangeDialect(ConfigDialect dialect)
    {
        if (store.Dialect == dialect)
            return;
        store = Open(dialect);
        if (draft is not null)
            draft = draft with { Document = new(draft.Document.Serialized.AsSpan(), dialect) };
        Refresh();
    }

    public void Refresh()
    {
        var available = ManagedProfiles();
        string? id = focused is not null && available.Any(profile => SameId(profile.Id, focused))
            ? focused : null;
        var selected = (id ?? available.FirstOrDefault(profile => profile.IsActive)?.Id) is { } profileId ? store.Read(profileId) : null;
        var entries = selected is null ? [] : store.History(selected.Profile.Id);
        profiles = available;
        focused = id;
        current = selected;
        history = entries;
    }

    public void Focus(string? id)
    {
        if (draft is not null)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        if (id is not null && !ManagedProfiles().Any(profile => SameId(profile.Id, id)))
            throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
        focused = id;
        Refresh();
    }

    public ConfigProfileRead ReadProfile(string id) => store.Read(id);
    public ConfigProfileRead? ReadActive() => ManagedProfiles().FirstOrDefault(profile => profile.IsActive) is { } active
        ? store.Read(active.Id) : null;

    public void Select(string id)
    {
        CheckWritable(rejectDraft: true);
        store.Select(id, store.ActiveSelection().OriginalBytes);
        Refresh();
    }

    public void SetDraft(ConfigDocument document)
    {
        CheckWritable(rejectDraft: false);
        if (document.Dialect != Dialect)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        var selected = current ?? throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
        draft = draft is { } existing ? existing with { Document = document } : new(selected.Profile.Id, selected.Version, document);
    }

    public void DiscardDraft()
    {
        Refresh();
        draft = null;
        recovery = null;
    }

    public void RebaseDraft(ConfigProfileVersion on)
    {
        CheckWritable(rejectDraft: false);
        var changed = draft ?? throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
        if (!store.Read(changed.ProfileId).Version.Matches(on))
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        draft = changed with { Expected = on };
    }

    public ConfigHistoryEntry SaveDraft(string summary, ConfigStudioMetadata? metadata = null)
    {
        CheckWritable(rejectDraft: false);
        var changed = draft ?? throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
        var result = store.Update(changed.ProfileId, changed.Document, changed.Expected, metadata, summary);
        draft = null;
        Refresh();
        return result;
    }

    public ConfigProfileRead Create(string id, string name, ConfigDocument? document = null)
    {
        CheckWritable(rejectDraft: true);
        var result = store.Create(id, name, document ?? new([], Dialect));
        Refresh();
        return result;
    }

    public ConfigProfileRead Duplicate(string id, string name)
    {
        CheckWritable(rejectDraft: true);
        var selected = current ?? throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
        var result = store.Duplicate(selected.Profile.Id, id, name, selected.Version);
        Refresh();
        return result;
    }

    public ConfigProfileTrashHandle Trash(string id, IConfigProfileTrash trash)
    {
        CheckWritable(rejectDraft: true);
        var result = store.MoveToTrash(id, store.Read(id).Version, trash);
        Refresh();
        return result;
    }

    public void RestoreTrash(string id, ConfigProfileTrashHandle handle, IConfigProfileTrash trash)
    {
        CheckWritable(rejectDraft: true);
        store.RestoreFromTrash(handle, id, trash);
        Focus(id);
    }

    public void InspectRecovery(string id) => recovery = store.InspectRecovery(id);

    public void KeepDiskVersion()
    {
        CheckWritable(rejectDraft: false);
        var inspected = recovery ?? throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
        store.KeepDiskVersion(inspected);
        recovery = null;
        Refresh();
    }

    public TResult Mutate<TResult>(string id, Func<ConfigProfileStore, TResult> edit)
    {
        CheckWritable(rejectDraft: true);
        if (!SameId(id, current?.Profile.Id))
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        var result = edit(store);
        Refresh();
        return result;
    }

    public ConfigProfileRead ReadImportTarget(string id)
    {
        CheckWritable(rejectDraft: true);
        return store.Read(id);
    }

    public async ValueTask<ConfigImportReceipt> CommitReviewedImportAsync(ConfigImportWrite write, CancellationToken cancellationToken = default)
    {
        CheckWritable(rejectDraft: true);
        var result = await store.CommitReviewedAsync(write, cancellationToken).ConfigureAwait(false);
        Refresh();
        return result;
    }

    public ConfigMigrationResult Migrate(ConfigMigrationInput input)
    {
        CheckWritable(rejectDraft: true);
        var result = store.Migrate(input);
        Refresh();
        return result;
    }

    // The adapter holds a cross-process launch/activation lease across PrepareLaunch and process
    // creation. Keep the lease through commit or cancel and use the same lease for recovery.
    public ConfigStudioLaunch? PrepareLaunch(LaunchLink? link = null, string? connectionProfileId = null,
        ConfigConnectionLaunch? connection = null, ConfigProfileVersion? expected = null, ImmutableArray<byte>? reviewedChain = null)
    {
        CheckWritable(rejectDraft: true);
        ReloadArchive();
        FinishSession();
        bool disposable = connection is { Values.Count: > 0 };
        if (disposable)
        {
            if (link is null)
                throw new ConfigProfileException(ConfigProfileFailure.Conflict);
            _ = connection!.ArgumentsFor(link).ToArray();
        }

        var active = store.ActiveSelection();
        var launch = active.ProfileId is { } id ? store.Read(id) : null;
        if (disposable || connectionProfileId is not null)
        {
            var chosen = connectionProfileId is { } alternate ? store.Read(alternate) :
                launch ?? throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
            if (disposable && (!chosen.Version.Matches(expected) || !SameBytes(chosen.Version.Chain, reviewedChain)))
                throw new ConfigProfileException(ConfigProfileFailure.Conflict);
            if (disposable || !SameId(active.ProfileId, chosen.Profile.Id))
            {
                var before = active.OriginalBytes ?? throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
                string targetId = disposable ? "csconn" + newIdentifier().ToString("N") : chosen.Profile.Id;
                Archive();
                if (disposable && archive!.Before.Keys.Any(id => SameId(id, targetId)))
                    throw new ConfigProfileException(ConfigProfileFailure.AlreadyExists);
                var selection = new ConfigStudioConnectionArchive(before.ToArray(), Encoding.UTF8.GetBytes(targetId),
                    disposable ? chosen.Profile.Id : null, disposable ? targetId : null);
                WriteArchive(archive! with { ConnectionSelection = selection });
                launch = disposable ? store.Duplicate(chosen.Profile.Id, targetId, chosen.Profile.DisplayName, chosen.Version) : chosen;
                store.Select(targetId, before);
            }
            else
                launch = chosen;
        }
        Archive();
        reserved = true;
        if (launch is null)
            return null;
        return new(launch.Profile.Id, launch.Version.Chain is not null ? ConfigStudioLaunch.ChainBasename(launch.Profile.Id) : null,
            disposable);
    }

    public ClientLaunchRequest PrepareClientLaunch(ClientLaunchRequest request)
    {
        var launch = PrepareLaunch(request.Link);
        return launch is null ? request : request with
        {
            Studio = launch,
            Loadout = ConfigProfileStore.LauncherValues(store.Read(launch.ProfileId).Document)
        };
    }

    public void CommitLaunch(GameProcessIdentity game)
    {
        if (!reserved)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        games = games.Add(game);
        reserved = false;
    }

    public void CancelLaunch()
    {
        reserved = false;
        if (!IsReadOnly)
            FinishSession();
        Refresh();
    }

    public void ReconcileGames(ImmutableArray<GameProcessIdentity> observed)
    {
        bool starting = games.IsEmpty && !observed.IsEmpty;
        games = observed.ToImmutableHashSet();
        if (starting && archive is null)
        {
            ReloadArchive();
            Archive();
        }
        if (!IsReadOnly)
        {
            ReloadArchive();
            FinishSession();
        }
        Refresh();
    }

    private ConfigProfileStore Open(ConfigDialect dialect) => new(files, dialect, () => IsReadOnly, () => draft is not null, clock);

    private ImmutableArray<ConfigProfile> ManagedProfiles() =>
        [.. store.List().Where(profile => !SameId(profile.Id, archive?.ConnectionSelection?.DisposableProfileId))];

    private void CheckWritable(bool rejectDraft)
    {
        if (IsReadOnly)
            throw new ConfigProfileException(ConfigProfileFailure.GameRunning);
        if (rejectDraft && draft is not null)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
    }

    private void Archive()
    {
        if (archive is not null)
            return;
        var snapshots = ManagedProfiles().ToDictionary(profile => profile.Id, profile =>
        {
            var version = store.Read(profile.Id).Version;
            return new ConfigStudioVersionArchive(version.Config.ToArray(), version.Metadata?.ToArray(), version.Chain?.ToArray());
        }, StringComparer.OrdinalIgnoreCase);
        WriteArchive(new(newIdentifier().ToString("D"), snapshots));
    }

    private void FinishSession()
    {
        if (archive is not { } saved)
            return;
        foreach (var profile in ManagedProfiles())
        {
            var original = saved.Before.FirstOrDefault(pair => SameId(pair.Key, profile.Id)).Value;
            var version = original is null ? new ConfigProfileVersion([], null) : original.Version();
            store.RecordGameSession(profile.Id, version, sessionId: saved.Id);
        }
        if (saved.ConnectionSelection is { } selection)
        {
            _ = store.RestoreConnectionSelection([.. selection.Before], [.. selection.Applied]);
            if (selection.DisposableProfileId is { } id && selection.SourceProfileId is { } source)
                store.DiscardConnectionProfile(id, source, [.. selection.Applied]);
        }
        files.DeleteFile(ArchivePath);
        archive = null;
    }

    private void ReloadArchive()
    {
        if (files.Read(ArchivePath) is not { } bytes)
        {
            archive = null;
            return;
        }

        try
        {
            var saved = JsonSerializer.Deserialize(bytes.AsSpan(), StudioArchiveJson.Default.ConfigStudioArchive);
            if (saved is null || !Guid.TryParseExact(saved.Id, "D", out _) || saved.Before is null ||
                saved.Before.Any(pair => pair.Value is null || pair.Value.Config is null) ||
                saved.Before.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != saved.Before.Count)
                throw InvalidArchive();
            foreach (string id in saved.Before.Keys)
                ConfigStudioLaunch.ValidateProfileId(id);
            if (saved.ConnectionSelection is { } selection)
            {
                if (selection.Before is null || selection.Applied is null)
                    throw InvalidArchive();
                var before = ConfigDocument.Tokenize(selection.Before, ConfigDialect.Stock).Tokens;
                if (before.IsEmpty)
                    throw InvalidArchive();
                ConfigStudioLaunch.ValidateProfileId(before[0].Text);
                string target = Encoding.UTF8.GetString(selection.Applied);
                ConfigStudioLaunch.ValidateProfileId(target);
                if (selection.DisposableProfileId is { } disposable)
                {
                    if (target != disposable || disposable.Length != 38 || !disposable.StartsWith("csconn", StringComparison.Ordinal) ||
                        !disposable[6..].All(char.IsAsciiHexDigitLower) || selection.SourceProfileId is not { } source ||
                        saved.Before.Keys.Any(id => SameId(id, disposable)))
                        throw InvalidArchive();
                    ConfigStudioLaunch.ValidateProfileId(source);
                }
            }
            archive = saved;
        }
        catch (Exception error) when (error is JsonException or LaunchPlanException)
        {
            throw InvalidArchive();
        }
    }

    private void WriteArchive(ConfigStudioArchive value)
    {
        files.WriteAtomic(ArchivePath, [.. JsonSerializer.SerializeToUtf8Bytes(value, StudioArchiveJson.Default.ConfigStudioArchive)]);
        archive = value;
    }

    private static bool SameId(string? a, string? b) => a is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static bool SameBytes(ImmutableArray<byte>? a, ImmutableArray<byte>? b) =>
        a.HasValue == b.HasValue && (a is not { } value || value.AsSpan().SequenceEqual(b!.Value.AsSpan()));

    private static ConfigProfileException InvalidArchive() => new(ConfigProfileFailure.InvalidHistory);
}

internal sealed record ConfigStudioVersionArchive(byte[] Config, byte[]? Metadata, byte[]? Chain)
{
    public ConfigProfileVersion Version() => new([.. Config], Metadata is null ? null : [.. Metadata], Chain is null ? null : [.. Chain]);
}
internal sealed record ConfigStudioConnectionArchive(byte[] Before, byte[] Applied,
    [property: JsonPropertyName("sourceProfileID")] string? SourceProfileId,
    [property: JsonPropertyName("disposableProfileID")] string? DisposableProfileId);
internal sealed record ConfigStudioArchive(string Id, Dictionary<string, ConfigStudioVersionArchive> Before,
    ConfigStudioConnectionArchive? ConnectionSelection = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConfigStudioArchive))]
internal sealed partial class StudioArchiveJson : JsonSerializerContext;
