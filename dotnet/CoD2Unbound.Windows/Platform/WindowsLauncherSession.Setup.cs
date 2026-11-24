using System.Collections.Immutable;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed partial class WindowsLauncherSession
{
    private SetupSession? priorSetup;

    internal bool ReviewingSetup => priorSetup is not null;
    internal bool SetupActive => !SetupComplete || ReviewingSetup;
    internal ImmutableArray<GameInstallation> SetupCopies { get; private set; } = [];
    private (string DataPath, (WindowsProfileDiscovery Found, string[] Searched) Result)? installedProfiles;
    private ConfigDialect PlayerDialect => Settings.Working.Engine == GameEdition.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock;

    internal async Task StartSetupAgainAsync()
    {
        RequireStopped();
        await Settings.FlushAsync();
        var review = await CreateSetupAsync(Setup.DataPath, Setup.HasStoredKey, SetupStep.Data, wizard: true, review: true);
        priorSetup = Setup;
        Setup = review;
    }

#if UPDATE_FIXTURES
    /// <summary>Fixture builds only: stands in for discovery so Setup's picture states can be drawn.</summary>
    internal void PreviewSetupCopies(ImmutableArray<GameInstallation> copies) => SetupCopies = copies;

#endif
    internal bool CancelSetup()
    {
        if (!Setup.Cancel() || priorSetup is not { } previous)
            return false;
        Setup = previous;
        priorSetup = null;
        return true;
    }

    internal async Task<bool> CommitSetupAsync()
    {
        await using var lease = await WindowsGameSessionHost.OpenLeaseAsync(HomePath, ".launcher-start.lock", default);
        RequireStopped();
        if (!await Setup.ContinueAsync(applyReview: ApplyReviewAsync))
            return false;
        priorSetup = null;
        await FinishSetupAsync();
        installedProfiles = null;
        await FindInstalledProfilesAsync(null, default);
        return true;
    }

    private async ValueTask ApplyReviewAsync()
    {
        if (Setup.Personalization is { } edit)
        {
            if (edit.Name is { } name)
                await SaveNameUnderLaunchLeaseAsync(name, id: null);
            if (edit.Display != Settings.Working.Fullscreen)
                await UpdateSettingsAsync(Settings.Working with { Fullscreen = edit.Display });
        }
        if (Setup.CrashChoice is { } crash)
            await SetCrashAsync(crash);
        if (Setup.UsageChoice is { } usage)
            await SetUsageAsync(usage, UsageSource.Setup);
    }

    internal ValueTask<string?> ReadRawPlayerNameAsync() =>
        ConfigProfileStore.ReadPlayerNameAsync(gameFolder, PlayerDialect, preserveColors: true);

    internal async Task SavePlayerNameAsync(string id, string name)
    {
        await using var launch = await WindowsGameSessionHost.OpenLeaseAsync(HomePath, ".launcher-start.lock", default);
        await SaveNameUnderLaunchLeaseAsync(name, id);
    }

    private async Task SaveNameUnderLaunchLeaseAsync(string name, string? id)
    {
        await using var profile = await WindowsGameSessionHost.OpenLeaseAsync(HomePath, "main/players/.studio.lock", default);
        RequireStopped();
        var settings = Settings.Working;
        var dialect = PlayerDialect;
        await Task.Run(() =>
        {
            var profileFiles = new WindowsConfigProfiles(HomePath, 0, profile);
            var store = new ConfigProfileStore(profileFiles, dialect, () => ClientIsRunning);
            if (id is null)
            {
                if (store.ActiveSelection().ProfileId is null)
                    new ConfigStudioSession(profileFiles, dialect, () => ClientIsRunning, TimeProvider.System).MigrateLegacySettings(settings);
                id = store.ActiveSelection().ProfileId ?? throw new ConfigProfileException(ConfigProfileFailure.MissingProfile);
            }
            store.SavePlayerName(id, name);
        });
    }

    private void RequireStopped()
    {
        if (ClientIsRunning)
            throw new ConfigProfileException(ConfigProfileFailure.GameRunning);
    }

    private Task<ImmutableArray<GameInstallation>> FindGameDataAsync(string? preferred) => Task.Run(async () =>
    {
        var installs = await discovery.DiscoverAsync(default);
        var candidates = SetupDiscovery.Complete(files, preferred, installs);
        if (!string.IsNullOrEmpty(preferred) && !candidates.Any(copy => SamePath(copy.DataPath, preferred)) &&
            GameArchives.Survey(files, preferred).IsComplete)
            return candidates.Insert(0, new(preferred, []));
        return candidates;
    });

    internal static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal Task<bool> CanOpenSteamAsync() => Task.Run(WindowsGameDiscovery.SteamInstalled);

    internal Task<GameInstallation> WaitForSteamAsync(CancellationToken token) => Task.Run(() =>
        SetupDiscovery.WaitForSteamAsync(files, discovery.DiscoverAsync, TimeSpan.FromSeconds(2), token), token);

    internal string? ReadDiscKeyForSetup() => Setup.Step == SetupStep.Key && FixtureHome() is null
        ? WindowsGameDiscovery.ReadDiscKey() : null;

    internal async Task<(WindowsProfileDiscovery Found, string[] Searched)> FindInstalledProfilesAsync(
        string? folder, CancellationToken token)
    {
        if (folder is not null)
            return await Task.Run(() => WindowsInstalledProfiles.Find(folder, [], token), token);

        string dataPath = Setup.DataPath;
        if (installedProfiles is { } saved && SamePath(saved.DataPath, dataPath))
            return saved.Result;

        try
        {
            var result = await Task.Run(async () => await discovery.DiscoverProfilesAsync(dataPath, token), token);
            installedProfiles = (dataPath, result);
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (new([], false, 1), []);
        }
    }

    internal LaunchRecovery RecoverLaunch(GameEdition edition, string message) =>
        LaunchRecovery.Classify(Setup.DataPath, GameArchives.Survey(files, Setup.DataPath).IsComplete, HasClient(edition), message);

    internal async Task<SetupDataChoice> RepairGameDataAsync(string path)
    {
        var change = await ChangeGameDataAsync();
        var choice = change.ChooseData(path, picked: true);
        if (choice.State == SetupDataState.Found && await change.ContinueAsync())
        {
            SetGameData(choice.Path);
            Record(new(UsageKind.SettingChanged, (int)UsageSetting.GameDataFolder));
        }
        return change.Data;
    }
}
