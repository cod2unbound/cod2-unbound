using System.Collections.Immutable;
using System.Text;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

/// <summary>Composes the platform adapters with the shared setup and settings sessions.</summary>
internal sealed partial class WindowsLauncherSession
{
    private readonly WindowsFiles files = new();
    private readonly ISettingsFolder folder;
    private readonly ISecretStore credentials;
    private readonly ISecretStore serverCredentials;
    private readonly WindowsGameDiscovery discovery;
    private readonly WindowsSettingsFolder gameFolder;
    private readonly WindowsEngineInventory inventory;
    private readonly WindowsGameSessionHost gameHost;
    internal GameLifecycle Game { get; }
    internal string? MigrationProblem => gameHost.MigrationProblem;
    internal WindowsReports Reports { get; }
    internal ConfigShareSession Share { get; }
    private (DateTimeOffset Started, GameEdition Edition, int Process, bool CrashConsent, Task<UsageGameStart?> UsageStart)? launchedGame;
    private const string UsageUpdateReceipt = "usage-update.txt";
    private (UpdatePhase Phase, string? Version, UpdateFailureStage? Failed, UpdateIssue Issue)? observedUpdate;
    internal ServerQueryClient ServerQuery { get; }
    internal UdpEndpoint[]? TestMaster { get; }
    internal ServerBrowserPreferences ServerPreferences { get; private set; } = ServerBrowserPreferences.Default;
    internal ServerBrowser Browser { get; }
    private readonly SemaphoreSlim libraryWrites = new(1);
    internal LauncherLibrary Library { get; private set; } = new();
    internal WindowsRenderMode Renderer => Settings.Working.WindowsRenderer ?? WindowsRenderMode.Direct3D9;
    internal LauncherUpdates Updates { get; }
    internal WindowsLauncherUpdate UpdateInstaller { get; }
    private UpdateOutcome lastUpdateOutcome;
    internal bool InstallNeedsRepair => UpdateInstaller.InstallNeedsRepair;
    internal bool SetupUpdating => lastUpdateOutcome == UpdateOutcome.Waiting;
    internal WindowsHosting Hosting { get; }
    private const string RendererFile = "windows-renderer.txt";
    internal string HomePath => gameFolder.Root;
    internal event Action? ClientStarted;

    private static string? FixtureHome()
    {
#if UPDATE_FIXTURES
        if (Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_MASTER_PORT") is not null)
            return Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_HOME");
#endif
        return null;
    }

#if UPDATE_FIXTURES
    internal static bool HasFixtureHome => FixtureHome() is not null;
#endif

    internal static string ResolveHome()
    {
        string gameHome = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoD2 Unbound");
#if UPDATE_FIXTURES
        // Native clients accept private homes under this root. Fixture owns a child of it.
        return FixtureHome() ?? Path.Join(gameHome, "Fixture");
#else
        return gameHome;
#endif
    }

    internal WindowsLauncherSession()
    {
        bool useWindowsCredentials = FixtureHome() is null;
        bool rejectFixturePasswordWrites = false;
#if UPDATE_FIXTURES
        if (Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_MASTER_PORT") is { } test)
            TestMaster = int.TryParse(test, out int port) && port is > 0 and <= 65535 ? [new("127.0.0.1", port)] : [];
        useWindowsCredentials |= TestMaster is { Length: > 0 } &&
            Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_WINDOWS_CREDENTIALS") == "1";
        rejectFixturePasswordWrites = TestMaster is not null &&
            Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_SERVER_PASSWORD_WRITE_FAILURE") == "1";
#endif
        bool hasTestSlot = WindowsCredentials.ResolveTestSlot() is not null;
        var probeCredentials = new ProbeCredentials(rejectFixturePasswordWrites);
        credentials = useWindowsCredentials || hasTestSlot ? new WindowsCredentials() : probeCredentials;
        serverCredentials = hasTestSlot ? probeCredentials : credentials;
        gameFolder = new WindowsSettingsFolder(ResolveHome(), allowNestedWrites: true);
        var settingsFolder = new WindowsSettingsFolder(Path.Join(HomePath, "launcher"));
        folder = settingsFolder;
        Reports = new(HomePath, settingsFolder, Version);
        var shares = new ConfigShareRecords(new WindowsConfigShareStorage(settingsFolder));
        Share = new(new(new(new HttpConfigShareTransport(), WindowsConfigShareStorage.ServiceAddress()), shares), shares, Record);
        string installRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, ".."));
        inventory = new WindowsEngineInventory(installRoot, files);
        discovery = new WindowsGameDiscovery(inventory);
        var processes = new WindowsProcessSupervisor(inventory, Path.Join(folder.Root, "logs"));
        gameHost = new(inventory, files, HomePath, () => Settings.Working, () => Renderer, SaveIntroAsync);
        Game = new GameLifecycle(processes, gameHost, files, TimeProvider.System);
        ServerQuery = new ServerQueryClient(new WindowsUdpTransport(TestMaster is not null));
        Browser = new ServerBrowser(ServerQuery, TimeProvider.System);
        Hosting = new(HomePath, inventory, files, TestMaster is not null);
        UpdateInstaller = new(installRoot, HomePath, Version, inventory, () => new(Game.State.Running, Hosting.HasActiveServers),
            FlushForUpdateAsync);
        Updates = new LauncherUpdates(new WindowsReleaseSource(TestMaster is not null), TimeProvider.System,
            Version, (version, assets) => WindowsReleaseAssets.Offer(version, assets, WindowsReleaseSource.LocalAssetOrigin),
            TimeZoneInfo.Local, WindowsReleaseAssets.IsNewer, UpdateInstaller.Log, (version, assets) =>
            {
                _ = WindowsReleaseAssets.Offer(version, assets, WindowsReleaseSource.LocalAssetOrigin, out var problem);
                return problem ?? "The release did not provide a usable update.";
            });
    }

    internal LauncherSettingsSession Settings { get; private set; } = null!;
    internal SetupSession Setup { get; private set; } = null!;
    internal bool SetupComplete { get; private set; }

    internal async Task InitializeAsync()
    {
        foreach (string name in new[] { GameSettingsStorage.FileName, SetupSession.DataPathFile, SetupSession.CompletionFile, LauncherLibrary.FileName })
        {
            if (await folder.ReadAsync(name, default) is null && await gameFolder.ReadAsync(name, default) is { } bytes)
                await folder.WriteAsync(name, bytes, default);
        }
        Library = await LauncherLibrary.LoadAsync(folder);
        ServerPreferences = await ServerBrowserPreferences.LoadAsync(folder);
        var storage = new GameSettingsStorage(folder);
        Settings = new LauncherSettingsSession(await storage.ReadAsync(), storage, TimeProvider.System);
        if (await folder.ReadAsync(RendererFile, default) is { } renderer)
        {
            if (Settings.Working.WindowsRenderer is null && renderer.AsSpan().SequenceEqual("dx7\n"u8))
                await UpdateSettingsAsync(Settings.Working with { WindowsRenderer = WindowsRenderMode.DirectX7 });
            await folder.DeleteAsync(RendererFile, default);
        }
        SetupComplete = await folder.ReadAsync(SetupSession.CompletionFile, default) is not null;
        await Reports.InitializeAsync(Settings.Working, SetupComplete);
        if (Reports.Usage.Available && Reports.Usage.Consent != Settings.Working.UsageConsent)
            await UpdateSettingsAsync(Settings.Working.WithUsageConsent(Reports.Usage.Consent));
        string? remembered = await folder.ReadAsync(SetupSession.DataPathFile, default) is { } path
            ? Encoding.UTF8.GetString(path.AsSpan()).Trim()
            : null;
        bool hasKey = SetupSession.KeyTail(await credentials.ReadAsync(SetupSession.KeySecret, default)) is not null;
        Setup = SetupComplete && !string.IsNullOrEmpty(remembered)
            ? new SetupSession(files, folder, credentials, remembered, hasKey, step: SetupStep.Complete)
            : await CreateSetupAsync(remembered, hasKey, SetupComplete ? SetupStep.Complete : SetupStep.Welcome,
                wizard: !SetupComplete);
        RefreshUpdateOutcome();
        await RecordInstalledUpdateAsync();
    }

    internal string Version => typeof(WindowsLauncherSession).Assembly.GetName().Version!.ToString(3);

    internal async Task RecordStartupAsync(LauncherPlace route, string? homeLayout)
    {
        // Overwrite the previous startup result. Only status fields belong in this log.
        string status = FormattableString.Invariant(
            $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} route={route} home={homeLayout ?? "-"} game={Setup.Data.State} key={(Setup.HasStoredKey ? "Recognised" : "Missing")}\n");
        try
        {
            await folder.WriteAsync("startup.log", [.. Encoding.UTF8.GetBytes(status)], default);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupTrace.Mark($"startup log unavailable (0x{error.HResult:X8})");
        }
    }

    internal GameServer ServerForAddress(string address) =>
        Library.Cache.FirstOrDefault(server => server.Address == address) ??
        new GameServer(address, ImmutableDictionary<string, string>.Empty, -1, []);

    internal MapPixels? ReadMapArtwork(string map) =>
        MapArtwork.Find(map, MapArtwork.ArchiveOrder.Select<string, Func<Stream>>(name =>
            () => WindowsFiles.Open(Setup.DataPath, name)));

    internal ImmutableArray<MatchSummary> ReadMatches()
    {
        try
        {
            using var input = WindowsFiles.Open(HomePath, "main/stats/matches.jsonl");
            return MatchSummaries.Read(input);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    internal async Task<GameStartState> JoinAsync(ServerAddress address, GameEdition? edition = null, UsageJoin source = UsageJoin.List,
        GameServer? observation = null, ServerLaunchLoadout? loadout = null)
    {
        var result = await LaunchAsync(LaunchLink.Direct(address.Address, address.Password ?? ""), edition, serverLoadout: loadout);
        if (result == GameStartState.Started)
            await RecordJoinAsync(address.Address, edition ?? Settings.Working.Engine, source, observation);
        return result;
    }

    internal Task<SetupSession> ChangeGameDataAsync() => CreateSetupAsync(Setup.DataPath, Setup.HasStoredKey, SetupStep.Data);

    internal void SetGameData(string path) => Setup.ChooseData(path);
    internal async ValueTask<string?> KeyEndingAsync() =>
        SetupSession.KeyTail(await credentials.ReadAsync(SetupSession.KeySecret, default));

    internal async Task<bool> ReplaceKeyAsync(string key)
    {
        await using var lease = await WindowsGameSessionHost.OpenLeaseAsync(HomePath, ".launcher-start.lock", default);
        RequireStopped();
        var change = new SetupSession(files, folder, credentials, Setup.DataPath, Setup.HasStoredKey, step: SetupStep.Key);
        change.ReplaceKey();
        if (!await change.ContinueAsync(key))
            return false;
        Setup = new SetupSession(files, folder, credentials, Setup.DataPath, change.HasStoredKey, step: Setup.Step);
        Record(new(UsageKind.SettingChanged, (int)UsageSetting.CdKey));
        return true;
    }

    internal Task DeleteMatchHistoryAsync() => gameFolder.DeleteAsync("main/stats/matches.jsonl", default).AsTask();
    internal string LogsPath => Path.Join(folder.Root, "logs");
    internal async Task FinishSetupAsync()
    {
        SetupComplete = Setup.Step == SetupStep.Complete;
        if (SetupComplete)
            await Reports.Usage.HoldAsync(false);
    }

    internal void Record(UsageEvent? item)
    {
        if (Reports.Usage is { } usage)
            _ = usage.RecordAsync(item);
    }

    internal void Opened(UsagePage page) => Record(UsageEvent.Page(page));
    internal void ShowError(UsageProblem kind) => Record(UsageEvent.Problem(kind));

    internal async Task SetUsageAsync(bool enabled, UsageSource source)
    {
        if (!await Reports.Usage.SetConsentAsync(enabled ? UsageConsent.Yes : UsageConsent.No, source,
            answer => UpdateSettingsAsync(Settings.Working.WithUsageConsent(answer))))
            throw new IOException("The usage data choice could not be saved.");
        if (!enabled)
            await ClearUsageUpdateAsync();
    }

    internal async Task ResetUsageAsync()
    {
        if (!await Reports.Usage.ResetAsync())
            throw new IOException("The install number could not be reset.");
        await ClearUsageUpdateAsync();
    }

    internal async Task SetCrashAsync(bool enabled)
    {
        if (!enabled)
            Reports.CancelCrash();
        await UpdateSettingsAsync(Settings.Working with { SendCrashReports = enabled, CrashReportsAsked = true });
    }

    internal Task SetCoD2xReportsAsync(bool enabled) =>
        UpdateSettingsAsync(Settings.Working with { SendCoD2xReports = enabled });

    internal ValueTask<ConfigProfile?> ReadLoadoutAsync() => ConfigProfileStore.ReadActiveForDisplayAsync(gameFolder, dialect: PlayerDialect);

    internal async Task<ImmutableArray<ConfigProfile>> ReadLoadoutChoicesAsync()
    {
        string players = Path.Join(HomePath, "main", "players");
        try
        {
            using var root = new WindowsFiles.DirectoryHandles(players);
            var ids = new DirectoryInfo(players).EnumerateDirectories()
                .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0).Select(entry => entry.Name);
            var dialect = Settings.Working.Engine == GameEdition.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock;
            return await ConfigProfileStore.ReadChoicesAsync(gameFolder, ids, dialect: dialect);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return []; }
    }

    internal async Task SelectLoadoutAsync(string id)
    {
        await using var launch = await WindowsGameSessionHost.OpenLeaseAsync(HomePath, ".launcher-start.lock", default);
        await using var profile = await WindowsGameSessionHost.OpenLeaseAsync(HomePath, "main/players/.studio.lock", default);
        if (Game.State.Running || !inventory.FindRunning(EngineRole.Client, default).IsEmpty)
            throw new ConfigProfileException(ConfigProfileFailure.GameRunning);
        await ConfigProfileStore.SelectForNextLaunchAsync(gameFolder, id);
    }

    internal async Task<(string Name, string? Loadout, string Key)> ReadPlayerAsync()
    {
        var loadout = await ReadLoadoutAsync();
        string? name = await ReadRawPlayerNameAsync();
        return (name ?? SetupPlayer.Unknown, loadout?.DisplayName,
            Setup.HasStoredKey ? "CD key verified" : "No CD key stored");
    }

    internal bool ClientIsRunning => Game.State.Running || !inventory.FindRunning(EngineRole.Client, default).IsEmpty;

    internal bool HasClient(GameEdition edition) =>
        inventory.Installed.Any(binary => binary is { Role: EngineRole.Client } && binary.Edition == edition);

    internal async Task<GameStartState> LaunchAsync(LaunchLink? link = null, GameEdition? edition = null, string? demo = null,
        ServerLaunchLoadout? serverLoadout = null)
    {
        if (SetupUpdating)
            throw new IOException("Wait for the launcher update to finish before opening the game.");
        var settings = Settings.Working;
        var request = new ClientLaunchRequest(edition ?? settings.Engine, settings, Setup.DataPath, HomePath) { Link = link, Demo = demo, ServerLoadout = serverLoadout };
        var usage = Reports.Usage;
        var installAtLaunch = usage.InstallNumber;
        GameStartState start;
        try { start = await Game.StartAsync(request, SetupComplete); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LaunchPlanException or System.ComponentModel.Win32Exception)
        {
            ShowError(UsageProblem.GameStart);
            throw;
        }
        if (start is GameStartState.NeedsSetup or GameStartState.MissingData)
            ShowError(UsageProblem.GameData);
        if (start == GameStartState.Started)
        {
            launchedGame = (Game.State.StartedAt ?? DateTimeOffset.UtcNow, request.Edition, Game.State.Games[0].Id,
                SetupComplete && settings.CrashReportsAsked && settings.SendCrashReports,
                usage.RecordGameStartAsync(installAtLaunch, request.Edition, Renderer, UsageEvent.ModeFor(settings.Fullscreen)));
            ClientStarted?.Invoke();
        }
        return start;
    }

    internal void GameObserved(GameSessionState state)
    {
        if (state.Running || state.LastExit is not { } exit || launchedGame is not { } launched)
            return;
        launchedGame = null;
        _ = Reports.Usage.RecordGameEndAsync(launched.UsageStart, exit.Unexpected,
            (int)(DateTimeOffset.UtcNow - launched.Started).TotalMinutes);
        if (exit.Unexpected)
        {
            ShowError(UsageProblem.GameExit);
            if (launched.CrashConsent && Settings.Working.SendCrashReports)
                _ = SendCrashAsync(launched.Started, launched.Edition, launched.Process);
        }
    }

    private async Task SendCrashAsync(DateTimeOffset since, GameEdition edition, int process)
    {
        try
        {
            string? key = await credentials.ReadAsync(SetupSession.KeySecret, default);
            await Reports.SendCrashesAsync(since, edition, process, key, SetupComplete && Settings.Working.SendCrashReports);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    internal Task<string?> ReadLatestLogAsync() => ReadLatestLogAsync(LogsPath);

    internal static async Task<string?> ReadLatestLogAsync(string logPath, int maxBytes = 256 * 1024)
    {
        if (!Directory.Exists(logPath))
            return null;
        using var root = new WindowsFiles.DirectoryHandles(logPath);
        var log = new DirectoryInfo(logPath).EnumerateFiles("engine-*.log")
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        if (log is null)
            return null;
        using var input = WindowsFiles.Open(logPath, log.Name);
        input.Seek(Math.Max(0, input.Length - maxBytes), SeekOrigin.Begin);
        using var text = new StreamReader(input);
        return await text.ReadToEndAsync();
    }

    internal Task SetRendererAsync(WindowsRenderMode renderer) =>
        UpdateSettingsAsync(Settings.Working with { WindowsRenderer = renderer });

    internal async Task<AdvancedSettingsReview> ReviewAdvancedAsync(string draft, GameSettings candidate)
    {
        var profile = await ConfigProfileStore.ReadActiveForLaunchAsync(gameFolder,
            candidate.Engine == GameEdition.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock);
        string data = Setup.DataPath.Length == 0 ? HomePath : Setup.DataPath;
        var display = WindowsDisplays.ForLoadout(WindowsDisplayInventory.ReadForGame(),
            candidate.WindowsMonitor, profile?.Values.GetValueOrDefault("r_monitor"));
        return AdvancedSettings.Inspect(candidate, draft, profile is not null, settings =>
        {
            try
            {
                var request = new ClientLaunchRequest(settings.Engine, settings, data, HomePath)
                    { Studio = profile?.Studio, Loadout = profile?.Values };
                _ = WindowsLaunchPlan.Client(request, new(settings.Engine, EngineRole.Client, "cod2_win32.exe"),
                    HomePath, settings.WindowsRenderer ?? WindowsRenderMode.Direct3D9, files, display: display);
                return null;
            }
            catch (LaunchPlanException error) { return error.Message; }
        });
    }

    internal async Task<string?> CheckUpdatesAsync(bool automatic, CancellationToken cancellationToken = default)
    {
        var before = Library;
        var check = await Updates.CheckAsync(before, automatic, cancellationToken);
        var next = check.Library;
        if (!ReferenceEquals(before, next))
            await UpdateLibraryAsync(library => library with
            {
                Dispatches = next.Dispatches, DispatchesCheckedAt = next.DispatchesCheckedAt,
                News = next.News, NewsNext = next.NewsNext, Releases = next.Releases
            });
        return check.NotifyVersion;
    }

    internal void ObserveUpdate(LauncherUpdateState state)
    {
        var observation = (state.Phase, state.AvailableVersion, state.FailedStage, state.Issue);
        if (observedUpdate == observation)
            return;
        observedUpdate = observation;
        var kind = state.Phase switch
        {
            UpdatePhase.Offered => UsageKind.UpdateOffered,
            UpdatePhase.Downloading => UsageKind.UpdateDownloading,
            UpdatePhase.Verifying => UsageKind.UpdateVerifying,
            UpdatePhase.Ready => UsageKind.UpdateReady,
            UpdatePhase.Installing => UsageKind.UpdateInstalling,
            _ => (UsageKind?)null
        };
        if (kind is { } usage && state.AvailableVersion is { } to)
            Record(UsageEvent.Update(usage, Version, to));
        if (state.FailedStage is { } failure)
            ShowError(failure == UpdateFailureStage.Check ? UsageProblem.UpdateCheck : UsageProblem.UpdateInstall);
    }

    private async Task FlushForUpdateAsync()
    {
        await Settings.FlushAsync();
        if (Reports.Usage.InstallNumber is { } install && Updates.State.AvailableVersion is { } to &&
            UsageEvent.Version(to) is { } tag)
        {
            try { await folder.WriteAsync(UsageUpdateReceipt, [.. Encoding.UTF8.GetBytes($"{install:D} {Version} {tag}")], default); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        await Reports.Usage.FlushAsync();
    }

    internal async Task RecordInstalledUpdateAsync()
    {
        try
        {
            if (await folder.ReadAsync(UsageUpdateReceipt, default) is not { Length: < 85 } bytes)
                return;
            var words = Encoding.UTF8.GetString(bytes.AsSpan()).Split(' ');
            if (lastUpdateOutcome == UpdateOutcome.Installed && words.Length == 3 && words[2] == Version &&
                Guid.TryParseExact(words[0], "D", out var install) && install == Reports.Usage.InstallNumber)
                await Reports.Usage.RecordAsync(UsageEvent.Update(UsageKind.UpdateInstalled, words[1], Version));
            if (lastUpdateOutcome != UpdateOutcome.Waiting)
                await ClearUsageUpdateAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private async Task ClearUsageUpdateAsync()
    {
        try { await folder.DeleteAsync(UsageUpdateReceipt, default); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal bool CanCheckUpdates => !SetupUpdating &&
        (TestMaster is null || WindowsReleaseSource.Fixture is not null || WindowsReleaseSource.LocalOrigin is not null);
    internal bool CanAutoCheckUpdates => !SetupUpdating && (TestMaster is null || WindowsReleaseSource.LocalOrigin is not null);

    internal void RefreshUpdateOutcome()
    {
        lastUpdateOutcome = UpdateInstaller.ReadOutcome();
        if (lastUpdateOutcome is UpdateOutcome.Failed or UpdateOutcome.Installed)
            Updates.RestoreInstallResult(lastUpdateOutcome == UpdateOutcome.Installed, Library.DispatchesCheckedAt);
    }

    internal async Task RefreshServersAsync(ServerScope scope, IProgress<ServerRefreshState> progress, CancellationToken cancellationToken)
    {
        Record(new(UsageKind.ServerListRefreshed, (int)scope));
        try
        {
            var library = Library;
            var next = await Task.Run(() => Browser.RefreshAsync(library, library.Cache, progress, cancellationToken, TestMaster));
            await UpdateLibraryAsync(library => library with { Cache = next.Cache, CachedAt = next.CachedAt });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            ShowError(UsageProblem.ServerList);
            throw;
        }
    }

    internal Task ToggleFavoriteAsync(string address) => UpdateLibraryAsync(library => library with
    {
        Favorites = library.Favorites.Contains(address) ? library.Favorites.Remove(address) : library.Favorites.Add(address)
    });

    private async Task UpdateLibraryAsync(Func<LauncherLibrary, LauncherLibrary> update)
    {
        await libraryWrites.WaitAsync();
        try
        {
            var library = update(Library);
            await library.SaveAsync(folder);
            Library = library;
        }
        finally { libraryWrites.Release(); }
    }

    internal async Task UpdateSettingsAsync(GameSettings choice)
    {
        var before = Settings.Saved;
        Settings.Replace(choice);
        try
        {
            await Settings.FlushAsync();
            if (!choice.SendCrashReports)
                Reports.CancelCrash();
            foreach (var item in UsageEvent.Changed(before, Settings.Saved))
                Record(item);
        }
        catch
        {
            Settings.Replace(Settings.Saved);
            throw;
        }
    }

    private async ValueTask SaveIntroAsync() => await UpdateSettingsAsync(Settings.Working with { IntroFilmsPlayed = true });

    private async Task<SetupSession> CreateSetupAsync(string? remembered, bool hasKey, SetupStep step,
        bool wizard = false, bool review = false)
    {
        var candidates = await FindGameDataAsync(remembered);
        if (wizard)
            SetupCopies = candidates;
        string initialPath = SetupSession.InitialDataPath(candidates.FirstOrDefault()?.DataPath, remembered, SetupComplete);
        return new SetupSession(files, folder, credentials, initialPath, hasKey, canCancel: review, step: step,
            hasReportService: Reports.CrashAvailable && (review || (!Settings.Working.CrashReportsAsked && !SetupComplete)),
            hasUsageService: review ? Reports.Usage.Available : Reports.Usage.ShouldAsk && !SetupComplete,
            unfinishedSteam: WindowsGameDiscovery.SteamDownloadUnfinished, deferWrites: wizard);
    }

    private sealed class ProbeCredentials(bool rejectServerWrites) : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (values)
                return ValueTask.FromResult(values.GetValueOrDefault(name));
        }

        public ValueTask WriteAsync(string name, string? value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rejectServerWrites && name != SetupSession.KeySecret)
                throw new IOException("Fixture refused to store the password.");
            lock (values)
            {
                if (value is null)
                    values.Remove(name);
                else
                    values[name] = value;
            }
            return ValueTask.CompletedTask;
        }
    }
}
