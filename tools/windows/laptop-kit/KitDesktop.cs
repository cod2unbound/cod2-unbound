using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
public static class KitDesktop {
    [StructLayout(LayoutKind.Sequential)] public struct Rect {
        public int L,T,R,B;
        public override string ToString() { return L+","+T+","+R+","+B; }
    }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct Key { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public int x,y; public uint data,flags,time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct Payload {
        [FieldOffset(0)] public Key key;
        [FieldOffset(0)] public Mouse mouse;
    }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public Payload value; }
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr w,int mode);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint source, uint target, bool attach);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int mode);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr state);
    delegate bool EnumWindow(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size; public uint Ticks; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput input);

    public static bool RequestMinimize(IntPtr window)
    {
        return PostMessage(window, 0x0112, new IntPtr(0xF020), IntPtr.Zero); // WM_SYSCOMMAND / SC_MINIMIZE
    }

    public static void MinimizeAndActivate(IntPtr window)
    {
        ShowWindow(window, 2); // SW_SHOWMINIMIZED
    }

    public static bool QuietDesktop(uint milliseconds)
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
        return GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Ticks) >= milliseconds;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string windowClass, string title);
    [StructLayout(LayoutKind.Sequential)] struct CopyData { public IntPtr Tag; public uint Size; public IntPtr Data; }
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr sender,
        ref CopyData input, uint flags, uint milliseconds, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window,
        uint message, IntPtr unused, string text, uint flags, uint milliseconds, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr window, int id);

    public static uint WindowPid(IntPtr window)
    {
        uint pid;
        GetWindowThreadProcessId(window, out pid);
        return pid;
    }

    public static IntPtr PrimaryTaskbar()
    {
        return FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
    }

    public static IntPtr[] VisibleProcessWindows(uint pid)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, state) => {
            if (IsWindowVisible(window) && WindowPid(window) == pid) windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    public static bool IsOwnedWindow(IntPtr window, IntPtr owner)
    {
        for (IntPtr parent = GetWindow(window, 4); parent != IntPtr.Zero; parent = GetWindow(parent, 4))
            if (parent == owner) return true;
        return false;
    }

    public static void CloseOwnedWindow(IntPtr window)
    {
        if (window != IntPtr.Zero) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }

    public static void SendGameConsole(IntPtr game, string command)
    {
        uint pid = WindowPid(game);
        if (pid == 0 || GetForegroundWindow() != game || WindowClass(game) != "SDL_app")
            throw new InvalidOperationException("The kit game must have focus to request the return picture.");
        IntPtr console = IntPtr.Zero;
        while ((console = FindWindowEx(IntPtr.Zero, console, "CoD2 WinConsole", null)) != IntPtr.Zero) {
            if (WindowPid(console) != pid) continue;
            IntPtr input = GetDlgItem(console, 101);
            IntPtr result;
            if (input == IntPtr.Zero ||
                SendMessageTimeout(input, 0x000C, IntPtr.Zero, command, 2, 3000, out result) == IntPtr.Zero ||
                result == IntPtr.Zero || !PostMessage(input, 0x0102, new IntPtr(13), new IntPtr(1)))
                throw new InvalidOperationException("The game did not accept the return picture command.");
            return;
        }
        throw new InvalidOperationException("The kit game's native console was not found.");
    }

    public static bool SendJoinProbe(int gamePid, string payload, int tag)
    {
        IntPtr window = FindWindowEx(new IntPtr(-3), IntPtr.Zero, "CoD2Unbound.LauncherJoin.1", gamePid.ToString());
        uint owner;
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out owner) == 0 || owner != gamePid)
            throw new InvalidOperationException("The running kit game's join receiver was not found.");
        byte[] bytes = Encoding.ASCII.GetBytes(payload + "\0");
        IntPtr data = Marshal.AllocHGlobal(bytes.Length);
        try {
            Marshal.Copy(bytes, 0, data, bytes.Length);
            var input = new CopyData { Tag = new IntPtr(tag), Size = (uint)bytes.Length, Data = data };
            IntPtr result;
            if (SendMessageTimeout(window, 0x4A, IntPtr.Zero, ref input, 2, 3000, out result) == IntPtr.Zero)
                throw new InvalidOperationException("The running game did not answer the join probe.");
            return result != IntPtr.Zero;
        } finally { Marshal.FreeHGlobal(data); }
    }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    static Rectangle Frame(IntPtr window)
    {
        Rect rect;
        if (DwmGetWindowAttribute(window, 9, out rect, Marshal.SizeOf(typeof(Rect))) != 0
            && !GetWindowRect(window, out rect))
            throw new InvalidOperationException("Windows did not report the visible frame.");
        return Rectangle.FromLTRB(rect.L, rect.T, rect.R, rect.B);
    }

    public static void SizeSurfaceWindow(IntPtr window, int width, int height)
    {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            Rectangle work = Screen.FromHandle(window).WorkingArea;
            Rectangle visible = Frame(window);
            Rect outer;
            if (!GetWindowRect(window, out outer))
                throw new InvalidOperationException("Windows did not report the test window bounds.");
            double scale = GetDpiForWindow(window) / 96.0;
            int w = Math.Min(work.Width, (int)Math.Round(width * scale));
            int h = Math.Min(work.Height, (int)Math.Round(height * scale));
            int left = work.Left + (work.Width - w) / 2 + outer.L - visible.Left;
            int top = work.Top + (work.Height - h) / 2 + outer.T - visible.Top;
            const uint keepZOrderAndFocus = 0x0004 | 0x0010;
            if (!SetWindowPos(window, IntPtr.Zero, left, top,
                w + outer.R - outer.L - visible.Width, h + outer.B - outer.T - visible.Height, keepZOrderAndFocus))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        } finally {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    public static string WindowFacts(IntPtr window)
    {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            uint style = unchecked((uint)GetWindowLong(window, -16));
            return string.Format("style=0x{0:X8}, frame={1}, monitor={2}, DPI={3}",
                style, Frame(window), Screen.FromHandle(window).Bounds, GetDpiForWindow(window));
        } finally {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    public static bool FillsMonitorWithoutFrame(IntPtr window)
    {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            const int captionOrResize = 0x00C40000;
            return (GetWindowLong(window, -16) & captionOrResize) == 0 &&
                Frame(window) == Screen.FromHandle(window).Bounds;
        } finally {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    static void ExcludeOverlappingWindows(Graphics canvas, IntPtr window, Rectangle bounds)
    {
        uint owner;
        GetWindowThreadProcessId(window, out owner);
        for (IntPtr other = GetWindow(window, 3); other != IntPtr.Zero; other = GetWindow(other, 3)) {
            int cloaked;
            if (!IsWindowVisible(other) || IsIconic(other) ||
                (DwmGetWindowAttribute(other, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0)) continue;
            uint process;
            GetWindowThreadProcessId(other, out process);
            if (process == owner) continue;
            Rectangle overlap = Rectangle.Intersect(bounds, Frame(other));
            if (overlap.Width <= 0 || overlap.Height <= 0) continue;
            overlap.Offset(-bounds.X, -bounds.Y);
            canvas.FillRectangle(Brushes.Black, overlap);
        }
    }

    public static void SaveWindow(IntPtr window, string path)
    {
        if (GetForegroundWindow() != window || IsIconic(window))
            throw new InvalidOperationException("Bring the kit window to the front before taking the picture.");
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            Rectangle monitor = Screen.FromHandle(window).Bounds;
            Rectangle bounds = Rectangle.Intersect(monitor, Frame(window));
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new InvalidOperationException("Move the kit window onto the screen.");
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
            using (var canvas = Graphics.FromImage(bitmap)) {
                canvas.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                if (GetForegroundWindow() != window || bounds != Rectangle.Intersect(monitor, Frame(window)))
                    throw new InvalidOperationException("The foreground or window position changed during capture.");
                ExcludeOverlappingWindows(canvas, window, bounds);
                // Windows 11 rounds window corners. Mask those pixels before
                // saving so the desktop behind the frame cannot appear.
                if (bounds != monitor) {
                    int corner = Math.Max(12, (int)GetDpiForWindow(window) / 8);
                    foreach (int x in new[] { 0, bounds.Width - corner })
                        foreach (int y in new[] { 0, bounds.Height - corner })
                            canvas.FillRectangle(Brushes.Black, x, y, corner, corner);
                    const int edge = 2;
                    canvas.FillRectangle(Brushes.Black, 0, 0, bounds.Width, edge);
                    canvas.FillRectangle(Brushes.Black, 0, bounds.Height - edge, bounds.Width, edge);
                    canvas.FillRectangle(Brushes.Black, 0, 0, edge, bounds.Height);
                    canvas.FillRectangle(Brushes.Black, bounds.Width - edge, 0, edge, bounds.Height);
                }
                bitmap.Save(path, ImageFormat.Png);
            }
        } finally {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    public static string SecurityPrompt() {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return "";
        uint processId;
        GetWindowThreadProcessId(window, out processId);
        try {
            using (var process = Process.GetProcessById((int)processId)) {
                switch (process.ProcessName.ToLowerInvariant()) {
                    case "pickerhost":
                        return WindowClass(window) == "Shell_SystemDialog" ? "windows-security" : "";
                    case "consent": return "uac";
                    case "smartscreen": return "smartscreen";
                    default: return "";
                }
            }
        } catch (ArgumentException) { return ""; }
    }

    public static bool TryFocus(IntPtr window)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == window) return true;
        if (foreground == IntPtr.Zero) return false;
        uint processId;
        uint foregroundThread = GetWindowThreadProcessId(foreground, out processId);
        try {
            using (var process = Process.GetProcessById((int)processId)) {
                string name = process.ProcessName.ToLowerInvariant();
                if (name == "searchhost" || name == "startmenuexperiencehost") {
                    // Dismiss only the foreground Start or Search overlay.
                    if (GetForegroundWindow() == foreground) Tap(27);
                    return false;
                }
                if (WindowClass(foreground) == "#32770" || name == "pickerhost" || name == "consent" || name == "smartscreen" || name == "securityhealthhost" ||
                    name == "securityhealthsystray" || name == "windowsdefender" || name == "applicationframehost")
                    return false;
            }
        } catch (ArgumentException) { return false; }
        uint caller = GetCurrentThreadId();
        bool attached = caller != foregroundThread && AttachThreadInput(caller, foregroundThread, true);
        try {
            ShowWindowAsync(window, 9);
            SetForegroundWindow(window);
        } finally {
            if (attached) AttachThreadInput(caller, foregroundThread, false);
        }
        return GetForegroundWindow() == window;
    }

    public static bool ClickSurface(IntPtr window, int x, int y, bool requireForeground)
    {
        return ClickSurface(window, x, y, requireForeground, null);
    }

    public static IntPtr RootWindowAt(int x, int y)
    {
        return GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2);
    }

    public static bool ClickSurface(IntPtr window, int x, int y, bool requireForeground, Action whilePressed)
    {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        System.Drawing.Point cursor = Cursor.Position;
        try {
            var point = new Point { X = x, Y = y };
            if (GetAncestor(WindowFromPoint(point), 2) != window) return false;
            Cursor.Position = new System.Drawing.Point(x - 16, y);
            Move(1, 0);
            Thread.Sleep(120);
            Cursor.Position = new System.Drawing.Point(x, y);
            Move(1, 0);
            Thread.Sleep(180);
            GetCursorPos(out point);
            if (GetAncestor(WindowFromPoint(point), 2) != window || SecurityPrompt() != "" ||
                (requireForeground && GetForegroundWindow() != window)) return false;
            ClickButton(false);
            try {
                Thread.Sleep(85);
                if (whilePressed != null) whilePressed();
            } finally { ClickButton(true); }
            return true;
        } finally {
            Cursor.Position = cursor;
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    public static bool TryFocusPictureWindow(IntPtr window, uint owner)
    {
        if (owner == 0 || WindowPid(window) != owner) return false;
        if (TryFocus(window)) return true;
        IntPtr foreground = GetForegroundWindow();
        if (!QuietDesktop(1200) || SecurityPrompt() != "" || WindowClass(foreground) == "#32770")
            return false;
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            // Place the owned window where a title-bar click can activate it normally.
            const uint keepSizePositionAndFocus = 0x0001 | 0x0002 | 0x0010;
            if (!SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, keepSizePositionAndFocus)) return false;
            Rectangle frame = Frame(window);
            int captionY = frame.Top + (int)(12 * GetDpiForWindow(window) / 96.0);
            if (!ClickSurface(window, frame.Left + frame.Width / 2, captionY, false)) return false;
            return GetForegroundWindow() == window;
        } finally {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    public static IntPtr[] NotificationBannerCandidates()
    {
        // The shell's ToastView is hosted in a top-level CoreWindow.
        var windows = new List<IntPtr>();
        EnumWindows((window, state) => {
            if (!IsWindowVisible(window) || WindowClass(window) != "Windows.UI.Core.CoreWindow") return true;
            int cloaked;
            if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            int owner = (int)WindowPid(window);
            if (owner == 0) return true;
            try {
                using (var process = Process.GetProcessById(owner)) {
                    string name = process.ProcessName;
                    if (name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("ShellHost", StringComparison.OrdinalIgnoreCase)) windows.Add(window);
                }
            } catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    [DllImport("user32.dll")] public static extern bool GetClipCursor(out Rect r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool PostMessage(IntPtr w,uint msg,IntPtr a,IntPtr b);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder b,int n);
    [StructLayout(LayoutKind.Explicit,Size=220)] struct Mode {
        [FieldOffset(68)] public short Size;
        [FieldOffset(168)] public int Bits;
        [FieldOffset(172)] public int Width;
        [FieldOffset(176)] public int Height;
        [FieldOffset(184)] public int Frequency;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device,int number,ref Mode mode);
    public static string[] Display() { return Display(null); }
    public static string[] Display(string device) {
        var modes = new List<string>();
        for(int i=-1;i<int.MaxValue;i++) {
            Mode mode=new Mode { Size=220 };
            if (!EnumDisplaySettings(device,i,ref mode)) break;
            modes.Add(i+" "+mode.Width+"x"+mode.Height+" "+mode.Bits+"bpp "+mode.Frequency+"Hz");
        }
        return modes.ToArray();
    }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    public static string DisplayDpi(int left, int top) {
        IntPtr monitor = MonitorFromPoint(new Point { X=left, Y=top }, 2);
        uint x, y;
        return GetDpiForMonitor(monitor, 0, out x, out y) == 0 ? x + "x" + y : "unavailable";
    }

    public static string WindowClass(IntPtr window) {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }
    public static string CurrentMode() {
        var mode = new Mode { Size = 220 };
        if (!EnumDisplaySettings(null, -1, ref mode))
            throw new InvalidOperationException("Windows did not report the current display mode.");
        return mode.Width + "x" + mode.Height;
    }
    static void Send(Input evt) {
        if (SendInput(1,new[]{evt},Marshal.SizeOf(typeof(Input)))!=1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public static void KeyEvent(ushort code,bool up) {
        Send(new Input { type=1,value=new Payload { key=new Key { vk=code,flags=up ? 2u : 0u } } });
    }
    public static void Tap(ushort code) { KeyEvent(code,false); Thread.Sleep(85); KeyEvent(code,true); }
    public static void Chord(ushort mod,ushort key) {
        KeyEvent(mod,false); try { Tap(key); } finally { KeyEvent(mod,true); }
    }
    public static void Chord(ushort first, ushort second, ushort key) {
        KeyEvent(first, false);
        try { Chord(second, key); } finally { KeyEvent(first, true); }
    }
    public static void Move(int x,int y) {
        Send(new Input { value=new Payload { mouse=new Mouse { x=x,y=y,flags=1 } } });
    }
    public static void ClickButton(bool up) {
        Send(new Input { value=new Payload { mouse=new Mouse { flags=up?4u:2u } } });
    }
    public static void ClickGameContinue(IntPtr window) {
        if (GetForegroundWindow() != window) throw new InvalidOperationException("The game needs focus.");
        Rect client;
        if (!GetClientRect(window, out client)) throw new InvalidOperationException("No game client area.");
        var point = new Point { X=(int)(client.R * 0.75), Y=(int)(client.B * 0.892) };
        if (!ClientToScreen(window, ref point) || !SetCursorPos(point.X, point.Y))
            throw new InvalidOperationException("Could not position the cursor in the game.");
        ClickButton(false);
        try { Thread.Sleep(85); } finally { ClickButton(true); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct ShellRequest {
        public int Size; public uint Mask; public IntPtr Window;
        public string Verb, File, Parameters, Directory; public int Show;
        public IntPtr Instance, List; public string FileClass; public IntPtr ClassKey; public uint Hotkey;
        public IntPtr Monitor, Process;
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool ShellExecuteEx(ref ShellRequest request);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static void OpenAssociation(string file, string fileClass) {
        var request = new ShellRequest { Size=Marshal.SizeOf(typeof(ShellRequest)),
            Mask=0x541, FileClass=fileClass, Verb="open", File=file, Show=1 };
        if (!ShellExecuteEx(ref request))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (request.Process != IntPtr.Zero) CloseHandle(request.Process);
    }
    public static string Clip() {
        Rect r; Point p;
        GetClipCursor(out r); GetCursorPos(out p);
        return r+" cursor="+p.X+","+p.Y;
    }
}
