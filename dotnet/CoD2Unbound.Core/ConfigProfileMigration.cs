using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public abstract record ConfigMigrationSource
{
    private ConfigMigrationSource() { }

    public sealed record EmptyHome : ConfigMigrationSource;
    public sealed record FlattenedLegacy : ConfigMigrationSource;
    public sealed record Profile(string Id) : ConfigMigrationSource;
}

public sealed class ConfigPersonalValues
{
    public ConfigPersonalValues(IEnumerable<(string Name, string Value)> effectiveLauncherDvars, double dpi)
    {
        if (!double.IsFinite(dpi) || dpi <= 0)
            throw new ConfigProfileException(ConfigProfileFailure.InvalidMetadata);
        Dpi = dpi;
        var selected = new List<(string Name, string Value)>();
        foreach (var (name, value) in effectiveLauncherDvars)
        {
            string lower = name.ToLowerInvariant();
            if (GameSettings.MachineDvarNames.Contains(lower) || lower == "r_rendererpreference" ||
                ConfigFetchRequest.CredentialName(lower))
                continue;
            selected.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            selected.Add((name, value));
        }
        Settings = [.. selected];
    }

    public ImmutableArray<(string Name, string Value)> Settings { get; }
    public double Dpi { get; }
}

public sealed record ConfigMigrationInput(ConfigMigrationSource Source, ImmutableArray<byte> ExpectedOriginal, ConfigPersonalValues PersonalValues);
public sealed record ConfigMigrationResult(string ProfileId, bool AlreadyCompleted);
internal sealed record ConfigMigrationRecord(ConfigMigrationSource Source, ImmutableArray<byte> Original, string TargetId,
    ImmutableArray<byte>? ExpectedActive, ConfigProfileVersion Target, bool Completed);

public sealed partial class ConfigProfileStore
{
    private const string MigrationBackup = Players + "/.studio-migration/original.cfg";
    private const string MigrationJournal = Players + "/.studio-migration/migration.json";

    public ImmutableArray<byte>? OriginalMigrationBackup()
    {
        using var lease = files.Acquire(default);
        return files.Read(MigrationBackup);
    }

    public ConfigMigrationResult Migrate(ConfigMigrationInput input)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        var record = files.Read(MigrationJournal) is { } saved ? ConfigProfileFormat.Migration(saved) : StartMigration(input);
        if (!SameBytes(files.Read(MigrationBackup), record.Original) || !ValidIdentifier(record.TargetId))
            throw new ConfigProfileException(ConfigProfileFailure.InvalidMigration);
        if (record.Completed)
            return new(record.TargetId, true);

        CheckSource();
        string directory = DirectoryName(record.TargetId);
        if (files.DirectoryExists(directory))
        {
            if (!ReadLocked(record.TargetId).Version.Matches(record.Target))
                throw Conflict();
        }
        else
        {
            ConfigProfileFormat.Metadata(record.Target.Metadata, record.TargetId);
            CreateLocked(record.TargetId, record.Target, ConfigHistorySource.Migration, "Imported existing launcher settings",
                before: new(record.Original, null));
        }
        CheckSource();
        var chosen = Selection().OriginalBytes;
        var desired = ImmutableArray.Create(Encoding.UTF8.GetBytes(record.TargetId));
        if (!SameBytes(chosen, record.ExpectedActive) && !SameBytes(chosen, desired))
            throw Conflict();
        if (!SameBytes(chosen, desired))
            Write(Active, desired);
        Write(MigrationJournal, ConfigProfileFormat.Encode(record with { Completed = true }));
        return new(record.TargetId, false);

        void CheckSource()
        {
            if (!SameBytes(MigrationSourceBytes(record.Source, record.TargetId), record.Original))
                throw Conflict();
        }
    }

    private ConfigMigrationRecord StartMigration(ConfigMigrationInput input)
    {
        var original = MigrationSourceBytes(input.Source);
        if (!SameBytes(original, input.ExpectedOriginal))
            throw Conflict();
        if (files.Read(MigrationBackup) is { } backup)
        {
            if (!SameBytes(backup, original))
                throw Conflict();
        }
        else
        {
            Write(MigrationBackup, original);
        }
        if (!SameBytes(MigrationSourceBytes(input.Source), original))
            throw Conflict();

        var document = new ConfigDocument(original.AsSpan(), Dialect);
        foreach (var (name, value) in input.PersonalValues.Settings)
            document = document.SetSetting(name, value);
        var names = files.ListDirectories(Players).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string id = "Main";
        for (int suffix = 2; names.Contains(id); suffix++)
            id = FormattableString.Invariant($"Main_{suffix}");
        var metadata = new ConfigStudioMetadata("Main")
        {
            Dpi = input.PersonalValues.Dpi,
            Origin = new(ConfigOriginKind.Migration, input.Source switch
            {
                ConfigMigrationSource.EmptyHome => "New installation",
                ConfigMigrationSource.FlattenedLegacy => "main/config_mp.cfg",
                ConfigMigrationSource.Profile profile => $"players/{profile.Id}/config_mp.cfg",
                _ => throw new ConfigProfileException(ConfigProfileFailure.InvalidMigration)
            })
        };
        var version = new ConfigProfileVersion(document.Serialized, ConfigProfileFormat.Encode(metadata));
        var record = new ConfigMigrationRecord(input.Source, original, id, Selection().OriginalBytes, version, false);
        Write(MigrationJournal, ConfigProfileFormat.Encode(record));
        return record;
    }

    private ImmutableArray<byte> MigrationSourceBytes(ConfigMigrationSource source, string? excluded = null)
    {
        if (source is ConfigMigrationSource.Profile profile)
            return files.Read(DirectoryName(profile.Id) + ConfigName) ?? throw Conflict();
        if (source is ConfigMigrationSource.FlattenedLegacy)
            return files.Read("main" + ConfigName) ?? throw Conflict();

        if (source is not ConfigMigrationSource.EmptyHome || files.Read("main" + ConfigName) is not null)
            throw Conflict();
        foreach (string name in files.ListDirectories(Players))
        {
            if (name.StartsWith(".studio-", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ValidIdentifier(name) || files.Read(DirectoryName(name) + ConfigName) is not null)
                throw Conflict();
        }
        return [];
    }
}
