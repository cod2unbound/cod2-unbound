using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed class SettingsViewModel(WindowsLauncherSession session)
{
    private SetupSession? pendingData;

    internal LauncherSettingsSession Settings => session.Settings;
    internal SetupDataChoice Data => pendingData?.Data ?? session.Setup.Data;
    internal bool HasPendingData => pendingData is not null;

    internal void RefreshData() => session.Setup.ChooseData(session.Setup.DataPath);
    internal void CancelDataChange() => pendingData = null;

    internal async Task ChooseAsync(string path)
    {
        pendingData ??= await session.ChangeGameDataAsync();
        pendingData.ChooseData(path);
    }

    internal async Task SaveDataAsync()
    {
        if (pendingData is { } change && await change.ContinueAsync())
        {
            session.SetGameData(change.DataPath);
            pendingData = null;
        }
    }

    internal Task SaveSettingsAsync(GameSettings choice) => session.UpdateSettingsAsync(choice);
}
