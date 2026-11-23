using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal static class WindowsDisplayInventory
{
    private const int CurrentSettings = -1;
    private const int Active = 1;
    private const int Primary = 1;

    internal static ImmutableArray<WindowsDisplay> ReadForGame() => [.. Read().Where(display => display.Monitor <= WindowsDisplays.MaxMonitor)];

    private static ImmutableArray<WindowsDisplay> Read()
    {
        var names = ReadTargets();
        var monitors = new List<MonitorInfo>();
        // SDL 2.30.9 enumerates HMONITORs in this order, with the primary first.
        _ = EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
                monitors.Add(info);
            return true;
        }, 0);
        var adapters = Devices(null).Where(device => (device.Flags & Active) != 0)
            .ToDictionary(device => device.Name, StringComparer.OrdinalIgnoreCase);
        var displays = ImmutableArray.CreateBuilder<WindowsDisplay>();
        int ordinal = 0;
        foreach (var source in monitors.OrderByDescending(info => (info.Flags & Primary) != 0))
        {
            ordinal++;
            if (!TryMode(source.Device, CurrentSettings, out var current))
                continue;
            adapters.TryGetValue(source.Device, out var adapter);
            var monitor = Devices(source.Device).FirstOrDefault(device => (device.Flags & Active) != 0);
            names.TryGetValue(source.Device, out var target);
            string name = FirstText(target.Name, monitor.Description, $"Display {ordinal}");
            string id = FirstText(target.Path, monitor.Id, source.Device);
            var modes = new List<WindowsDisplayMode>();
            for (int i = 0; TryMode(source.Device, i, out var mode); i++)
                modes.Add(mode);
            modes.Add(current); // The current mode also comes from EnumDisplaySettingsEx.
            displays.Add(new(id, name, adapter.Description?.Trim() ?? "", ordinal, target.Preferred,
                current.Size, WindowsDisplays.Offered(modes)));
        }
        return displays.ToImmutable();
    }

    private static string FirstText(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();

    private static IEnumerable<DisplayDevice> Devices(string? parent)
    {
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevicesW(parent, index, ref device, parent is null ? 0u : 1u))
                yield break;
            yield return device;
        }
    }

    private static bool TryMode(string device, int index, out WindowsDisplayMode result)
    {
        var mode = new DisplayMode { Size = (short)Marshal.SizeOf<DisplayMode>() };
        bool reported = EnumDisplaySettingsExW(device, index, ref mode, 0);
        result = new(new(mode.Width, mode.Height), mode.Hz > 1 ? mode.Hz : 0);
        return reported;
    }

    private static Dictionary<string, (string? Name, string? Path, DisplayResolution? Preferred)> ReadTargets()
    {
        const uint onlyActivePaths = 2;
        const int insufficientBuffer = 122;
        Dictionary<string, (string? Name, string? Path, DisplayResolution? Preferred)> names = new(StringComparer.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(onlyActivePaths, out uint paths, out uint modes) != 0 || paths == 0)
                break;
            var entries = new DisplayPath[paths];
            int status = QueryDisplayConfig(onlyActivePaths, ref paths, entries, ref modes, new DisplayConfigMode[modes], 0);
            if (status == insufficientBuffer)
                continue;
            if (status != 0)
                break;
            foreach (var path in entries.AsSpan(0, (int)paths))
            {
                var source = new SourceName { Header = new(1, Marshal.SizeOf<SourceName>(), path.SourceAdapter, path.SourceId) };
                var target = new TargetName { Header = new(2, Marshal.SizeOf<TargetName>(), path.TargetAdapter, path.TargetId) };
                var preferred = new TargetMode { Header = new(3, Marshal.SizeOf<TargetMode>(), path.TargetAdapter, path.TargetId) };
                if (GetSourceName(ref source) != 0 || GetTargetName(ref target) != 0)
                    continue;
                DisplayResolution? native = GetTargetMode(ref preferred) == 0 && preferred.Width > 0 && preferred.Height > 0
                    ? new(preferred.Width, preferred.Height) : null;
                if (!names.TryAdd(source.Name, (target.FriendlyName, target.Path, native)))
                {
                    // Mirrored targets share a source and one set of selectable source modes.
                    var previous = names[source.Name];
                    string combined = string.Join(" / ", new[] { previous.Name, target.FriendlyName }
                        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));
                    names[source.Name] = (combined, previous.Path, previous.Preferred == native ? native : null);
                }
            }
            break;
        }
        return names;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        internal int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string RegistryKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size, Left, Top, Right, Bottom, WorkLeft, WorkTop, WorkRight, WorkBottom, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
        internal short SpecVersion, DriverVersion, Size, DriverExtra;
        internal int Fields, X, Y, Orientation, FixedOutput;
        internal short Color, Duplex, YResolution, TtOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Form;
        internal short LogPixels;
        internal int BitsPerPixel, Width, Height, Flags, Hz, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2,
            PanningWidth, PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Luid(uint Low, int High);

    // DISPLAYCONFIG_PATH_INFO and DISPLAYCONFIG_MODE_INFO from wingdi.h, with only the fields read here exposed.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct DisplayPath
    {
        [FieldOffset(0)] internal Luid SourceAdapter;
        [FieldOffset(8)] internal uint SourceId;
        [FieldOffset(20)] internal Luid TargetAdapter;
        [FieldOffset(28)] internal uint TargetId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigMode
    {
        [FieldOffset(16)] private ulong targetPixelRate; // Align the array as DISPLAYCONFIG_VIDEO_SIGNAL_INFO.
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DisplayHeader(int Type, int Size, Luid Adapter, uint Id);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        internal DisplayHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        internal DisplayHeader Header;
        internal uint Flags, Technology;
        internal ushort Maker, Product;
        internal uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Path;
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct TargetMode
    {
        [FieldOffset(0)] internal DisplayHeader Header;
        [FieldOffset(20)] internal int Width;
        [FieldOffset(24)] internal int Height;
        [FieldOffset(32)] private ulong pixelRate;
    }

    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? name, uint index, ref DisplayDevice device, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsExW(string name, int index, ref DisplayMode mode, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] entries, ref uint modes,
        [Out] DisplayConfigMode[] modeEntries, nint topology);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int GetSourceName(ref SourceName source);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int GetTargetName(ref TargetName target);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int GetTargetMode(ref TargetMode mode);
}
