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
    internal LauncherLibrary Library { get; private set; } = new();
    internal WindowsRenderMode Renderer { get; private set; }
    private const string RendererFile = "windows-renderer.txt";
    internal string HomePath => gameFolder.Root;

    internal WindowsLauncherSession()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        gameFolder = new WindowsSettingsFolder(Path.Join(localData, "CoD2 Unbound"));
        folder = new WindowsSettingsFolder(Path.Join(HomePath, "launcher"));
        string installRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, ".."));
        inventory = new WindowsEngineInventory(installRoot, files);
        discovery = new WindowsGameDiscovery(inventory);
        var processes = new WindowsProcessSupervisor(inventory, Path.Join(folder.Root, "logs"));
        var gameHost = new WindowsGameSessionHost(inventory, files, HomePath, gameFolder, () => Renderer, SaveIntroAsync);
        Game = new GameLifecycle(processes, gameHost, files, TimeProvider.System);
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

    internal Task<SetupSession> ChangeGameDataAsync() => CreateSetupAsync(Setup.DataPath, Setup.HasStoredKey, SetupStep.Data);

    internal void SetGameData(string path) => Setup.ChooseData(path);
    internal void FinishSetup() => SetupComplete = Setup.Step == SetupStep.Complete;

    internal ValueTask<ConfigProfile?> ReadLoadoutAsync() => ConfigProfileStore.ReadActiveForDisplayAsync(gameFolder);

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
