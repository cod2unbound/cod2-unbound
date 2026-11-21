using System.Collections.Immutable;
using System.Text;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

/// <summary>Composes the platform adapters with the shared setup and settings sessions.</summary>
internal sealed class WindowsLauncherSession
{
    private readonly WindowsFiles files = new();
    private readonly ISettingsFolder folder;
    private readonly WindowsCredentials credentials = new();
    private readonly WindowsGameDiscovery discovery;
    private readonly WindowsSettingsFolder gameFolder;
    private readonly WindowsEngineInventory inventory;
    internal GameLifecycle Game { get; }
    internal ServerQueryClient ServerQuery { get; }
    internal UdpEndpoint[]? TestMaster { get; }
    internal ServerBrowserPreferences ServerPreferences { get; private set; } = ServerBrowserPreferences.Default;
    internal ServerBrowser Browser { get; }
    internal LauncherLibrary Library { get; private set; } = new();
    internal WindowsRenderMode Renderer { get; private set; }
    private const string RendererFile = "windows-renderer.txt";
    internal string HomePath => gameFolder.Root;

    internal WindowsLauncherSession()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        gameFolder = new WindowsSettingsFolder(Path.Join(localData, "CoD2 Unbound"), allowNestedWrites: true);
        folder = new WindowsSettingsFolder(Path.Join(HomePath, "launcher"));
        string installRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, ".."));
        inventory = new WindowsEngineInventory(installRoot, files);
        discovery = new WindowsGameDiscovery(inventory);
        var processes = new WindowsProcessSupervisor(inventory, Path.Join(folder.Root, "logs"));
        var gameHost = new WindowsGameSessionHost(inventory, files, HomePath, gameFolder, () => Renderer, SaveIntroAsync);
        Game = new GameLifecycle(processes, gameHost, files, TimeProvider.System);
        if (Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_MASTER_PORT") is { } test)
            TestMaster = int.TryParse(test, out int port) && port is > 0 and <= 65535 ? [new("127.0.0.1", port)] : [];
        ServerQuery = new ServerQueryClient(new WindowsUdpTransport(TestMaster is not null));
        Browser = new ServerBrowser(ServerQuery, TimeProvider.System);
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
        Renderer = await folder.ReadAsync(RendererFile, default) is { } renderer &&
            renderer.AsSpan().SequenceEqual("dx7\n"u8) ? WindowsRenderMode.DirectX7 : WindowsRenderMode.Direct3D9;
        var storage = new GameSettingsStorage(folder);
        Settings = new LauncherSettingsSession(await storage.ReadAsync(), storage, TimeProvider.System);
        SetupComplete = await folder.ReadAsync(SetupSession.CompletionFile, default) is not null;
        string? remembered = await folder.ReadAsync(SetupSession.DataPathFile, default) is { } path
            ? Encoding.UTF8.GetString(path.AsSpan()).Trim()
            : null;
        bool hasKey = SetupSession.KeyTail(await credentials.ReadAsync(SetupSession.KeySecret, default)) is not null;
        Setup = await CreateSetupAsync(remembered, hasKey, SetupStep.Welcome);
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
        catch (DirectoryNotFoundException) { return []; }
    }

    internal async Task SelectLoadoutAsync(string id)
    {
        await using var launch = await WindowsGameSessionHost.OpenLeaseAsync(HomePath, ".launcher-start.lock", default);
        await using var profile = await WindowsGameSessionHost.OpenLeaseAsync(HomePath, "main/players/.studio.lock", default);
        await Game.ReconcileAsync();
        if (Game.State.Running)
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

    internal bool HasClient(GameEdition edition) =>
        inventory.Installed.Any(binary => binary is { Role: EngineRole.Client } && binary.Edition == edition);

    internal Task<GameStartState> LaunchAsync(LaunchLink? link = null, GameEdition? edition = null)
    {
        var settings = Settings.Working;
        var request = new ClientLaunchRequest(edition ?? settings.Engine, settings, Setup.DataPath, HomePath) { Link = link };
        return Game.StartAsync(request, SetupComplete);
    }

    internal async Task SetRendererAsync(WindowsRenderMode renderer)
    {
        await folder.WriteAsync(RendererFile, renderer == WindowsRenderMode.DirectX7 ? [.. "dx7\n"u8] : [.. "dx9\n"u8], default);
        Renderer = renderer;
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
}
