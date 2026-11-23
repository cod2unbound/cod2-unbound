using System.ComponentModel;
using System.Diagnostics;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed partial class WindowsLauncherSession
{
    private const string SwitchVersionWarning = "switch-version";
    private readonly SemaphoreSlim serverPreferencesWrites = new(1);
    private readonly Lazy<ConfigCatalog> serverCatalog = new(ConfigCatalog.LoadBundled);
    private readonly Lazy<ConfigRules> serverRules = new(ConfigRules.LoadBundled);

    internal ConfigCatalog ServerCatalog => serverCatalog.Value;
    internal ConfigRules ServerRules => serverRules.Value;
    internal bool AlwaysUseServerVersion => Settings.Working.DismissedWarnings.Contains(SwitchVersionWarning);

    internal Task RememberServerVersionChoiceAsync() => UpdateSettingsAsync(Settings.Working with
    {
        DismissedWarnings = Settings.Working.DismissedWarnings.Add(SwitchVersionWarning)
    });

    internal ServerPasswordStore ServerPasswords => new(credentials);

    internal ServerReadySession OpenServerReadySession(GameEdition edition)
    {
        var studio = OpenServerConfigStudio(edition);
        return new(new(ServerCatalog, ServerRules, TimeProvider.System), ServerQuery, studio);
    }

    internal bool HasTemporaryServerLoadout(GameEdition edition) => OpenServerConfigStudio(edition).IsTemporaryServerLoadout;

    private ConfigStudioSession OpenServerConfigStudio(GameEdition edition) =>
        new(new WindowsConfigProfiles(HomePath, 0), edition == GameEdition.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock,
            () => ClientIsRunning, TimeProvider.System);

    internal async Task ClearRecentServersAsync()
    {
        // Windows stores the actual recent list and its timestamps in the same library record.
        await UpdateServerPreferencesAsync(preferences => preferences with { LaunchedAt = preferences.LaunchedAt.Clear() });
        await UpdateLibraryAsync(library => library.ClearRecent());
    }

    internal Task SaveFiltersAsync(ServerFilters filters) =>
        UpdateServerPreferencesAsync(preferences => preferences with { Filters = filters });

    private async Task UpdateServerPreferencesAsync(Func<ServerBrowserPreferences, ServerBrowserPreferences> update)
    {
        await serverPreferencesWrites.WaitAsync();
        try
        {
            var preferences = update(ServerPreferences);
            if (preferences == ServerPreferences)
                return;
            await preferences.SaveAsync(folder);
            ServerPreferences = preferences;
        }
        finally { serverPreferencesWrites.Release(); }
    }

    internal Task AddFavoriteAsync(string address) =>
        UpdateLibraryAsync(library => library with { Favorites = library.Favorites.Add(address) });

    internal Task RememberServerAsync(GameServer server) =>
        UpdateLibraryAsync(library => WithServerObservation(library, server));

    internal async Task<GameServer?> RefreshServerAsync(string address)
    {
        var server = await ServerQuery.ServerAsync(address);
        if (server is not null)
            await RememberServerAsync(server);
        return server;
    }

    internal GameEdition? RunningEdition(GameProcessIdentity game)
    {
        try
        {
            using var process = Process.GetProcessById(game.Id);
            if (WindowsEngineProcess.CreationTime(process.SafeHandle) == game.CreatedAt && process.MainModule?.FileName is { } image)
                return inventory.Installed.FirstOrDefault(binary => binary.Role == EngineRole.Client &&
                    string.Equals(binary.Path, image, StringComparison.OrdinalIgnoreCase))?.Edition;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
        return null;
    }

    internal async Task<bool> DeliverJoinAsync(GameProcessIdentity game, LaunchLink link, GameEdition edition,
        UsageJoin source, GameServer? observation)
    {
        if (RunningEdition(game) != edition)
            return false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            if (await Game.ConnectToRunningAsync(game, link, deadline.Token) != GameLinkDelivery.Delivered)
                return false;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return false; }
        await RecordJoinAsync(link.Address, edition, source, observation);
        return true;
    }

    internal bool ReturnToRunningGame(GameProcessIdentity game) => WindowsRunningGameJoin.Activate(game);

    private Task RecordJoinAsync(string address, GameEdition edition, UsageJoin source, GameServer? observation)
    {
        Record(UsageEvent.Join(source, edition));
        return UpdateLibraryAsync(library =>
        {
            var joined = library.Joined(LaunchLink.CanonicalAddress(address), TimeProvider.System.GetUtcNow());
            return observation is null ? joined : WithServerObservation(joined, observation);
        });
    }

    private static LauncherLibrary WithServerObservation(LauncherLibrary library, GameServer observation) => library with
    {
        Cache = [.. library.Cache.Where(server => !string.Equals(server.Address, observation.Address, StringComparison.OrdinalIgnoreCase)),
            observation]
    };
}
