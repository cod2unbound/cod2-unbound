using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Dispatching;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private LauncherLibrary? taskbarLibrary;
    private bool taskbarQueued;

    private void RefreshTaskbarJumpList()
    {
        if (!initialized || taskbarQueued || ReferenceEquals(taskbarLibrary, session.Library))
            return;
        taskbarQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            taskbarQueued = false;
            if (closing || flushedForClose || windowsSessionEnding)
                return;
            taskbarLibrary = session.Library;
            StartupTrace.Mark($"taskbar refresh settingsInFront={settingsWindow?.IsInFront == true}");
            WindowsTaskbarJumpList.Publish(taskbarLibrary.Recent.Select(session.ServerForAddress));
        });
    }
}
