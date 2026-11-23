using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal static class WindowsRunningGameJoin
{
    internal static unsafe GameLinkDelivery Deliver(GameProcessIdentity target, LaunchLink link)
    {
        try
        {
            using var client = Process.GetProcessById(target.Id);
            if (WindowsEngineProcess.CreationTime(client.SafeHandle) != target.CreatedAt || client.HasExited)
                return GameLinkDelivery.Failed;

            nint receiver = FindWindowEx(new nint(-3), 0, "CoD2Unbound.LauncherJoin.1",
                target.Id.ToString(CultureInfo.InvariantCulture));
            if (receiver == 0)
                return GameLinkDelivery.Pending;
            if (GetWindowThreadProcessId(receiver, out uint owner) == 0 || owner != target.Id)
                return GameLinkDelivery.Failed;

            // The engine validates the payload again. The bytes are only sent to the verified process.
            Span<byte> payload = stackalloc byte[2048];
            if (link.Url.Length >= payload.Length)
                return GameLinkDelivery.Failed;
            try
            {
                int length = Encoding.ASCII.GetBytes(link.Url.AsSpan(8), payload);
                payload[length] = 0;
                fixed (byte* bytes = payload)
                {
                    var copy = new CopyData { Tag = 0x434F4432, Length = (uint)(length + 1), Bytes = (nint)bytes };
                    const uint blockAbortIfHungAndErrorOnExit = 0x23;
                    if (SendMessageTimeout(receiver, 0x004A, 0, ref copy, blockAbortIfHungAndErrorOnExit, 1500, out nuint accepted) == 0 ||
                        accepted != 1)
                        return GameLinkDelivery.Failed;
                }
            }
            finally { payload.Clear(); }

            Activate(client);
            return GameLinkDelivery.Delivered;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return GameLinkDelivery.Failed;
        }
    }

    internal static void Activate(GameProcessIdentity target)
    {
        try
        {
            using var client = Process.GetProcessById(target.Id);
            if (WindowsEngineProcess.CreationTime(client.SafeHandle) == target.CreatedAt)
                Activate(client);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    private static void Activate(Process client)
    {
        nint window = client.MainWindowHandle;
        if (window == 0)
            return;
        if (IsIconic(window))
            _ = ShowWindowAsync(window, 9); // SW_RESTORE
        _ = SetForegroundWindow(window);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public nuint Tag;
        public uint Length;
        public nint Bytes;
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string windowName);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", ExactSpelling = true)]
    private static extern nint SendMessageTimeout(nint window, uint message, nint sender, ref CopyData data, uint flags,
        uint milliseconds, out nuint result);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
