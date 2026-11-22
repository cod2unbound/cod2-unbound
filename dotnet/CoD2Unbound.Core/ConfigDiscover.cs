using System.Collections.Immutable;
using System.Globalization;

namespace CoD2Unbound.Core;

public enum ConfigDiscoverPhase { Idle, Loading, Loaded, Unavailable, Failed }
public enum ConfigSetupFilter { Featured, Classic, Cod2x, Modern, All }
public sealed record ConfigDiscoverState(ConfigDiscoverPhase Phase, ConfigDirectorySnapshot? Snapshot = null,
    DateTimeOffset? Fetched = null, string? Problem = null, ImmutableArray<ConfigDirectorySavedEntry> Saved = default);
public sealed record ConfigSetupShelf(ConfigSetupFilter Filter, ImmutableArray<ConfigDirectoryEntry> Entries, int Total);
public sealed record ConfigSetupTeam(string Name, int Count);
public sealed record ConfigEraShelf(string Era, ImmutableArray<ConfigDirectoryEntry> Entries);

/// <summary>Player Setups accepts only data returned by a verified directory.</summary>
public sealed class ConfigDiscover(ConfigDirectory directory, ConfigCatalog catalog)
{
    private const double StockYaw = 0.022;
    private readonly SemaphoreSlim gate = new(1);
    private ConfigDiscoverState state = new(ConfigDiscoverPhase.Idle, Saved: []);
    public ConfigDiscoverState State => Volatile.Read(ref state);
    public ImmutableArray<ConfigDirectoryEntry> Entries => State.Snapshot?.Index.Entries ?? [];

    public async Task LoadAsync(bool retry = false, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = State;
            if (!retry && previous.Phase != ConfigDiscoverPhase.Idle)
                return;
            Publish(new(ConfigDiscoverPhase.Loading, Saved: []));
            try
            {
                var result = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (result.Snapshot is not { } snapshot)
                {
                    Publish(new(ConfigDiscoverPhase.Unavailable,
                        Problem: result.UnavailableReason ?? "The player directory isn’t available.", Saved: []));
                    return;
                }
                ImmutableArray<ConfigDirectorySavedEntry> saved = [];
                try { saved = await directory.SavedAsync(snapshot, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or ConfigDirectoryException or UnauthorizedAccessException) { }
                var fetched = await directory.CacheDateAsync(cancellationToken).ConfigureAwait(false);
                Publish(new(ConfigDiscoverPhase.Loaded, snapshot, fetched, result.RefreshError, saved));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Publish(previous);
                throw;
            }
            catch (Exception error) when (error is IOException or ConfigDirectoryException or UnauthorizedAccessException)
            {
                Publish(new(ConfigDiscoverPhase.Failed, Problem: error.Message, Saved: []));
            }
        }
        finally { gate.Release(); }
    }

    public async Task<string?> SetSavedAsync(string id, bool value, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State.Snapshot is not { } snapshot)
                return null;
            await directory.SetSavedAsync(id, value, snapshot, cancellationToken).ConfigureAwait(false);
            Publish(State with { Saved = await directory.SavedAsync(snapshot, cancellationToken).ConfigureAwait(false) });
            return value ? "Saved for later." : "Removed from Saved.";
        }
        finally { gate.Release(); }
    }

    public async Task<ConfigImportSession> ReviewAsync(string id, ConfigDialect dialect, ConfigProfileRead? active,
        ConfigImportPlatform destination = ConfigImportPlatform.Windows, CancellationToken cancellationToken = default)
    {
        var snapshot = State.Snapshot ?? throw new InvalidOperationException("The player directory isn’t available.");
        var entry = snapshot.Index.Entries.FirstOrDefault(item => item.Id == id) ??
            throw new InvalidOperationException("This player is no longer in the directory.");
        var adapter = new ConfigImportCatalogAdapter(catalog);
        var bundle = await directory.LoadProfileAsync(id, snapshot, dialect, adapter, cancellationToken).ConfigureAwait(false);
        var origin = new ConfigOrigin(ConfigOriginKind.Imported, entry.SourceUrl.AbsoluteUri, entry.Credit);
        var session = new ConfigImportSession(bundle, dialect, adapter, entry.Player,
            entry.Platform == ConfigDirectoryPlatform.Windows ? ConfigImportPlatform.Windows : ConfigImportPlatform.Native,
            destination, origin);
        if (active is not null)
            session.SetTarget(new ConfigImportTarget.Existing(active));
        return session;
    }

    public static double? SetupTurnDistance(ConfigImportSession review, double dpi)
    {
        var settings = review.Rows.Where(row => row.NewValue is not null && row.Candidate.SelectedByDefault &&
            (row.CanSelect || (!row.LockedNames.IsEmpty && row.PlatformNote is null)));
        var sensitivity = settings.LastOrDefault(row => row.Title.Equals("Set sensitivity", StringComparison.OrdinalIgnoreCase));
        var yaw = settings.LastOrDefault(row => row.Title.Equals("Set m_yaw", StringComparison.OrdinalIgnoreCase));
        return TurnDistance(dpi, sensitivity?.NewValue, yaw?.NewValue);
    }

    public static double? LoadoutTurnDistance(ConfigDocument document, double dpi, string? defaultSensitivity = null) =>
        TurnDistance(dpi, document.EffectiveSetting("sensitivity") ?? defaultSensitivity, document.EffectiveSetting("m_yaw"));

    private static double? TurnDistance(double dpi, string? sensitivity, string? yaw) =>
        sensitivity is not null && ConfigValues.Number(sensitivity) is { } value
            ? MouseDistance.Centimetres(dpi, value, yaw is null ? StockYaw : Math.Abs(ConfigValues.Number(yaw) ?? 0))
            : null;

    public static ImmutableArray<ConfigSetupFilter> Filters(ImmutableArray<ConfigDirectoryEntry> entries) =>
        [.. Enum.GetValues<ConfigSetupFilter>().Where(filter => entries.Any(entry => Keeps(filter, entry)))];

    public static ImmutableArray<ConfigDirectoryEntry> Setups(ImmutableArray<ConfigDirectoryEntry> entries,
        ConfigSetupFilter filter, string query, CultureInfo culture)
    {
        string search = query.Trim();
        if (!Filters(entries).Contains(filter))
            filter = ConfigSetupFilter.All;
        return [.. entries.Where(entry => search.Length == 0 ? Keeps(filter, entry) :
            new[] { entry.Player, entry.Team, entry.Role, entry.Era, entry.Feel, entry.Provenance.PublishedBy }
                .OfType<string>().Concat(entry.StyleTags)
                .Any(text => culture.CompareInfo.IndexOf(text, search, CompareOptions.IgnoreCase) >= 0))
            .OrderBy(entry => entry.Player, StringComparer.Create(culture, ignoreCase: true))];
    }

    public static ImmutableArray<ConfigDirectoryEntry> Heroes(ImmutableArray<ConfigDirectoryEntry> entries)
    {
        var featured = entries.Where(item => item.Featured == true).Take(5).ToImmutableArray();
        return featured.IsEmpty ? [.. entries.Take(5)] : featured;
    }

    public static ImmutableArray<ConfigSetupShelf> StyleShelves(ImmutableArray<ConfigDirectoryEntry> entries, CultureInfo culture)
    {
        ConfigSetupFilter[] filters = [ConfigSetupFilter.Classic, ConfigSetupFilter.Cod2x, ConfigSetupFilter.Modern];
        return [.. filters.Select(filter =>
        {
            var matching = entries.Where(entry => Keeps(filter, entry)).ToArray();
            return new ConfigSetupShelf(filter, [.. matching.OrderByDescending(entry => entry.Featured == true)
                .ThenBy(entry => entry.Player, StringComparer.Create(culture, ignoreCase: true)).Take(10)], matching.Length);
        }).Where(shelf => shelf.Total > 0)];
    }

    public static ImmutableArray<ConfigEraShelf> EraShelves(ImmutableArray<ConfigDirectoryEntry> entries) =>
        [.. entries.GroupBy(entry => entry.Era, StringComparer.Ordinal).Select(group => new ConfigEraShelf(group.Key, [.. group]))];

    public static ImmutableArray<ConfigSetupTeam> Teams(ImmutableArray<ConfigDirectoryEntry> entries, CultureInfo culture) =>
        [.. entries.Select(entry => entry.Team).OfType<string>().Where(team => team.Length > 0).GroupBy(team => team)
            .Select(group => new ConfigSetupTeam(group.Key, group.Count())).Where(team => team.Count > 1)
            .OrderByDescending(team => team.Count).ThenBy(team => team.Name, StringComparer.Create(culture, ignoreCase: true))];

    private static bool Keeps(ConfigSetupFilter filter, ConfigDirectoryEntry entry) => filter switch
    {
        ConfigSetupFilter.Featured => entry.Featured == true,
        ConfigSetupFilter.Classic => entry.StyleTags.Contains("Classic era"),
        ConfigSetupFilter.Cod2x => entry.StyleTags.Contains("CoD2x era"),
        ConfigSetupFilter.Modern => entry.StyleTags.Contains("Modern"),
        _ => true
    };

    private void Publish(ConfigDiscoverState next) => Volatile.Write(ref state, next);
}

public sealed record ConfigLauncherCommands(bool UseLoadouts, bool New, bool Duplicate, bool Rename, bool Export,
    bool Delete, bool Import)
{
    public static ConfigLauncherCommands For(ConfigStudioState? state, bool canNavigate, bool setupComplete,
        bool gameRunning, bool hasCatalog, bool importOpen)
    {
        bool selected = state?.Current is not null;
        bool write = canNavigate && state is { ReadOnly: false, Draft: null };
        bool use = canNavigate && selected;
        return new(use, write, write && selected, write && selected, use && hasCatalog,
            write && state?.Current is { Profile.IsActive: false }, canNavigate && setupComplete && !gameRunning &&
                !importOpen && state is { ReadOnly: false, Draft: null });
    }
}
