using System.Runtime.InteropServices;
using CoD2Unbound.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Foundation;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsUpdateNotifications : IDisposable
{
    internal const string AppId = WindowsInstallIdentity.AppId;
    private const string UpdateAction = "updates";
    internal const string ActivationPrefix = "cod2unbound:updates:";
    private AppNotificationManager? manager;
    private TypedEventHandler<AppNotificationManager, AppNotificationActivatedEventArgs>? handler;

    internal WindowsUpdateNotifications(Action<string[]> enqueue)
    {
        try
        {
            int identity = SetCurrentProcessExplicitAppUserModelID(AppId);
            StartupTrace.Mark($"update notice identity hr=0x{identity:X8}");
            string operation = "IsSupported";
            try
            {
                bool supported = AppNotificationManager.IsSupported();
                StartupTrace.Mark($"update notice supported={supported}");
                if (identity < 0 || !supported)
                    return;
                operation = "Default";
                var notifications = AppNotificationManager.Default;
                handler = (_, args) =>
                {
                    var arguments = ReadArguments(args);
                    if (arguments.Length != 0)
                        enqueue(arguments);
                };
                operation = "NotificationInvoked subscription";
                notifications.NotificationInvoked += handler;
                try
                {
                    notifications.Register(WindowsInstallIdentity.Product,
                        new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "mark-48.png")));
                    manager = notifications;
                    StartupTrace.Mark("update notice register=success");
                }
                catch (Exception error) when (Unavailable(error))
                {
                    RecordFailure("Register", error);
                    operation = "NotificationInvoked removal";
                    notifications.NotificationInvoked -= handler;
                }
            }
            catch (Exception error) when (Unavailable(error))
            {
                RecordFailure(operation, error);
            }
        }
        catch (Exception error) when (Unavailable(error)) { RecordFailure("process identity", error); }
    }

    internal static string[] ReadArguments(AppNotificationActivatedEventArgs args) =>
        args.Arguments.TryGetValue("action", out string? action) && action is UpdateAction or "release-notes" &&
        args.Arguments.TryGetValue("version", out string? version) && UpdateNotification.IsVersion(version)
            ? [ActivationPrefix + version] : [];

    internal bool IsEnabled
    {
        get
        {
            try
            {
                var setting = manager?.Setting;
                StartupTrace.Mark($"update notice setting={setting?.ToString() ?? "no manager"}");
                return setting == AppNotificationSetting.Enabled;
            }
            catch (Exception error) when (Unavailable(error))
            {
                RecordFailure("Setting", error);
                return false;
            }
        }
    }

    internal bool Show(string version)
    {
        if (manager is not { } notifications)
            return false;
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", UpdateAction)
                .AddArgument("version", version)
                .AddText($"CoD2 Unbound {version} is ready")
                .AddText("Select to open Updates.")
                .BuildNotification();
            notification.Tag = UpdateNotification.Tag(version);
            notifications.Show(notification);
            StartupTrace.Mark($"update notice show version={version} id={notification.Id}");
            return notification.Id != 0;
        }
        catch (Exception error) when (Unavailable(error))
        {
            RecordFailure("Show", error);
            return false;
        }
    }

#if UPDATE_FIXTURES
    internal async Task TraceHistoryAsync(string version)
    {
        if (manager is not { } notifications || !StartupTrace.Enabled)
            return;
        try
        {
            var history = await notifications.GetAllAsync();
            StartupTrace.Mark($"update notice history version={version} present={history.Any(item => item.Tag == UpdateNotification.Tag(version))}");
        }
        catch (Exception error) when (Unavailable(error)) { RecordFailure("GetAllAsync", error); }
    }
#endif

    internal async Task RemoveAsync(string version)
    {
        if (manager is null)
            return;
        try { await manager.RemoveByTagAsync(UpdateNotification.Tag(version)); }
        catch (Exception error) when (Unavailable(error)) { RecordFailure("RemoveByTagAsync", error); }
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
        catch (Exception error) when (Unavailable(error)) { RecordFailure("Unregister", error); }
    }

    private static bool Unavailable(Exception error) => error is COMException or UnauthorizedAccessException or
        InvalidOperationException or NotSupportedException or TypeLoadException or DllNotFoundException or
        EntryPointNotFoundException or TypeInitializationException or ArgumentException;

    [System.Diagnostics.Conditional("UPDATE_FIXTURES")]
    private static void RecordFailure(string operation, Exception error) =>
        StartupTrace.Mark($"update notice {operation} error={error.GetType().Name} hr=0x{error.HResult:X8}");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
