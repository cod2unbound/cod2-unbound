using System.Globalization;
using System.Runtime.InteropServices;

namespace CoD2Unbound.Windows.Platform;

internal static unsafe class WindowsGraphicsFacts
{
    private static readonly Guid Factory6Id = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid Adapter1Id = new("29038f61-3839-4626-91fd-086879011a05");
    private static readonly Guid DeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private const uint HighPerformance = 2;
    private const uint SoftwareAdapter = 2;
    private const int AdapterNotFound = unchecked((int)0x887a0002);
    // Slots include the inherited IUnknown and DXGI methods in dxgi.h and dxgi1_6.h.
    private const int EnumAdapterByGpuPreference = 29;
    private const int CheckInterfaceSupport = 9;
    private const int GetDesc1 = 10;

    internal static (string? Name, ulong DedicatedBytes, string? Driver) Read()
    {
        if (CreateDXGIFactory1(in Factory6Id, out nint factory) < 0)
            return default;
        try
        {
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, uint, Guid*, nint*, int>)
                Method(factory, EnumAdapterByGpuPreference);
            Guid iid = Adapter1Id;
            (string? Name, ulong DedicatedBytes, string? Driver)? software = null;
            for (uint index = 0; ; index++)
            {
                nint adapter;
                int result = enumerate(factory, index, HighPerformance, &iid, &adapter);
                if (result < 0)
                    return result == AdapterNotFound ? software.GetValueOrDefault() : default;
                try
                {
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)Method(adapter, GetDesc1);
                    AdapterDescription description;
                    if (getDescription(adapter, &description) < 0)
                        continue;
                    bool isSoftware = (description.Flags & SoftwareAdapter) != 0;
                    if (isSoftware && software.HasValue)
                        continue;
                    var text = new ReadOnlySpan<char>(description.Name, 128);
                    int terminator = text.IndexOf('\0');
                    var facts = (terminator < 0 ? null : text[..terminator].ToString(), (ulong)description.DedicatedVideoBytes,
                        ReadDriver(adapter));
                    if (!isSoftware)
                        return facts;
                    software = facts;
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
    }

    private static string? ReadDriver(nint adapter)
    {
        var check = (delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Method(adapter, CheckInterfaceSupport);
        Guid iid = DeviceId;
        ulong version;
        if (check(adapter, &iid, &version) < 0 || version == 0)
            return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"{(ushort)(version >> 48)}.{(ushort)(version >> 32)}.{(ushort)(version >> 16)}.{(ushort)version}");
    }

    private static nint Method(nint instance, int index) => (*(nint**)instance)[index];

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        internal fixed char Name[128];
        internal uint Vendor, Device, Subsystem, Revision;
        internal nuint DedicatedVideoBytes, DedicatedSystemBytes, SharedSystemBytes;
        internal uint LuidLow;
        internal int LuidHigh;
        internal uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
}
