namespace CoD2Unbound.Core;

public sealed record ConfigSharePublication(ConfigShareReply Reply, ConfigShareRecord? Record = null, bool Saved = false);
public sealed record ConfigShareWithdrawal(ConfigWithdrawReply Reply, bool Removed = false);

public sealed class ConfigShareService(ConfigShareClient client, ConfigShareRecords records, TimeProvider? timeProvider = null)
{
    public async ValueTask<ConfigSharePublication> PublishAsync(string loadoutId, ConfigSharePrepared prepared, string name,
        ConfigDialect dialect, string app, CancellationToken cancellationToken = default)
    {
        if (!ConfigStudioLaunch.IsProfileId(loadoutId))
            throw new ArgumentException("The loadout identifier is invalid.", nameof(loadoutId));
        if (ConfigShareName.Valid(name) is not { } creditName || prepared.Problem == ConfigShareProblem.NothingToShare)
            return new(new(ConfigShareReplyKind.Unreachable));
        if (prepared.Problem == ConfigShareProblem.TooLarge)
            return new(new(ConfigShareReplyKind.TooLarge));

        var reply = await client.ShareAsync(ConfigShare.Request(prepared, creditName, dialect, app), cancellationToken).ConfigureAwait(false);
        if (reply.Receipt is not { } receipt)
            return new(reply);

        var record = new ConfigShareRecord(receipt.Request, receipt.Url, receipt.Token,
            ConfigShareRecords.Day((timeProvider ?? TimeProvider.System).GetUtcNow()), prepared.Digest, creditName);
        try
        {
            // A known success owns a withdrawal token even if the UI cancels after the reply.
            await records.RememberAsync(loadoutId, record, CancellationToken.None).ConfigureAwait(false);
            return new(reply, record, Saved: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(reply, record);
        }
    }

    public async ValueTask<ConfigShareWithdrawal> WithdrawAsync(string loadoutId, CancellationToken cancellationToken = default)
    {
        if (!ConfigStudioLaunch.IsProfileId(loadoutId))
            throw new ArgumentException("The loadout identifier is invalid.", nameof(loadoutId));

        var saved = (await records.LoadAsync(cancellationToken).ConfigureAwait(false)).Record(loadoutId);
        if (saved is null)
            return new(new(ConfigWithdrawReplyKind.Forbidden));

        var reply = await client.WithdrawAsync(saved.Request, saved.Token, cancellationToken).ConfigureAwait(false);
        if (reply.Kind is not (ConfigWithdrawReplyKind.Withdrawn or ConfigWithdrawReplyKind.Removal))
            return new(reply);

        try
        {
            bool removed = await records.ForgetIfCurrentAsync(loadoutId, saved, CancellationToken.None).ConfigureAwait(false);
            return new(reply, removed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(reply);
        }
    }
}
