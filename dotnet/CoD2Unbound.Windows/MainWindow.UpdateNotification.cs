using System.Runtime.InteropServices;
using Microsoft.UI.Input;
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

    private global::Windows.Graphics.RectInt32? homeCaptionPassthrough;

    /// Home's update pill reaches into the title bar row (as the Mac's sits in its toolbar). Its rectangle,
    /// in window pixels, takes pointer input there; other units' passthrough rectangles are kept.
    internal void SetHomeCaptionPassthrough(global::Windows.Graphics.RectInt32? rect)
    {
        if (Nullable.Equals(rect, homeCaptionPassthrough))
            return;
        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            var kept = source.GetRegionRects(NonClientRegionKind.Passthrough)
                .Where(existing => homeCaptionPassthrough is not { } previous || !existing.Equals(previous)).ToList();
            if (rect is { } next)
                kept.Add(next);
            source.SetRegionRects(NonClientRegionKind.Passthrough, [.. kept]);
            homeCaptionPassthrough = rect;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException) { }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
