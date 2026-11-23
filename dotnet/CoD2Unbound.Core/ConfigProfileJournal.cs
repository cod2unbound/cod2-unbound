using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public sealed partial class ConfigProfileStore
{
    public ConfigTransactionRecovery InspectRecovery(string id)
    {
        using var lease = files.Acquire(default);
        string directory = DirectoryName(id);
        var raw = files.Read(directory + JournalName) ?? throw Conflict();
        return new(id, ReadVersion(id, directory), raw);
    }

    public void KeepDiskVersion(ConfigTransactionRecovery recovery)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        string id = recovery.ProfileId;
        string directory = DirectoryName(id);
        string journalPath = directory + JournalName;
        CheckReceipt();

        var transaction = ConfigProfileFormat.Transaction(recovery.Journal);
        string archive = directory + "/recovery/" + transaction.Entry.Id + ".json";
        if (files.Read(archive) is { } saved)
        {
            if (!SameBytes(saved, recovery.Journal))
                throw Conflict();
        }
        else
        {
            Write(archive, recovery.Journal);
        }
        CheckReceipt();

        string staged = SnapshotPath(directory, transaction.Entry.Id);
        if (files.Read(staged) is { } snapshot)
        {
            if (!ConfigProfileFormat.SameHistory(ConfigProfileFormat.History(snapshot), transaction.Entry))
                throw Conflict();
            Delete(staged);
        }
        CheckReceipt();
        Delete(journalPath);

        void CheckReceipt()
        {
            if (!SameBytes(files.Read(journalPath), recovery.Journal) || !ReadVersion(id, directory).Matches(recovery.Current))
                throw Conflict();
        }
    }

    public ImmutableArray<ConfigHistoryEntry> History(string id)
    {
        using var lease = files.Acquire(default);
        string directory = DirectoryName(id);
        Recover(id, directory);
        ReadVersion(id, directory);
        return ReadHistory(directory);
    }

    public ConfigHistoryEntry Restore(string id, string snapshotId, ConfigProfileVersion expected,
        ConfigSnapshotRevision revision = ConfigSnapshotRevision.After)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        string directory = DirectoryName(id);
        Recover(id, directory);
        var snapshot = ReadHistory(directory).FirstOrDefault(entry => entry.Id == snapshotId) ??
            throw new ConfigProfileException(ConfigProfileFailure.InvalidHistory);
        var version = revision == ConfigSnapshotRevision.Before ? snapshot.Before : snapshot.After;
        ConfigProfileFormat.Metadata(version.Metadata, id);
        return UpdateLocked(id, expected, version, ConfigHistorySource.Restore, "Restored snapshot");
    }

    public void RecordGameSession(string id, ConfigProfileVersion before, string summary = "Game session", string? sessionId = null,
        ConfigStudioDisplayOverride? displayOverride = null)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        var read = ReadLocked(id);
        var current = read.Version;
        string directory = DirectoryName(id);
        if (displayOverride is not null)
        {
            var original = new ConfigDocument(before.Config.AsSpan(), Dialect);
            var restored = RestoreDisplayValue(read.Document, original, "r_mode", displayOverride.Resolution);
            restored = RestoreDisplayValue(restored, original, "r_monitor",
                displayOverride.Monitor?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!ReferenceEquals(restored, read.Document))
            {
                current = current with { Config = restored.Serialized };
                Write(directory + ConfigName, current.Config);
            }
        }
        if (current.Matches(before))
            return;

        var history = ReadHistory(directory);
        if (sessionId is not null)
        {
            if (!ConfigProfileFormat.ValidHistoryId(sessionId))
                throw new ConfigProfileException(ConfigProfileFailure.InvalidHistory);
            var recorded = history.FirstOrDefault(entry => entry.Id == sessionId);
            if (recorded is not null)
            {
                if (recorded.Source != ConfigHistorySource.GameSession || !recorded.Before.Matches(before))
                    throw Conflict();
                return;
            }
        }
        var snapshot = Entry(ConfigHistorySource.GameSession, summary, before, current, sessionId);
        Write(SnapshotPath(directory, snapshot.Id), ConfigProfileFormat.Encode(snapshot));
        Prune(directory);
    }

    private static ConfigDocument RestoreDisplayValue(ConfigDocument document, ConfigDocument original, string name, string? temporary)
    {
        if (temporary is null || document.EffectiveSetting(name) != temporary)
            return document;
        return original.EffectiveSettingBytes(name) is { } saved ? document.SetSetting(name, saved.AsSpan()) : document.RemoveSetting(name);
    }

    private ConfigHistoryEntry UpdateLocked(string id, ConfigProfileVersion expected, ConfigProfileVersion after,
        ConfigHistorySource source, string summary, string? entryId = null)
    {
        var original = ReadLocked(id);
        if (!original.Version.Matches(expected))
            throw Conflict();
        string directory = DirectoryName(id);
        ReadHistory(directory);
        var snapshot = Entry(source, summary, expected, after, entryId);
        var transaction = new ConfigProfileTransaction(expected, after, snapshot);
        Write(directory + JournalName, ConfigProfileFormat.Encode(transaction));
        Finish(id, directory, transaction);
        return snapshot;
    }

    private void Recover(string id, string directory)
    {
        RestoreTrashedChain(id, directory);
        if (files.Read(directory + JournalName) is not { } bytes)
            return;
        CheckWritable();
        var transaction = ConfigProfileFormat.Transaction(bytes);
        EnsureRecoverable(ReadVersion(id, directory), transaction);
        Finish(id, directory, transaction);
    }

    private void Finish(string id, string directory, ConfigProfileTransaction transaction)
    {
        var entry = transaction.Entry;
        ReadHistory(directory);
        string history = SnapshotPath(directory, entry.Id);
        if (files.Read(history) is { } existing)
        {
            if (!ConfigProfileFormat.SameHistory(ConfigProfileFormat.History(existing), entry))
                throw Conflict();
        }
        else
        {
            Write(history, ConfigProfileFormat.Encode(entry));
        }

        // Recheck after staging, then publish the helper before the config which invokes it.
        var current = ReadVersion(id, directory);
        EnsureRecoverable(current, transaction);
        var after = transaction.After;
        if (!SameBytes(current.Chain, after.Chain))
            ReplaceOptional(ChainPath(id), after.Chain);
        if (!ReadVersion(id, directory).Matches(current with { Chain = after.Chain }))
            throw Conflict();

        if (!SameBytes(current.Config, after.Config))
            Write(directory + ConfigName, after.Config);
        var afterConfig = ReadVersion(id, directory);
        if (!SameBytes(afterConfig.Config, after.Config) || !SameBytes(afterConfig.Chain, after.Chain) ||
            (!SameBytes(afterConfig.Metadata, transaction.Before.Metadata) && !SameBytes(afterConfig.Metadata, after.Metadata)))
            throw Conflict();

        if (!SameBytes(afterConfig.Metadata, after.Metadata))
            ReplaceOptional(directory + MetadataName, after.Metadata);
        if (!ReadVersion(id, directory).Matches(after))
            throw Conflict();

        Prune(directory);
        Delete(directory + JournalName);
    }

    private static void EnsureRecoverable(ConfigProfileVersion current, ConfigProfileTransaction transaction)
    {
        if ((!SameBytes(current.Config, transaction.Before.Config) && !SameBytes(current.Config, transaction.After.Config)) ||
            (!SameBytes(current.Metadata, transaction.Before.Metadata) && !SameBytes(current.Metadata, transaction.After.Metadata)) ||
            (!SameBytes(current.Chain, transaction.Before.Chain) && !SameBytes(current.Chain, transaction.After.Chain)))
            throw Conflict();
    }

    private ImmutableArray<ConfigHistoryEntry> ReadHistory(string directory)
    {
        var history = ImmutableArray.CreateBuilder<ConfigHistoryEntry>();
        foreach (string name in files.ListFiles(directory + HistoryName))
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal))
                continue;
            string id = name[..^5];
            if (!ConfigProfileFormat.ValidHistoryId(id) || files.Read(SnapshotPath(directory, id)) is not { } bytes)
                throw new ConfigProfileException(ConfigProfileFailure.InvalidHistory);
            var entry = ConfigProfileFormat.History(bytes);
            if (entry.Id != id)
                throw new ConfigProfileException(ConfigProfileFailure.InvalidHistory);
            history.Add(entry);
        }
        return history.OrderByDescending(entry => entry.Date).ThenByDescending(entry => entry.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    private void Prune(string directory)
    {
        foreach (var expired in ReadHistory(directory).Skip(HistoryRetention))
            Delete(SnapshotPath(directory, expired.Id));
    }

    private void ReplaceOptional(string path, ImmutableArray<byte>? value)
    {
        if (value is { } bytes)
            Write(path, bytes);
        else
            Delete(path);
    }
}
