using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed class SetupViewModel(WindowsLauncherSession session)
{
    internal SetupSession Setup => session.Setup;
    internal void Finish() => session.FinishSetup();
}
