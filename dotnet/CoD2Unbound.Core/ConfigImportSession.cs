using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public enum ConfigImportPlatform { Native, Windows }
public enum ConfigImportMode { Review, Everything }

public abstract record ConfigImportTarget
{
    private ConfigImportTarget() { }

    public sealed record NewLoadout(string Id, string DisplayName) : ConfigImportTarget;
    public sealed record Existing(ConfigProfileRead Read) : ConfigImportTarget;
}

public enum ConfigImportSessionFailure { InvalidBundle, UnavailableSelection, EmptySelection, UnsafeComposition, AlreadyApplied, ApplyInProgress }

public sealed class ConfigImportSessionException(ConfigImportSessionFailure failure) : Exception(failure switch
{
    ConfigImportSessionFailure.InvalidBundle => "The import files are missing, duplicated or use a different config dialect.",
    ConfigImportSessionFailure.UnavailableSelection => "This command is blocked, locked or belongs to machine settings.",
    ConfigImportSessionFailure.EmptySelection => "Select at least one config change.",
    ConfigImportSessionFailure.UnsafeComposition => "These selected commands cannot be safely combined with this loadout. Review their dependencies and scripts.",
    ConfigImportSessionFailure.AlreadyApplied => "This import has already been applied.",
    ConfigImportSessionFailure.ApplyInProgress => "This import is already being applied.",
    _ => throw new ArgumentOutOfRangeException(nameof(failure))
})
{
    public ConfigImportSessionFailure Failure { get; } = failure;
}

public sealed record ConfigImportRow(ConfigImportCandidate Candidate, string Title, string Group, string? OldValue,
    string? NewValue, ImmutableArray<string> LockedNames, string? PlatformNote)
{
    public string Id => Candidate.Id;
    public string? SourceLine { get; init; }
    public string? PreviousLine { get; init; }
    public bool CanSelect => Candidate.Importable && Candidate.Classification != ConfigImportClassification.Blocked &&
        LockedNames.IsEmpty && PlatformNote is null && Candidate.Command.Kind is not
        (ConfigCommandKind.Empty or ConfigCommandKind.Comment);
}

public sealed record ConfigImportPreview(ConfigDocument Document, ConfigStudioMetadata Metadata);
public sealed record ConfigImportReceipt(string ProfileId, ConfigHistoryEntry HistoryEntry);
public sealed record ConfigImportWrite(string ProfileId, ConfigDocument Document, ConfigStudioMetadata Metadata,
    ConfigProfileVersion? Expected, string Summary, string OperationId);

public interface IConfigImportStore
{
    ConfigDialect Dialect { get; }

    // Check the draft and running-game interlocks and the expected version under the same
    // ownership as the write. A null expected version creates a new profile. Preserve
    // active selection and publish exactly one Import history entry. If already committed,
    // return its receipt even if a subsequent refresh or cancellation occurs.
    ValueTask<ConfigImportReceipt> CommitReviewedAsync(ConfigImportWrite write, CancellationToken cancellationToken);
}

public sealed partial class ConfigImportSession
{
    private readonly Lock gate = new();
    private readonly string operationId = Guid.NewGuid().ToString();
    private readonly IConfigImportCatalog catalog;
    private readonly ConfigImportSafety safety;
    private readonly Dictionary<string, ConfigImportFile> files;
    private ImmutableArray<ConfigImportRow> rows;
    private ImmutableArray<ConfigImportCandidate> priorReview;
    private bool applying;

    public ConfigImportSession(ConfigImportBundle bundle, ConfigDialect dialect, IConfigImportCatalog catalog, string displayName,
        ConfigImportPlatform sourcePlatform = ConfigImportPlatform.Native, ConfigImportPlatform destinationPlatform = ConfigImportPlatform.Native,
        ConfigOrigin? origin = null)
    {
        files = new(StringComparer.Ordinal);
        foreach (var file in bundle.Files)
            if (file.Document.Dialect != dialect || !files.TryAdd(file.Path, file))
                throw new ConfigImportSessionException(ConfigImportSessionFailure.InvalidBundle);
        if (!files.ContainsKey(bundle.EntryPath))
            throw new ConfigImportSessionException(ConfigImportSessionFailure.InvalidBundle);

        this.catalog = catalog;
        safety = new(catalog);
        Dialect = dialect;
        SourcePlatform = sourcePlatform;
        DestinationPlatform = destinationPlatform;
        Origin = origin ?? new(ConfigOriginKind.Imported, SourceKey(bundle.Source.Kind));
        Review = safety.Review(bundle);
        Target = new ConfigImportTarget.NewLoadout("Import-" + Guid.NewGuid().ToString("D")[..24].ToUpperInvariant(), displayName);
        RefreshRows();
    }

    public ConfigImportReview Review { get; }
    public ConfigDialect Dialect { get; }
    public ConfigImportPlatform SourcePlatform { get; }
    public ConfigImportPlatform DestinationPlatform { get; }
    public ConfigOrigin Origin { get; }
    public ConfigImportMode Mode { get; private set; }
    public ConfigImportTarget Target { get; private set; }
    public ConfigImportReceipt? Receipt { get; private set; }
    public ImmutableHashSet<string> SelectedIds { get; private set; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    public ImmutableArray<ConfigImportRow> Rows => rows;
    public bool IsEmpty => !rows.Any(row => row.Candidate.Command.Kind is not
        (ConfigCommandKind.Comment or ConfigCommandKind.Empty) &&
        !row.Candidate.Reasons.Contains(ConfigImportRisk.Malformed));
    public int SelectedRiskCount => rows.Count(row => SelectedIds.Contains(row.Id) &&
        row.Candidate.Classification == ConfigImportClassification.NeedsOk);
    public bool RequiresConfirmation => Mode == ConfigImportMode.Everything || SelectedRiskCount > 0;

    public void RenameNewTarget(string id, string name)
    {
        lock (gate)
        {
            CheckMutable();
            if (Target is not ConfigImportTarget.NewLoadout)
                throw new ConfigImportSessionException(ConfigImportSessionFailure.UnavailableSelection);
            Target = new ConfigImportTarget.NewLoadout(id, name);
        }
    }

    private ConfigProfileRead? Baseline => (Target as ConfigImportTarget.Existing)?.Read;

    public void SetTarget(ConfigImportTarget target)
    {
        lock (gate)
        {
            CheckMutable();
            Target = target;
            RefreshRows();
        }
    }

    public void SetMode(ConfigImportMode mode)
    {
        lock (gate)
        {
            CheckMutable();
            Mode = mode;
            ResetSelection();
        }
    }

    public void Select(string id, bool selected)
    {
        lock (gate)
        {
            CheckMutable();
            if (!rows.Any(row => row.Id == id && row.CanSelect))
                throw new ConfigImportSessionException(ConfigImportSessionFailure.UnavailableSelection);
            SelectedIds = selected ? SelectedIds.Add(id) : SelectedIds.Remove(id);
            if (!selected && Mode == ConfigImportMode.Everything)
                Mode = ConfigImportMode.Review;
        }
    }

    public void ReplaceSelection(IEnumerable<string> ids)
    {
        lock (gate)
        {
            CheckMutable();
            var replacement = ids.ToImmutableHashSet(StringComparer.Ordinal);
            if (!replacement.IsSubsetOf(rows.Where(row => row.CanSelect).Select(row => row.Id)))
                throw new ConfigImportSessionException(ConfigImportSessionFailure.UnavailableSelection);
            Mode = ConfigImportMode.Review;
            SelectedIds = replacement;
        }
    }

    public ConfigImportPreview Preview()
    {
        lock (gate)
        {
            CheckMutable();
            return Prepare();
        }
    }

    public async ValueTask<ConfigImportReceipt> ApplyAsync(IConfigImportStore store, CancellationToken cancellationToken = default)
    {
        ConfigImportWrite write;
        lock (gate)
        {
            CheckMutable();
            cancellationToken.ThrowIfCancellationRequested();
            if (store.Dialect != Dialect)
                throw new ConfigImportSessionException(ConfigImportSessionFailure.InvalidBundle);
            var prepared = Prepare();
            string profileId = Target switch
            {
                ConfigImportTarget.NewLoadout created => created.Id,
                ConfigImportTarget.Existing existing => existing.Read.Profile.Id,
                _ => throw new InvalidOperationException()
            };
            write = new(profileId, prepared.Document, prepared.Metadata, Baseline?.Version, "Imported reviewed config", operationId);
            applying = true;
        }

        try
        {
            var receipt = await store.CommitReviewedAsync(write, cancellationToken).ConfigureAwait(false);
            lock (gate)
                Receipt = receipt;
            return receipt;
        }
        finally
        {
            lock (gate)
                applying = false;
        }
    }

    private void CheckMutable()
    {
        if (Receipt is not null)
            throw new ConfigImportSessionException(ConfigImportSessionFailure.AlreadyApplied);
        if (applying)
            throw new ConfigImportSessionException(ConfigImportSessionFailure.ApplyInProgress);
    }

    private void ResetSelection() => SelectedIds = rows
        .Where(row => row.CanSelect && (Mode == ConfigImportMode.Everything || row.Candidate.SelectedByDefault))
        .Select(row => row.Id).ToImmutableHashSet(StringComparer.Ordinal);

    private ConfigImportPreview Prepare()
    {
        var selected = rows.Where(row => SelectedIds.Contains(row.Id)).ToArray();
        if (selected.Length == 0)
            throw new ConfigImportSessionException(ConfigImportSessionFailure.EmptySelection);
        var baseline = Baseline;
        if (selected.Length != SelectedIds.Count || selected.Any(row => !row.CanSelect) ||
            (baseline is not null && baseline.Document.Dialect != Dialect))
            throw new ConfigImportSessionException(ConfigImportSessionFailure.UnavailableSelection);

        ImmutableArray<byte> original = baseline?.Version.Config ?? [];
        if (baseline is not null && !original.AsSpan().SequenceEqual(baseline.Document.Serialized.AsSpan()))
            throw new ConfigImportSessionException(ConfigImportSessionFailure.UnsafeComposition);

        using var bytes = new MemoryStream();
        bytes.Write(original.AsSpan());
        var starts = new int[selected.Length];
        for (int i = 0; i < selected.Length; i++)
        {
            var location = selected[i].Candidate.Location;
            if (bytes.Length > 0)
                bytes.WriteByte(10);
            starts[i] = (int)bytes.Length;
            bytes.Write(location.Span.Slice(files[location.File].Document.Serialized.AsSpan()));
            bytes.WriteByte(10);
            if (bytes.Length > 65_535)
                throw new ConfigImportException(ConfigImportFailure.TooLarge);
        }

        var composed = new ConfigDocument(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), Dialect);
        var commands = composed.Commands.ToDictionary(command => command.Span.Start);
        var before = baseline?.Document ?? new ConfigDocument([], Dialect);
        foreach (var command in before.Commands.Where(command => !command.Tokens.IsEmpty))
            if (!commands.TryGetValue(command.Span.Start, out var actual) || !SameTokens(command, actual))
                throw new ConfigImportSessionException(ConfigImportSessionFailure.UnsafeComposition);

        var composedReview = ReviewDocument(composed, "composed.cfg").ToDictionary(row => row.Location.Span.Start);
        for (int i = 0; i < selected.Length; i++)
        {
            var expected = selected[i].Candidate;
            if (!composedReview.TryGetValue(starts[i], out var actual) || !SameTokens(expected.Command, actual.Command) ||
                !actual.Importable || actual.Classification > expected.Classification || !LockedNames(actual, baseline).IsEmpty ||
                PlatformNote(actual) is not null)
                throw new ConfigImportSessionException(ConfigImportSessionFailure.UnsafeComposition);
        }

        var metadata = baseline?.Metadata ??
            new ConfigStudioMetadata(((ConfigImportTarget.NewLoadout)Target).DisplayName) { Origin = Origin };
        var imported = metadata.LastImportedValues.ToBuilder();
        foreach (var row in selected)
        {
            var candidate = row.Candidate;
            var document = files[candidate.Location.File].Document;
            if (ConfigImportScript.Assignment(candidate.Command, document, catalog, static _ => false) is { } assignment)
                imported[assignment.Name] = ConfigImportScript.Text(assignment.Value.AsSpan());
        }
        metadata = metadata with { LastImportedValues = imported.ToImmutable() };
        metadata.Validate();
        return new(composed, metadata);
    }

    private ImmutableArray<ConfigImportCandidate> ReviewDocument(ConfigDocument document, string name) =>
        safety.Review(new(new(ConfigImportSourceKind.Paste), name, [new(name, document)], [])).Candidates;

    private static bool SameTokens(ConfigCommand a, ConfigCommand b) =>
        a.Tokens.Select(token => token.Value).SequenceEqual(b.Tokens.Select(token => token.Value), ConfigScriptBytes.Instance);

    private static string SourceKey(ConfigImportSourceKind source) => source.ToString().ToLowerInvariant();
}
