using System.Runtime.InteropServices;
using System.Text;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal static class WindowsTaskbarJumpList
{
    private static readonly Guid DestinationListClass = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid CollectionClass = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid LinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly PropertyKey TitleKey = new(new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
    private const ushort WideString = 31; // VT_LPWSTR

    internal static void Publish(IEnumerable<GameServer> servers)
    {
        if (Environment.ProcessPath is not { } executable)
            return;
        ICustomDestinationList? list = null;
        IObjectArray? removed = null;
        IObjectCollection? recent = null, tasks = null;
        bool updating = false;
        try
        {
            list = Create<ICustomDestinationList>(DestinationListClass);
            list.SetAppID(WindowsUpdateNotifications.AppId);
            list.BeginList(out _, typeof(IObjectArray).GUID, out removed);
            updating = true;
            var entries = TaskbarJumpList.Recent(servers, RemovedArguments(removed));
            if (!entries.IsEmpty)
            {
                recent = MakeLinks(entries, executable);
                // History can be disabled by Windows privacy settings. Still publish the tasks.
                const int accessDenied = unchecked((int)0x80070005);
                const int missingAssociation = unchecked((int)0x80040F03);
                int result = list.AppendCategory("Recent servers", (IObjectArray)recent);
                if (result is accessDenied or missingAssociation)
                    StartupTrace.Mark($"taskbar recent servers unavailable (0x{result:X8})");
                else
                    Marshal.ThrowExceptionForHR(result);
            }
            tasks = MakeLinks(TaskbarJumpList.Tasks, executable);
            list.AddUserTasks((IObjectArray)tasks);
            list.CommitList();
            updating = false;
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            StartupTrace.Mark($"taskbar jump list unavailable (0x{error.HResult:X8})");
        }
        finally
        {
            if (updating)
                _ = list!.AbortList();
            Release(tasks);
            Release(recent);
            Release(removed);
            Release(list);
        }
    }

    private static List<string> RemovedArguments(IObjectArray items)
    {
        List<string> removed = [];
        items.GetCount(out uint count);
        var arguments = new StringBuilder(1024);
        for (uint index = 0; index < count; index++)
        {
            object? item = null;
            try
            {
                if (items.GetAt(index, typeof(IShellLink).GUID, out item) >= 0 && item is IShellLink link)
                {
                    arguments.Clear();
                    link.GetArguments(arguments, arguments.Capacity);
                    removed.Add(arguments.ToString());
                }
            }
            finally { Release(item); }
        }
        return removed;
    }

    private static IObjectCollection MakeLinks(IEnumerable<TaskbarEntry> entries, string executable)
    {
        var collection = Create<IObjectCollection>(CollectionClass);
        try
        {
            foreach (var entry in entries)
            {
                var link = Create<IShellLink>(LinkClass);
                try
                {
                    link.SetPath(executable);
                    link.SetArguments(entry.Arguments);
                    link.SetIconLocation(executable, 0);
                    var properties = (IPropertyStore)link;
                    var title = new PropertyValue { Type = WideString, String = Marshal.StringToCoTaskMemUni(entry.Title) };
                    try { properties.SetValue(TitleKey, title); }
                    finally { Marshal.FreeCoTaskMem(title.String); }
                    properties.Commit();
                    collection.AddObject(link);
                }
                finally { Release(link); }
            }
            return collection;
        }
        catch
        {
            Release(collection);
            throw;
        }
    }

    private static T Create<T>(Guid classId) =>
        (T)Activator.CreateInstance(Type.GetTypeFromCLSID(classId, throwOnError: true)!)!;

    private static void Release(object? value)
    {
        if (value is not null)
            _ = Marshal.ReleaseComObject(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    // PROPVARIANT on x64 and ARM64, with the VT_LPWSTR member of its union.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint String;
    }

    [ComImport, Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string id);
        void BeginList(out uint slots, in Guid iid, out IObjectArray removed);
        [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string name, IObjectArray items);
        void AppendKnownCategory();
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations();
        void DeleteList();
        [PreserveSig] int AbortList();
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? item);
    }

    [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        void GetCount();
        void GetAt();
        void AddObject([MarshalAs(UnmanagedType.IUnknown)] object item);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount();
        void GetAt();
        void GetValue();
        void SetValue(in PropertyKey key, in PropertyValue value);
        void Commit();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        // Unused entries preserve the native vtable order.
        void GetPath();
        void GetIDList();
        void SetIDList();
        void GetDescription();
        void SetDescription();
        void GetWorkingDirectory();
        void SetWorkingDirectory();
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey();
        void SetHotkey();
        void GetShowCmd();
        void SetShowCmd();
        void GetIconLocation();
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath();
        void Resolve();
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
