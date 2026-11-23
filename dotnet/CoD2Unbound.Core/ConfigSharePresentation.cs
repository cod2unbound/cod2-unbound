using System.Globalization;

namespace CoD2Unbound.Core;

public enum ConfigSharePhase { Ready, Sending, Offline, NetworkLimit, DailyLimit, Already, NothingToShare, TooLarge, Unreadable, Sent }
public enum ConfigShareLoadoutPhase { NotShared, Waiting, Listed, Stopping }
public enum ConfigShareNoticeLevel { Info, Success, Warning, Error }

public sealed record ConfigShareNotice(ConfigShareNoticeLevel Level, string Title, string Message = "")
{
    public static ConfigShareNotice Withdrawal(string loadout, ConfigWithdrawReplyKind reply, bool updated)
    {
        return (reply, updated) switch
        {
            (ConfigWithdrawReplyKind.Withdrawn, true) => new(ConfigShareNoticeLevel.Success, $"{loadout} is no longer shared."),
            (ConfigWithdrawReplyKind.Removal, true) => new(ConfigShareNoticeLevel.Success,
                $"{loadout} comes off Player Setups with the next catalogue update."),
            (ConfigWithdrawReplyKind.Withdrawn or ConfigWithdrawReplyKind.Removal, false) => Failed(
                "The request was taken back, but CoD2 Unbound couldn’t update its record. Check that your CoD2 Unbound folder is writable."),
            (ConfigWithdrawReplyKind.Forbidden, _) => Failed(
                "cod2unbound.com doesn’t recognise this request any more, so nothing was taken back. To have the setup removed, use Ask to remove or correct this setup on its page in Player Setups."),
            _ => Failed("Couldn’t reach cod2unbound.com, so the setup is still shared. Check your connection, then choose Stop sharing again.")
        };

        ConfigShareNotice Failed(string message) => new(ConfigShareNoticeLevel.Error, $"Couldn’t stop sharing {loadout}", message);
    }
}

// This is the public part of a share record. Its private withdrawal token stays in the core.
public sealed record ConfigShareLoadout(ConfigShareLoadoutPhase Phase, string? Detail = null, Uri? RequestUrl = null,
    ConfigDirectoryEntry? Listed = null)
{
    public static ConfigShareLoadout From(ConfigShareRecord? record, ConfigDirectorySnapshot? snapshot, bool stopping = false)
    {
        if (stopping)
            return new(ConfigShareLoadoutPhase.Stopping);
        if (record is null)
            return new(ConfigShareLoadoutPhase.NotShared);
        var listing = snapshot?.Index.Entries.FirstOrDefault(entry =>
            entry.Sha256 == record.Sha256 && entry.SourceUrl.AbsoluteUri == record.Url.AbsoluteUri);
        if (listing is not null)
            return new(ConfigShareLoadoutPhase.Listed, $"Listed as “{listing.Player}”.", Listed: listing);
        string sent = DateOnly.ParseExact(record.Sent, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
        return new(ConfigShareLoadoutPhase.Waiting, $"Sent {sent} as “{record.Name}”.", record.Url);
    }
}

public sealed class ConfigShareForm(string loadout, string name, ConfigSharePrepared? prepared, bool sourceTooLarge = false)
{
    public string Name { get; set; } = name;
    public ConfigSharePrepared? Prepared { get; } = prepared;
    public ConfigSharePhase Phase { get; private set; } = sourceTooLarge ? ConfigSharePhase.TooLarge : prepared switch
    {
        null => ConfigSharePhase.Unreadable,
        { Problem: ConfigShareProblem.NothingToShare } => ConfigSharePhase.NothingToShare,
        { Problem: ConfigShareProblem.TooLarge } => ConfigSharePhase.TooLarge,
        _ => ConfigSharePhase.Ready
    };

    public string? NameProblem => ConfigShareName.Problem(Name);
    public bool CanSend => (Phase is ConfigSharePhase.Ready or ConfigSharePhase.Offline) && ConfigShareName.Valid(Name) is not null;
    public bool ShowDetails => Phase is not (ConfigSharePhase.TooLarge or ConfigSharePhase.NothingToShare or ConfigSharePhase.Unreadable);
    public string Action => Phase switch
    {
        ConfigSharePhase.Sending => "Sending…",
        ConfigSharePhase.Offline => "Try again",
        ConfigSharePhase.Sent => "Done",
        _ => "Share setup"
    };

    public ConfigShareNotice? Notice => Phase switch
    {
        ConfigSharePhase.Offline => new(ConfigShareNoticeLevel.Error, "",
            "Couldn’t reach cod2unbound.com, so nothing was sent. Check your connection."),
        ConfigSharePhase.NetworkLimit => new(ConfigShareNoticeLevel.Warning, "",
            "Three setups were shared from this network today, which is the most in a day. Try again tomorrow."),
        ConfigSharePhase.DailyLimit => new(ConfigShareNoticeLevel.Warning, "",
            "Player Setups has taken as many new setups as can be looked at today. Try again tomorrow."),
        ConfigSharePhase.Already => Info("This exact setup is already in Player Setups, or waiting to be looked at."),
        ConfigSharePhase.TooLarge => Info($"{loadout} is larger than Player Setups takes: 64 KB or 3,000 lines."),
        ConfigSharePhase.NothingToShare => Info($"{loadout} still has the game’s own settings. Change something first, then share it."),
        ConfigSharePhase.Unreadable => Info($"{loadout} couldn’t be read for sharing. Close this, open the loadout in Configs, then try again."),
        _ => null
    };

    internal bool ShowProblemInUsage => Phase is ConfigSharePhase.Offline or ConfigSharePhase.NetworkLimit
        or ConfigSharePhase.DailyLimit or ConfigSharePhase.TooLarge;

    internal string? StartSending()
    {
        if (!CanSend)
            return null;
        Phase = ConfigSharePhase.Sending;
        return ConfigShareName.Valid(Name);
    }

    internal void Finish(ConfigShareReplyKind reply) => Phase = reply switch
    {
        ConfigShareReplyKind.Sent => ConfigSharePhase.Sent,
        ConfigShareReplyKind.Already => ConfigSharePhase.Already,
        ConfigShareReplyKind.Network => ConfigSharePhase.NetworkLimit,
        ConfigShareReplyKind.Day => ConfigSharePhase.DailyLimit,
        ConfigShareReplyKind.TooLarge => ConfigSharePhase.TooLarge,
        ConfigShareReplyKind.Cancelled => ConfigSharePhase.Ready,
        _ => ConfigSharePhase.Offline
    };

    private static ConfigShareNotice Info(string message) => new(ConfigShareNoticeLevel.Info, "", message);
}
