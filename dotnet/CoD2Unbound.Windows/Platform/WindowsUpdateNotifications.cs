using System.Runtime.InteropServices;
using CoD2Unbound.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Foundation;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsUpdateNotifications : IDisposable
{
    // Keep this ID in sync with the installer's Start menu shortcut.
    private const string AppId =
#if UPDATE_FIXTURES
        "CoD2Unbound.Launcher.Fixture";
#else
        "CoD2Unbound.Launcher";
#endif
    internal const string ActivationPrefix = "cod2unbound:release-notes:";
    private AppNotificationManager? manager;
    private TypedEventHandler<AppNotificationManager, AppNotificationActivatedEventArgs>? handler;

    internal WindowsUpdateNotifications(Action<string[]> enqueue)
    {
        try
        {
            if (SetCurrentProcessExplicitAppUserModelID(AppId) < 0 || !AppNotificationManager.IsSupported())
                return;
            var notifications = AppNotificationManager.Default;
            handler = (_, args) =>
            {
                var arguments = ReadArguments(args);
                if (arguments.Length != 0)
                    enqueue(arguments);
            };
            notifications.NotificationInvoked += handler;
            try
            {
                notifications.Register();
                manager = notifications;
            }
            catch (Exception error) when (Unavailable(error))
            {
                notifications.NotificationInvoked -= handler;
            }
        }
        catch (Exception error) when (Unavailable(error)) { }
    }

    internal static string[] ReadArguments(AppNotificationActivatedEventArgs args) =>
        args.Arguments.TryGetValue("action", out string? action) && action == "release-notes" &&
        args.Arguments.TryGetValue("version", out string? version) && UpdateNotification.IsVersion(version)
            ? [ActivationPrefix + version] : [];

    internal bool IsEnabled
    {
        get
        {
            try { return manager?.Setting == AppNotificationSetting.Enabled; }
            catch (Exception error) when (Unavailable(error)) { return false; }
        }
    }

    internal bool Show(string version)
    {
        if (manager is not { } notifications)
            return false;
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "release-notes")
                .AddArgument("version", version)
                .AddText($"CoD2 Unbound {version} is ready")
                .AddText("Select to see what’s new.")
                .BuildNotification();
            notification.Tag = UpdateNotification.Tag(version);
            notifications.Show(notification);
            return notification.Id != 0;
        }
        catch (Exception error) when (Unavailable(error)) { return false; }
    }

    internal async Task RemoveAsync(string version)
    {
        if (manager is null)
            return;
        try { await manager.RemoveByTagAsync(UpdateNotification.Tag(version)); }
        catch (Exception error) when (Unavailable(error)) { }
    }

    public void Dispose()
    {
        if (manager is null)
            return;
        try
        {
            if (handler is not null)
                manager.NotificationInvoked -= handler;
            manager.Unregister();
        }
        catch (Exception error) when (Unavailable(error)) { }
    }

    private static bool Unavailable(Exception error) => error is COMException or UnauthorizedAccessException or
        InvalidOperationException or NotSupportedException or TypeLoadException or DllNotFoundException or
        EntryPointNotFoundException or TypeInitializationException or ArgumentException;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
