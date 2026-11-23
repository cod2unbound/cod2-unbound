namespace CoD2Unbound.Core;

public sealed record ConfigShareSent(Uri Url, string Name, bool Saved);

// Used by one launcher session. The ledger is shared across its Configs pages, including receipts
// the service accepted when a local write failed. The window serializes the interactive operations.
public sealed class ConfigShareSession(ConfigShareService service, ConfigShareRecords storage, Action<UsageEvent> usage)
{
    private const string ReadProblem = "CoD2 Unbound couldn’t read its shared setups. Check that your CoD2 Unbound folder is readable and writable.";
    private ConfigShareRecordFile? records;
    private readonly HashSet<string> unsaved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConfigShareNotice> stopProblems = new(StringComparer.OrdinalIgnoreCase);
    private string? stopping;

    public bool RecordsAvailable => records is not null;
    public ConfigShareNotice? RecordsProblem => RecordsAvailable ? null : new(ConfigShareNoticeLevel.Error, "", ReadProblem);

    public async Task LoadAsync()
    {
        if (records is not null)
            return;
        try { records = await storage.LoadAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    public ConfigShareLoadout Status(string id, ConfigDirectorySnapshot? snapshot) =>
        ConfigShareLoadout.From(records?.Record(id), snapshot, string.Equals(stopping, id, StringComparison.OrdinalIgnoreCase));

    public bool CanShare(string id, bool readOnly) => records is not null && !readOnly && records.Record(id) is null;
    public bool CanStop(string id) => stopping is null && records?.Record(id) is not null;
    public ConfigShareNotice? StopProblem(string id) => stopProblems.GetValueOrDefault(id);
    public void DismissStopProblem(string id) => stopProblems.Remove(id);

    public async Task<ConfigShareForm> PrepareAsync(ConfigProfile profile, Func<ConfigProfileRead> read, IConfigImportCatalog catalog)
    {
        string name = records?.Name ?? "";
        ConfigSharePrepared? prepared = null;
        try { prepared = await ConfigShare.PrepareAsync(ConfigShare.LoadoutText(read()), catalog).ConfigureAwait(false); }
        catch (ConfigImportException error) when (error.Failure == ConfigImportFailure.TooLarge)
        {
            return new(profile.DisplayName, name, null, sourceTooLarge: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ConfigCleanException) { }
        return new(profile.DisplayName, name, prepared);
    }

    public async Task<ConfigShareSent?> PublishAsync(string id, ConfigShareForm form, ConfigDialect dialect, string app,
        bool readOnly, CancellationToken cancellationToken)
    {
        if (!CanShare(id, readOnly) || form.Prepared is not { } prepared || form.StartSending() is not { } name)
            return null;
        var published = await service.PublishAsync(id, prepared, name, dialect, app, cancellationToken).ConfigureAwait(false);
        form.Finish(published.Reply.Kind);
        if (published.Record is not { } receipt)
        {
            if (form.ShowProblemInUsage)
                usage(UsageEvent.Problem(UsageProblem.Share));
            return null;
        }

        var current = records!;
        records = current with { Name = receipt.Name, Loadouts = current.Loadouts.SetItem(id.ToLowerInvariant(), receipt) };
        if (!published.Saved)
            unsaved.Add(id);
        usage(new(UsageKind.SetupShared));
        return new(receipt.Url, receipt.Name, published.Saved);
    }

    public async Task<ConfigShareNotice?> StopAsync(ConfigProfile profile)
    {
        string id = profile.Id;
        if (stopping is not null || records is not { } current || current.Record(id) is not { } receipt)
            return null;
        DismissStopProblem(id);
        stopping = id;
        try
        {
            var result = await service.WithdrawAsync(id, receipt, CancellationToken.None).ConfigureAwait(false);
            bool updated = false;
            if (result.Reply.Kind is ConfigWithdrawReplyKind.Withdrawn or ConfigWithdrawReplyKind.Removal)
            {
                updated = result.Removed || unsaved.Remove(id);
                if (updated)
                    records = current with { Loadouts = current.Loadouts.Remove(id.ToLowerInvariant()) };
                usage(new(UsageKind.SetupWithdrawn));
            }
            var notice = ConfigShareNotice.Withdrawal(profile.DisplayName, result.Reply.Kind, updated);
            if (notice.Level == ConfigShareNoticeLevel.Error)
            {
                stopProblems[id] = notice;
                usage(UsageEvent.Problem(UsageProblem.Withdraw));
            }
            return notice;
        }
        finally { stopping = null; }
    }
}
