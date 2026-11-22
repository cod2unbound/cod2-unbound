using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace CoD2Unbound.Windows;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        StartupTrace.Mark("managed entry");
        if (Platform.WindowsInstallRecovery.RedirectInterruptedStart())
            return 0;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        var current = AppInstance.GetCurrent();
        var initial = current.GetActivatedEventArgs();
        var pending = new ConcurrentQueue<string[]>();
        Action? schedule = null;

        // Subscribe before making the key discoverable, including during XAML startup.
        current.Activated += Redirected;
        try
        {
            var owner = AppInstance.FindOrRegisterForKey("CoD2Unbound.Launcher");
            if (!owner.IsCurrent)
                return Redirect(owner, initial);

            StartupTrace.Mark("AppLifecycle registration");
            Application.Start(initialization =>
            {
                StartupTrace.Mark("Application.Start callback");
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                _ = new App(window =>
                {
                    window.ReceiveActivation(ReadArguments(initial), bringForward: false);
                    Volatile.Write(ref schedule, () => dispatcher.TryEnqueue(Drain));
                    Drain();

                    void Drain()
                    {
                        while (pending.TryDequeue(out var arguments))
                            window.ReceiveActivation(arguments, bringForward: true);
                    }
                });
            });
            return 0;
        }
        finally { current.Activated -= Redirected; }

        void Redirected(object? sender, AppActivationArguments activation)
        {
            pending.Enqueue(ReadArguments(activation));
            Volatile.Read(ref schedule)?.Invoke();
        }
    }

    private static int Redirect(AppInstance owner, AppActivationArguments activation)
    {
        _ = AllowSetForegroundWindow(owner.ProcessId);
        Task redirect = Task.Run(async () => await owner.RedirectActivationToAsync(activation));
        // The documented WinUI pattern redirects off the STA while this thread services COM calls.
        var completed = ((IAsyncResult)redirect).AsyncWaitHandle;
        if (CoWaitForMultipleObjects(0, 15_000, 1, [completed.SafeWaitHandle.DangerousGetHandle()], out _) != 0)
            return 1;
        try { redirect.GetAwaiter().GetResult(); }
        catch (COMException) { return 1; }
        return 0;
    }

    private static string[] ReadArguments(AppActivationArguments activation) => activation.Data switch
    {
        ILaunchActivatedEventArgs launch => ReadCommandLine(launch.Arguments),
        IFileActivatedEventArgs files => [.. files.Files.Select(file => file.Path)],
        IProtocolActivatedEventArgs protocol => [protocol.Uri.OriginalString],
        _ => []
    };

    private static string[] ReadCommandLine(string commandLine)
    {
        // For an unpackaged Launch, the SDK forwards GetCommandLineW, including argv[0].
        nint argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == 0)
            return [];
        try
        {
            if (count < 2)
                return [];
            // Setup's command contains exactly one quoted %1.
            return count == 2 ? [Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, IntPtr.Size)) ?? ""] : [""];
        }
        finally { _ = LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, nint[] handles, out uint index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
