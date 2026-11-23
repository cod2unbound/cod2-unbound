using System.Runtime.InteropServices;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsSessionEnd
{
    private const uint QueryEndSession = 0x0011, EndSession = 0x0016, NcDestroy = 0x0082;
    private readonly Action onEnd;
    private readonly SubclassProc procedure;
    private bool ended;

    internal WindowsSessionEnd(nint window, Action onEnd)
    {
        this.onEnd = onEnd;
        procedure = Dispatch;
        if (!SetWindowSubclass(window, procedure, 1, 0))
            StartupTrace.Mark("session-end messages unavailable");
    }

    private nint Dispatch(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == QueryEndSession)
            return 1;
        if (message == EndSession)
        {
            if (wParam != 0 && !ended)
            {
                ended = true;
                onEnd();
            }
            return 0;
        }
        if (message == NcDestroy)
            _ = RemoveWindowSubclass(hwnd, procedure, id);
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc procedure, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc procedure, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
