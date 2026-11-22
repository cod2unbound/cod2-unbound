using System.Collections.Immutable;
using System.Text;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

/// <summary>Composes the platform adapters with the shared setup and settings sessions.</summary>
internal sealed class WindowsLauncherSession
{
    private readonly WindowsFiles files = new();
    private readonly ISettingsFolder folder;
    private readonly ISecretStore credentials;
    private readonly WindowsGameDiscovery discovery;
    private readonly WindowsSettingsFolder gameFolder;
    private readonly WindowsEngineInventory inventory;
    internal GameLifecycle Game { get; }
    internal ServerQueryClient ServerQuery { get; }
    internal UdpEndpoint[]? TestMaster { get; }
    internal ServerBrowserPreferences ServerPreferences { get; private set; } = ServerBrowserPreferences.Default;
    internal ServerBrowser Browser { get; }
    internal LauncherLibrary Library { get; private set; } = new();
    internal WindowsRenderMode Renderer => Settings.Working.WindowsRenderer ?? WindowsRenderMode.Direct3D9;
    internal LauncherUpdates Updates { get; }
    internal WindowsLauncherUpdate UpdateInstaller { get; }
    private UpdateOutcome lastUpdateOutcome;
    internal bool SetupUpdating => lastUpdateOutcome == UpdateOutcome.Waiting;
    internal WindowsHosting Hosting { get; }
    private const string RendererFile = "windows-renderer.txt";
    internal string HomePath => gameFolder.Root;
    internal event Action? ClientStarted;

    internal WindowsLauncherSession()
    {
        if (Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_MASTER_PORT") is { } test)
            TestMaster = int.TryParse(test, out int port) && port is > 0 and <= 65535 ? [new("127.0.0.1", port)] : [];
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? testHome = TestMaster is not null ? Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_HOME") : null;
        credentials = testHome is null ? new WindowsCredentials() : new ProbeCredentials();
        gameFolder = new WindowsSettingsFolder(testHome ?? Path.Join(localData, "CoD2 Unbound"), allowNestedWrites: true);
        folder = new WindowsSettingsFolder(Path.Join(HomePath, "launcher"));
        string installRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, ".."));
        inventory = new WindowsEngineInventory(installRoot, files);
        discovery = new WindowsGameDiscovery(inventory);
        var processes = new WindowsProcessSupervisor(inventory, Path.Join(folder.Root, "logs"));
        var gameHost = new WindowsGameSessionHost(inventory, files, HomePath, gameFolder, () => Renderer, SaveIntroAsync);
        Game = new GameLifecycle(processes, gameHost, files, TimeProvider.System);
        ServerQuery = new ServerQueryClient(new WindowsUdpTransport(TestMaster is not null));
        Browser = new ServerBrowser(ServerQuery, TimeProvider.System);
        Hosting = new(HomePath, inventory, files, TestMaster is not null);
        Updates = new LauncherUpdates(new WindowsReleaseSource(TestMaster is not null), TimeProvider.System,
            Version, (version, assets) => WindowsReleaseAssets.Offer(version, assets, WindowsReleaseSource.LocalOrigin),
            TimeZoneInfo.Local, WindowsReleaseAssets.IsNewer);
        UpdateInstaller = new(installRoot, HomePath, Version, inventory, () => new(Game.State.Running, Hosting.HasActiveServers),
            () => Settings.FlushAsync());
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
        string? remembered = await folder.ReadAsync(SetupSession.DataPathFile, default) is { } path
            ? Encoding.UTF8.GetString(path.AsSpan()).Trim()
            : null;
        bool hasKey = SetupSession.KeyTail(await credentials.ReadAsync(SetupSession.KeySecret, default)) is not null;
        Setup = await CreateSetupAsync(remembered, hasKey, SetupStep.Welcome);
        RefreshUpdateOutcome();
    }

    internal string Version => typeof(WindowsLauncherSession).Assembly.GetName().Version!.ToString(3);

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

    internal async Task<GameStartState> JoinAsync(ServerAddress address, GameEdition? edition = null)
    {
        var result = await LaunchAsync(LaunchLink.Direct(address.Address, address.Password ?? ""), edition);
        if (result == GameStartState.Started)
            await SaveLibraryAsync(Library.Joined(address.Address, TimeProvider.System.GetUtcNow()));
        return result;
    }

    internal Task<SetupSession> ChangeGameDataAsync() => CreateSetupAsync(Setup.DataPath, Setup.HasStoredKey, SetupStep.Data);

    internal void SetGameData(string path) => Setup.ChooseData(path);
    internal async ValueTask<string?> KeyEndingAsync() =>
        SetupSession.KeyTail(await credentials.ReadAsync(SetupSession.KeySecret, default));

    internal async Task<bool> ReplaceKeyAsync(string key)
    {
        var change = new SetupSession(files, folder, credentials, Setup.DataPath, Setup.HasStoredKey, step: SetupStep.Key);
        change.ReplaceKey();
        if (!await change.ContinueAsync(key))
            return false;
        Setup = new SetupSession(files, folder, credentials, Setup.DataPath, change.HasStoredKey, step: Setup.Step);
        return true;
    }

    internal Task DeleteMatchHistoryAsync() => gameFolder.DeleteAsync("main/stats/matches.jsonl", default).AsTask();
    internal string LogsPath => Path.Join(folder.Root, "logs");
    internal void FinishSetup() => SetupComplete = Setup.Step == SetupStep.Complete;

    internal ValueTask<ConfigProfile?> ReadLoadoutAsync() => ConfigProfileStore.ReadActiveForDisplayAsync(gameFolder);

    internal async Task<ImmutableArray<ConfigProfile>> ReadLoadoutChoicesAsync()
    {
        string players = Path.Join(HomePath, "main", "players");
        try
        {
            using var root = new WindowsFiles.DirectoryHandles(players);
            var ids = new DirectoryInfo(players).EnumerateDirectories()
                .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0).Select(entry => entry.Name);
            return await ConfigProfileStore.ReadChoicesAsync(gameFolder, ids);
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
        var dialect = Settings.Working.Engine == GameEdition.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock;
        string? name = await ConfigProfileStore.ReadPlayerNameAsync(gameFolder, dialect);
        return (string.IsNullOrWhiteSpace(name) ? "Player" : name, loadout?.DisplayName,
            Setup.HasStoredKey ? "CD key verified" : "CD key needed");
    }

    internal bool ClientIsRunning => Game.State.Running || !inventory.FindRunning(EngineRole.Client, default).IsEmpty;

    internal bool HasClient(GameEdition edition) =>
        inventory.Installed.Any(binary => binary is { Role: EngineRole.Client } && binary.Edition == edition);

    internal async Task<GameStartState> LaunchAsync(LaunchLink? link = null, GameEdition? edition = null, string? demo = null)
    {
        if (SetupUpdating)
            throw new IOException("Wait for the launcher update to finish before opening the game.");
        var settings = Settings.Working;
        var request = new ClientLaunchRequest(edition ?? settings.Engine, settings, Setup.DataPath, HomePath) { Link = link, Demo = demo };
        var start = await Game.StartAsync(request, SetupComplete);
        if (start == GameStartState.Started)
            ClientStarted?.Invoke();
        return start;
    }

    internal Task<string?> ReadLatestLogAsync() => ReadLatestLogAsync(LogsPath);

    internal static async Task<string?> ReadLatestLogAsync(string logPath)
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
        input.Seek(Math.Max(0, input.Length - 256 * 1024), SeekOrigin.Begin);
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
        return AdvancedSettings.Inspect(candidate, draft, profile is not null, settings =>
        {
            try
            {
                var request = new ClientLaunchRequest(settings.Engine, settings, data, HomePath)
                    { Studio = profile?.Studio, Loadout = profile?.Values };
                _ = WindowsLaunchPlan.Client(request, new(settings.Engine, EngineRole.Client, "cod2_win32.exe"),
                    HomePath, settings.WindowsRenderer ?? WindowsRenderMode.Direct3D9, files);
                return null;
            }
            catch (LaunchPlanException error) { return error.Message; }
        });
    }

    internal async Task CheckUpdatesAsync(bool automatic, CancellationToken cancellationToken = default)
    {
        var result = await Updates.CheckAsync(Library, automatic, cancellationToken);
        var next = result.Library;
        if (next.DispatchesCheckedAt != Library.DispatchesCheckedAt)
            await SaveLibraryAsync(Library with { Dispatches = next.Dispatches, DispatchesCheckedAt = next.DispatchesCheckedAt });
    }

    internal bool CanCheckUpdates => !SetupUpdating &&
        (TestMaster is null || WindowsReleaseSource.Fixture is not null || WindowsReleaseSource.LocalOrigin is not null);
    internal bool CanAutoCheckUpdates => !SetupUpdating && (TestMaster is null || WindowsReleaseSource.LocalOrigin is not null);

    internal void RefreshUpdateOutcome()
    {
        lastUpdateOutcome = UpdateInstaller.ReadOutcome();
        if (lastUpdateOutcome is UpdateOutcome.Failed or UpdateOutcome.Installed)
            Updates.RestoreInstallResult(lastUpdateOutcome == UpdateOutcome.Installed);
    }

    internal async Task SaveFiltersAsync(ServerFilters filters)
    {
        var preferences = ServerPreferences with { Filters = filters };
        await preferences.SaveAsync(folder);
        ServerPreferences = preferences;
    }

    internal async Task RefreshServersAsync(IProgress<ServerRefreshState> progress, CancellationToken cancellationToken)
    {
        var next = await Browser.RefreshAsync(Library, Library.Cache, progress, cancellationToken, TestMaster);
        await SaveLibraryAsync(next);
    }

    internal Task ToggleFavoriteAsync(string address) => SaveLibraryAsync(Library with
    {
        Favorites = Library.Favorites.Contains(address) ? Library.Favorites.Remove(address) : Library.Favorites.Add(address)
    });

    internal async Task SaveLibraryAsync(LauncherLibrary library)
    {
        await library.SaveAsync(folder);
        Library = library;
    }

    internal async Task UpdateSettingsAsync(GameSettings choice)
    {
        Settings.Replace(choice);
        try { await Settings.FlushAsync(); }
        catch
        {
            Settings.Replace(Settings.Saved);
            throw;
        }
    }

    private async ValueTask SaveIntroAsync() => await UpdateSettingsAsync(Settings.Working with { IntroFilmsPlayed = true });

    private async Task<SetupSession> CreateSetupAsync(string? remembered, bool hasKey, SetupStep step)
    {
        var candidates = await Task.Run(async () =>
        {
            var installations = await discovery.DiscoverAsync(default).ConfigureAwait(false);
            return GameArchives.RecoverSelection(files, remembered, installations.Select(install => install.DataPath));
        });
        string initialPath = SetupSession.InitialDataPath(candidates, remembered, SetupComplete);
        return new SetupSession(files, folder, credentials, initialPath, hasKey, step: step);
    }

    private sealed class ProbeCredentials : ISecretStore
    {
        private string? key;
        public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken) => ValueTask.FromResult(key);
        public ValueTask WriteAsync(string name, string? value, CancellationToken cancellationToken)
        {
            key = value;
            return ValueTask.CompletedTask;
        }
    }
}
