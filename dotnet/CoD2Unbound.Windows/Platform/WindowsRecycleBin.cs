using System.Runtime.InteropServices;

namespace CoD2Unbound.Windows.Platform;

internal static class WindowsRecycleBin
{
    private static readonly Guid FileOperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private const uint Silent = 0x0004, NoConfirmation = 0x0010, AllowUndo = 0x0040, NoErrorUi = 0x0400;
    private const uint WantNukeWarning = 0x4000, EarlyFailure = 0x00100000, RecycleOnDelete = 0x00080000;

    private const uint NoConnectedElements = 0x2000;

    internal static string Recycle(string path, nint owner, bool singleFile = false)
    {
        const string message = "Windows couldn’t move this item to the Recycle Bin.";
        nint item = 0, progress = 0;
        var sink = new RecycleProgress();
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ShellItemInterface, out item));
            progress = Marshal.GetComInterfaceForObject(sink, typeof(IRecycleProgressSink));
            uint flags = Silent | NoErrorUi | EarlyFailure | NoConfirmation | AllowUndo | WantNukeWarning | RecycleOnDelete;
            if (singleFile)
            {
                // Fail before asking the Shell to remove a file on a drive without a Recycle Bin.
                string root = Path.GetPathRoot(path)!;
                var info = new RecycleBinQuery { Size = Marshal.SizeOf<RecycleBinQuery>() };
                if (new DriveInfo(root).DriveType != DriveType.Fixed || SHQueryRecycleBin(root, ref info) < 0)
                    throw new IOException(message);
                flags |= NoConnectedElements;
            }
            Perform(owner, flags, operation => operation.DeleteItem(item, progress), message);
            if (sink.ItemId is null || Path.Exists(path))
                throw new IOException(message);
            return sink.ItemId;
        }
        catch (COMException error) { throw new IOException(message, error); }
        finally
        {
            if (progress != 0)
                Marshal.Release(progress);
            if (item != 0)
                Marshal.Release(item);
        }
    }

    internal static void Restore(string token, string destination, nint owner) =>
        RestoreItem(token, destination, owner, singleFile: false);

    internal static void RestoreFile(string token, string destination, nint owner) =>
        RestoreItem(token, destination, owner, singleFile: true);

    private static void RestoreItem(string token, string destination, nint owner, bool singleFile)
    {
        string message = singleFile ? "Windows couldn’t restore this file from the Recycle Bin."
            : "Windows couldn’t restore this loadout from the Recycle Bin.";
        if (Path.Exists(destination))
            throw new IOException(singleFile ? "A file with that name already exists." : "A loadout with this identifier already exists.");
        byte[] pidl = Convert.FromBase64String(token);
        nint buffer = Marshal.AllocCoTaskMem(pidl.Length);
        nint item = 0, folder = 0, progress = 0;
        try
        {
            Marshal.Copy(pidl, 0, buffer, pidl.Length);
            Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(buffer, ShellItemInterface, out item));
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetDirectoryName(destination)!, 0, ShellItemInterface, out folder));
            uint flags = Silent | NoErrorUi | EarlyFailure;
            string name = Path.GetFileName(destination);
            var sink = singleFile ? new RecycleProgress(destination) : null;
            if (sink is not null)
            {
                flags |= NoConnectedElements;
                progress = Marshal.GetComInterfaceForObject(sink, typeof(IRecycleProgressSink));
            }
            Perform(owner, flags, operation => operation.MoveItem(item, folder, name, progress), message);
            if (sink is not null)
            {
                if (!sink.Restored)
                    throw new IOException(message);
                using var verified = WindowsFiles.Open(Path.GetDirectoryName(destination)!, Path.GetFileName(destination));
            }
            else if (!Directory.Exists(destination))
                throw new IOException(message);
        }
        catch (COMException error) { throw new IOException(message, error); }
        finally
        {
            if (item != 0)
                Marshal.Release(item);
            if (folder != 0)
                Marshal.Release(folder);
            if (progress != 0)
                Marshal.Release(progress);
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static void Perform(nint owner, uint flags, Action<IFileOperation> queue, string failure)
    {
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClass, throwOnError: true)!)!;
        try
        {
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(flags);
            queue(operation);
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted)
                throw new IOException(failure);
        }
        finally { Marshal.ReleaseComObject(operation); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleProgress(string? destination = null) : IRecycleProgressSink
    {
        private const int Cancel = unchecked((int)0x80004004);
        internal string? ItemId { get; private set; }
        internal bool Restored { get; private set; }

        public int PreDeleteItem(uint flags, nint item)
        {
            const uint recycleIfPossible = 0x80;
            return (flags & recycleIfPossible) == 0 ? Cancel : 0;
        }

        public void PostDeleteItem(uint flags, nint item, int result, nint recycled)
        {
            if (result < 0 || recycled == 0 || SHGetIDListFromObject(recycled, out nint pidl) < 0)
                return;
            try
            {
                var bytes = new byte[ILGetSize(pidl)];
                Marshal.Copy(pidl, bytes, 0, bytes.Length);
                ItemId = Convert.ToBase64String(bytes);
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }

        public void StartOperations() { }
        public void FinishOperations(int result) { }
        public void PreRenameItem(uint flags, nint item, nint name) { }
        public void PostRenameItem(uint flags, nint item, nint name, int result, nint created) { }
        public int PreMoveItem(uint flags, nint item, nint folder, nint name)
        {
            const uint renameOrOverwrite = 0x03;
            return destination is not null && ((flags & renameOrOverwrite) != 0 || Path.Exists(destination)) ? Cancel : 0;
        }

        public void PostMoveItem(uint flags, nint item, nint folder, nint name, int result, nint created) =>
            Restored = destination is not null && result >= 0 && created != 0 &&
                string.Equals(Marshal.PtrToStringUni(name), Path.GetFileName(destination), StringComparison.OrdinalIgnoreCase);
        public void PreCopyItem(uint flags, nint item, nint folder, nint name) { }
        public void PostCopyItem(uint flags, nint item, nint folder, nint name, int result, nint created) { }
        public void PreNewItem(uint flags, nint folder, nint name) { }
        public void PostNewItem(uint flags, nint folder, nint name, nint template, uint attributes, int result, nint created) { }
        public void UpdateProgress(uint total, uint completed) { }
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleBinQuery
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW", ExactSpelling = true)]
    private static extern int SHQueryRecycleBin(string root, ref RecycleBinQuery info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint binding, in Guid iid, out nint item);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHCreateItemFromIDList(nint pidl, in Guid iid, out nint item);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetIDListFromObject(nint item, out nint pidl);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int ILGetSize(nint pidl);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    private interface IFileOperation
    {
        void Advise();
        void Unadvise();
        void SetOperationFlags(uint flags);
        void SetProgressMessage();
        void SetProgressDialog();
        void SetProperties();
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem();
        void ApplyPropertiesToItems();
        void RenameItem();
        void RenameItems();
        void MoveItem(nint item, nint destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint progressSink);
        void MoveItems();
        void CopyItem();
        void CopyItems();
        void DeleteItem(nint item, nint progressSink);
        void DeleteItems();
        void NewItem();
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}

[ComVisible(true), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
public interface IRecycleProgressSink
{
    void StartOperations();
    void FinishOperations(int result);
    void PreRenameItem(uint flags, nint item, nint name);
    void PostRenameItem(uint flags, nint item, nint name, int result, nint created);
    [PreserveSig] int PreMoveItem(uint flags, nint item, nint folder, nint name);
    void PostMoveItem(uint flags, nint item, nint folder, nint name, int result, nint created);
    void PreCopyItem(uint flags, nint item, nint folder, nint name);
    void PostCopyItem(uint flags, nint item, nint folder, nint name, int result, nint created);
    [PreserveSig] int PreDeleteItem(uint flags, nint item);
    void PostDeleteItem(uint flags, nint item, int result, nint recycled);
    void PreNewItem(uint flags, nint folder, nint name);
    void PostNewItem(uint flags, nint folder, nint name, nint template, uint attributes, int result, nint created);
    void UpdateProgress(uint total, uint completed);
    void ResetTimer();
    void PauseTimer();
    void ResumeTimer();
}
