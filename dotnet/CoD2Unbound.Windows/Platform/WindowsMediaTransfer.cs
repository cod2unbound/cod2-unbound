using System.Runtime.InteropServices;
using CoD2Unbound.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CoD2Unbound.Windows.Platform;

internal sealed partial class WindowsMediaFiles
{
    internal async Task CopyAsync(MediaKind kind, string name)
    {
        string relative = Relative(kind, name);
        string path = WindowsFiles.Under(home, relative);
        using var parents = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!);
        using var input = WindowsFiles.Open(home, relative, FileShare.Read);
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetStorageItems([file], readOnly: true);

        using var bitmap = new InMemoryRandomAccessStream();
        if (kind == MediaKind.Screenshot)
        {
            if (Path.GetExtension(name).Equals(".tga", StringComparison.OrdinalIgnoreCase))
            {
                TgaThumbnail image = await Task.Run(() => TgaThumbnail.Read(input, ushort.MaxValue));
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, bitmap);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    (uint)image.Width, (uint)image.Height, 96, 96, image.Bgra);
                await encoder.FlushAsync();
                bitmap.Seek(0);
                data.SetBitmap(RandomAccessStreamReference.CreateFromStream(bitmap));
            }
            else
            {
                data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            }
        }

        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    internal async Task ShareAsync(MediaKind kind, string name, WindowsMediaShare share)
    {
        string relative = Relative(kind, name);
        string path = WindowsFiles.Under(home, relative);
        using var parents = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!);
        using var input = WindowsFiles.Open(home, relative, FileShare.Read);
        share.Show(await StorageFile.GetFileFromPathAsync(path));
    }
}

internal sealed class WindowsMediaShare
{
    private static readonly Guid ManagerId = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    private readonly nint owner;
    private readonly IDataTransferManagerInterop interop;
    private readonly DataTransferManager manager;
    private StorageFile? pending;

    internal WindowsMediaShare(nint owner)
    {
        this.owner = owner;
        interop = DataTransferManager.As<IDataTransferManagerInterop>();
        nint pointer = interop.GetForWindow(owner, ManagerId);
        try { manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
        manager.DataRequested += OnDataRequested;
    }

    internal void Show(StorageFile file)
    {
        pending = file;
        try { interop.ShowShareUIForWindow(owner); }
        catch
        {
            pending = null;
            throw;
        }
    }

    private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var file = pending;
        pending = null;
        if (file is null)
            return;
        var data = args.Request.Data;
        data.Properties.Title = file.Name;
        data.RequestedOperation = DataPackageOperation.Copy;
        data.SetStorageItems([file], readOnly: true);
    }

    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow(nint window, in Guid iid);
        void ShowShareUIForWindow(nint window);
    }
}
