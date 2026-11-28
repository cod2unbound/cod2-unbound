using System.Runtime.InteropServices;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    internal WindowsUpdateNotifications? UpdateNotifications { private get; set; }

    private async Task NotifyUpdateAsync(string? version)
    {
        if (version is null || UpdateNotifications is not { } notifications || closing)
            return;
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        bool foregroundMatch = window != 0 && window == GetForegroundWindow();
        bool minimized = IsIconic(window);
        bool visible = IsWindowVisible(window);
        bool foreground = foregroundMatch && !minimized && visible;
        bool eligible = UpdateNotification.ShouldShow(version, foreground, session.UpdateInstaller.Observe(),
            available: true, session.Library.NotifiedUpdates);
        StartupTrace.Mark($"update notice offer version={version} foregroundMatch={foregroundMatch} minimized={minimized} visible={visible} foreground={foreground} eligible={eligible}");
        if (eligible && notifications.IsEnabled && notifications.Show(version))
        {
            await session.RememberUpdateNotificationAsync(version);
#if UPDATE_FIXTURES
            await notifications.TraceHistoryAsync(version);
#endif
        }
    }

    private async Task RemoveInstalledNotificationsAsync()
    {
        if (UpdateNotifications is not { } notifications)
            return;
        foreach (string version in session.Library.NotifiedUpdates.Append(session.Version).Distinct(StringComparer.Ordinal))
            if (!WindowsReleaseAssets.IsNewer(version, session.Version))
                await notifications.RemoveAsync(version);
    }

    /// Home's Install Update: the same path as the release notes' Install, from Settings › Updates.
    internal async Task InstallOfferedUpdateAsync()
    {
        if (session.Updates.State.Offer is null || updateRequest is not null)
            return;
        await NavigateAsync(LauncherPlace.UpdateSettings);
        await RunUpdateAsync(install: true);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
}
