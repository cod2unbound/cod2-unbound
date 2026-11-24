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
        bool foreground = WinRT.Interop.WindowNative.GetWindowHandle(this) == GetForegroundWindow();
        if (UpdateNotification.ShouldShow(version, foreground, session.UpdateInstaller.Observe(), notifications.IsEnabled,
                session.Library.NotifiedUpdates) && notifications.Show(version))
            await session.RememberUpdateNotificationAsync(version);
    }

    private async Task RemoveInstalledNotificationsAsync()
    {
        if (UpdateNotifications is not { } notifications)
            return;
        foreach (string version in session.Library.NotifiedUpdates.Append(session.Version).Distinct(StringComparer.Ordinal))
            if (!WindowsReleaseAssets.IsNewer(version, session.Version))
                await notifications.RemoveAsync(version);
    }

    private async Task OpenNotificationReleaseAsync(string version)
    {
        if (WindowsReleaseAssets.IsNewer(version, session.Version) && session.Updates.State.Offer?.Version != version &&
            updateRequest is null && session.CanCheckUpdates)
            await RunUpdateAsync(install: false);
        await ShowReleaseNotesAsync(version);
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
}
