using System.Collections;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using CoD2Unbound.Core;
using Microsoft.Win32.SafeHandles;

namespace CoD2Unbound.Windows.Platform;

internal static class NativeEngineStart
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartUseStdHandles = 0x00000100;
    private const string HideServerConsole = "COD2_UNBOUND_HIDE_SERVER_CONSOLE";
    private static readonly nint HandleListAttribute = 0x00020002;

    internal static SafeProcessHandle Start(EngineProcessStart start, AnonymousPipeServerStream output, bool hostedServer,
        out int id)
    {
        // A closed input pipe gives the child EOF. Only these handles are inherited.
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        nint stdin = input.ClientSafePipeHandle.DangerousGetHandle();
        nint stdout = output.ClientSafePipeHandle.DangerousGetHandle();
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        nint attributes = Marshal.AllocHGlobal(checked((nint)size));
        nint handles = 0, env = 0;
        bool initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw LastError("Windows could not prepare the engine's process handles.");
            initialized = true;
            handles = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, stdin);
            Marshal.WriteIntPtr(handles, IntPtr.Size, stdout);
            if (!UpdateProcThreadAttribute(attributes, 0, HandleListAttribute, handles, (nuint)(2 * IntPtr.Size), 0, 0))
                throw LastError("Windows could not prepare the engine's standard streams.");
            env = Marshal.StringToHGlobalUni(EnvironmentBlock(start, hostedServer));
            var info = new StartupInfoEx
            {
                Startup = new()
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = StartUseStdHandles,
                    Input = stdin, Output = stdout, Error = stdout
                },
                AttributeList = attributes
            };
            var command = new StringBuilder(WindowsCommandLine.Format(start));
            if (!CreateProcess(start.Executable, command, 0, 0, true,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow, env,
                    start.WorkingDirectory, ref info, out var native))
                throw LastError("Windows could not start Call of Duty 2.");
            using var thread = new SafeWaitHandle(native.Thread, ownsHandle: true);
            id = native.Id;
            return new SafeProcessHandle(native.Process, ownsHandle: true);
        }
        finally
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(env);
        }
    }

    internal static IOException LastError(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    internal static void CloseServerConsole(int id)
    {
        nint window = 0;
        // Process.CloseMainWindow only finds visible windows.
        while ((window = FindWindowEx(0, window, "CoD2 WinConsole", null)) != 0)
        {
            if (GetWindowThreadProcessId(window, out uint owner) != 0 && owner == id)
            {
                _ = PostMessage(window, 0x0010, 0, 0); // WM_CLOSE
                return;
            }
        }
    }

    private static string EnvironmentBlock(EngineProcessStart start, bool hostedServer)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment.Add((string)entry.Key, (string)entry.Value!);
        foreach (var (name, value) in start.EnvironmentChanges)
        {
            if (value is null)
                environment.Remove(name);
            else
                environment[name] = value;
        }
        if (hostedServer)
            environment[HideServerConsole] = "1";
        else
            environment.Remove(HideServerConsole);
        return string.Join('\0', environment.Select(pair => pair.Key + "=" + pair.Value)) + "\0";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, ConsoleWidth, ConsoleHeight, Fill, Flags;
        internal ushort ShowWindow, ReservedBytes;
        internal nint ReservedData, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo Startup;
        internal nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process, Thread;
        internal int Id, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, nint processSecurity, nint threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nuint size,
        nint previous, nint returnedSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
