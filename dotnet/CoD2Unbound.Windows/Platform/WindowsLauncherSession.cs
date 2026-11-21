using System.Text;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

/// <summary>Composes the platform adapters with the shared setup and settings sessions.</summary>
internal sealed class WindowsLauncherSession
{
    private readonly WindowsFiles files = new();
    private readonly ISettingsFolder folder;
    private readonly WindowsCredentials credentials = new();
    private readonly WindowsGameDiscovery discovery = new();

    internal WindowsLauncherSession()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        folder = new WindowsSettingsFolder(Path.Join(localData, "CoD2 Unbound"));
    }

    internal LauncherSettingsSession Settings { get; private set; } = null!;
    internal SetupSession Setup { get; private set; } = null!;
    internal bool SetupComplete { get; private set; }

    internal async Task InitializeAsync()
    {
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

    internal ValueTask<ConfigProfile?> ReadLoadoutAsync() => ConfigProfileStore.ReadActiveForDisplayAsync(folder);

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
