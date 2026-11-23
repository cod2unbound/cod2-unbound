namespace CoD2Unbound.Core;

public enum ConfigHistoryFilter { All, You, Imports, Checkup, Game }

public static class ConfigHistoryFilters
{
    public static bool Includes(this ConfigHistoryFilter filter, ConfigHistorySource source) => filter switch
    {
        ConfigHistoryFilter.All => true,
        ConfigHistoryFilter.You => source is ConfigHistorySource.You or ConfigHistorySource.Restore,
        ConfigHistoryFilter.Imports => source == ConfigHistorySource.Import,
        ConfigHistoryFilter.Checkup => source == ConfigHistorySource.Checkup,
        ConfigHistoryFilter.Game => source == ConfigHistorySource.GameSession,
        _ => false
    };

    public static bool HasEarlierRevision(ConfigHistoryEntry entry, IEnumerable<ConfigHistoryEntry> newestFirst) =>
        !entry.Before.Matches(entry.After) ||
        newestFirst.SkipWhile(item => item.Id != entry.Id).Skip(1).Any(item => item.After.Matches(entry.Before));
}
