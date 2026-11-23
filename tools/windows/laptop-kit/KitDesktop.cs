using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
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
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr w,int mode);
    [DllImport("user32.dll")] public static extern bool GetClipCursor(out Rect r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
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
    public static void Move(int x,int y) {
        Send(new Input { value=new Payload { mouse=new Mouse { x=x,y=y,flags=1 } } });
    }
    public static void ClickButton(bool up) {
        Send(new Input { value=new Payload { mouse=new Mouse { flags=up?4u:2u } } });
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
