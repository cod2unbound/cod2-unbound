using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CoD2Unbound.Core;
using Microsoft.Win32;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsReportFacts
{
    private readonly string version;
    private readonly Lazy<(string Model, string Processor, string[] PrivateValues)> machine = new(ReadMachine);
    private readonly Lazy<Task<UsageHardware>> hardware;

    internal WindowsReportFacts(string version)
    {
        this.version = version;
        hardware = new(() => Task.Run(ReadHardware));
    }

    internal nint Window { get; set; }

    internal async ValueTask<UsageFacts> ReadAsync()
    {
        var specs = await hardware.Value.ConfigureAwait(false);
        var mode = Display(Window);
        return new(version, Environment.OSVersion.Version.ToString(3), machine.Value.Model, machine.Value.Processor,
            mode.Width, mode.Height, mode.Hz, Language())
        {
            Hardware = specs
        };
    }

    internal CrashReportFacts Crash()
    {
        return new(version, "Windows", Environment.OSVersion.Version.ToString(3), machine.Value.Model);
    }

    internal static string[] PrivateNames() => [Environment.UserName, Environment.MachineName];

    private static (string Model, string Processor, string[] PrivateValues) ReadMachine()
    {
        var (maker, model, serial) = FirmwareModel();
        string[] privateNames = [.. PrivateNames(), serial?.Trim() ?? ""];
        string? processor = null;
        try
        {
            processor = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", null) as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return (UsageFacts.Model(maker, model, privateNames), UsageFacts.SafeProcessor(processor, privateNames), privateNames);
    }

    private UsageHardware ReadHardware()
    {
        const ushort allProcessorGroups = 0xffff;
        uint count = GetActiveProcessorCount(allProcessorGroups);
        int cores = count == 0 ? Environment.ProcessorCount : (int)Math.Min(count, 1024);
        var (graphics, bytes, driver) = WindowsGraphicsFacts.Read();
        return new(cores, ArchitectureName(RuntimeInformation.OSArchitecture),
            ArchitectureName(RuntimeInformation.ProcessArchitecture), UsageHardware.InstalledMemoryGb(InstalledMemoryKiB()),
            UsageFacts.SafeGraphics(graphics, machine.Value.PrivateValues), (long)(bytes >> 30), driver);
    }

    private static string? ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null
    };

    private static ulong InstalledMemoryKiB()
    {
        if (GetPhysicallyInstalledSystemMemory(out ulong kibibytes))
            return kibibytes;
        // Windows can still report visible physical memory if firmware omits its installed total.
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysicalBytes / 1024 : 0;
    }

    private static (string? Maker, string? Model, string? Serial) FirmwareModel()
    {
        const uint rawSmbios = 0x52534D42;
        uint size = GetSystemFirmwareTable(rawSmbios, 0, null, 0);
        if (size is < 8 or > 1024 * 1024)
            return default;
        var data = new byte[size];
        if (GetSystemFirmwareTable(rawSmbios, 0, data, size) != size)
            return default;
        for (int start = 8; start + 4 < data.Length;)
        {
            int length = data[start + 1], end = start + length;
            if (length < 4 || end >= data.Length - 1)
                break;
            var strings = new List<string>(4);
            while (end < data.Length - 1 && (data[end] != 0 || data[end + 1] != 0))
            {
                int next = Array.IndexOf(data, (byte)0, end);
                if (next < 0 || next == data.Length - 1)
                    return default;
                if (data[start] == 1)
                    strings.Add(Encoding.UTF8.GetString(data, end, Math.Min(next - end, 65)));
                end = next + (data[next + 1] == 0 ? 0 : 1);
            }
            if (data[start] == 1 && length >= 6)
                return (Read(data[start + 4]), Read(data[start + 5]), length >= 8 ? Read(data[start + 7]) : null);
            if (data[start] == 127)
                break;
            start = end + 2;

            string? Read(int index) => index > 0 && index <= strings.Count ? strings[index - 1] : null;
        }
        return default;
    }

    private static string Language()
    {
        try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).TwoLetterISOLanguageName; }
        catch (CultureNotFoundException) { return "unknown"; }
    }

    private static (int Width, int Height, int Hz) Display(nint window)
    {
        nint handle = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (handle == 0 || !GetMonitorInfoW(handle, ref info))
            return default;
        var mode = new DisplayMode { Size = (short)Marshal.SizeOf<DisplayMode>() };
        return EnumDisplaySettingsW(info.Device, -1, ref mode)
            ? (mode.Width, mode.Height, mode.Hz > 1 ? mode.Hz : 0) : default;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size, MonitorLeft, MonitorTop, MonitorRight, MonitorBottom, WorkLeft, WorkTop, WorkRight, WorkBottom, Flags;
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

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint table, [Out] byte[]? data, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysicalBytes, AvailablePhysicalBytes, TotalPageFileBytes, AvailablePageFileBytes,
            TotalVirtualBytes, AvailableVirtualBytes, AvailableExtendedVirtualBytes;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetActiveProcessorCount(ushort group);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kibibytes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetUserDefaultUILanguage();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string device, int index, ref DisplayMode mode);
}
