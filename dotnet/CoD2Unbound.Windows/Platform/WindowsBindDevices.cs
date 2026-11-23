using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsKeyboard
{
    internal static nint ActiveLayout => GetKeyboardLayout(0);

    internal WindowsKeyboard() => Layout = ActiveLayout;
    internal nint Layout { get; }

    internal string Name
    {
        get
        {
            try
            {
                var language = CultureInfo.GetCultureInfo((int)(Layout & 0xffff));
                return language.Name switch
                {
                    "en-US" => "US", "en-GB" => "UK", _ => language.DisplayName
                };
            }
            catch (CultureNotFoundException) { return "Keyboard"; }
        }
    }

    internal KeyboardShape Shape => MapVirtualKeyExW(0x56, 3, Layout) == 0xe2
        ? KeyboardShape.Iso : MapVirtualKeyExW(0x7d, 3, Layout) != 0 ? KeyboardShape.Jis : KeyboardShape.Ansi;

    internal EngineKey? Map(int virtualKey, int scan, bool extended) =>
        WindowsBindInput.Key(virtualKey, scan, extended, (char)(MapVirtualKeyExW((uint)virtualKey, 2, ActiveLayout) & 0xffff));

    internal (EngineKey? Key, string Label) Describe(int position, string? caption)
    {
        bool extended = position >= 0xe000;
        int scan = position & 0xff;
        int vk = (position, caption) switch
        {
            (0xe037, _) => 0x2c,
            (0x45, "Pause") => 0x13,
            (0x45, "Num") => 0x90,
            (0x153, _) => 0x6e,
            (>= 0x147 and <= 0x152, _) when caption is { Length: 1 } && char.IsAsciiDigit(caption[0]) => 0x60 + caption[0] - '0',
            _ => (int)MapVirtualKeyExW((uint)position, 3, Layout)
        };
        var key = WindowsBindInput.Key(vk, scan, extended, (char)(MapVirtualKeyExW((uint)vk, 2, Layout) & 0xffff));
        if (caption is not null)
            return (key, caption);

        var label = new StringBuilder(8);
        // Flag 4 leaves Windows' live dead-key state intact (available since Windows 10 1607).
        int count = ToUnicodeEx((uint)vk, (uint)scan, new byte[256], label, label.Capacity, 4, Layout);
        return (key, count != 0 && label.Length != 0 && !char.IsControl(label[0])
            ? label.ToString().ToUpper(CultureInfo.CurrentCulture)
            : key is { } gameKey ? WindowsBindInput.Label(gameKey) : "—");
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetKeyboardLayout(uint thread);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyExW(uint code, uint mode, nint layout);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder text, int capacity, uint flags, nint layout);
}

internal sealed record WindowsMouse(string Name, int? Buttons)
{
    internal static IReadOnlyList<WindowsMouse> Read()
    {
        uint count = 0;
        uint stride = (uint)Marshal.SizeOf<RawDevice>();
        if (GetRawInputDeviceList(null, ref count, stride) == uint.MaxValue || count == 0)
            return [];
        var devices = new RawDevice[count];
        uint read = GetRawInputDeviceList(devices, ref count, stride);
        if (read == uint.MaxValue)
            return [];
        var mice = new List<WindowsMouse>();
        foreach (var device in devices.Take((int)read).Where(device => device.Type == 0))
        {
            uint bytes = 32; // RID_DEVICE_INFO including its largest union member.
            var data = new uint[8];
            data[0] = bytes;
            if (GetRawInputDeviceInfoW(device.Handle, 0x2000000b, data, ref bytes) == uint.MaxValue)
                continue;
            uint characters = 0;
            _ = GetRawInputDeviceInfoW(device.Handle, 0x20000007, (StringBuilder?)null, ref characters);
            var path = new StringBuilder((int)characters);
            if (characters > 0)
                _ = GetRawInputDeviceInfoW(device.Handle, 0x20000007, path, ref characters);
            mice.Add(new(ProductName(path.ToString()) ?? "Windows mouse", (int)data[3]));
        }
        return mice.OrderByDescending(mouse => mouse.Buttons).ToArray();
    }

    private static string? ProductName(string path)
    {
        if (path.Length == 0)
            return null;
        using var handle = CreateFileW(path, 0, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
            return null;
        var text = new StringBuilder(128);
        return HidD_GetProductString(handle, text, (uint)(text.Capacity * sizeof(char))) && text.Length > 0 ? text.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice { internal nint Handle; internal uint Type; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetRawInputDeviceList([Out] RawDevice[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetRawInputDeviceInfoW(nint device, uint command, [In, Out] uint[] data, ref uint size);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(nint device, uint command, StringBuilder? data, ref uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access,
        uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("hid.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder text, uint bytes);
}
