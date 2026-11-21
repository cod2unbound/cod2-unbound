using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed class HomeViewModel(WindowsLauncherSession session)
{
    internal GameEdition Edition => session.Settings.Working.Engine;
    internal SetupDataChoice RefreshData() => session.Setup.ChooseData(session.Setup.DataPath);
    internal ValueTask<ConfigProfile?> ReadLoadoutAsync() => session.ReadLoadoutAsync();
}
