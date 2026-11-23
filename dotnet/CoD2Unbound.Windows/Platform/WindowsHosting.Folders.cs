using System.ComponentModel;
using System.Diagnostics;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed partial class WindowsHosting
{
    internal async Task<HostedServer> ReadSavedAsync(string id) =>
        HostedServer.FindSaved(await ReadAsync(), id) ?? throw new IOException("This server is no longer saved in Hosting.");

    private string ServerFolder(string id) => WindowsFiles.Under(Path.Join(root, "servers"), id);

    internal bool HasServerFolder(string id) => Directory.Exists(ServerFolder(id));

    internal Task RevealServerFolderAsync(string id) => RevealFolderAsync(ServerFolder(id));

    internal static Task RevealFolderAsync(string path)
    {
        using var folderHandle = new WindowsFiles.DirectoryHandles(path);
        try { using var explorer = Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Win32Exception error) { throw new IOException("File Explorer couldn’t show the server folder.", error); }
        return Task.CompletedTask;
    }

    internal async Task DeleteAsync(string id, nint owner)
    {
        await writes.WaitAsync();
        try
        {
            await using var startLease = await WindowsGameSessionHost.OpenLeaseAsync(root, ".hosting-start.lock", default);
            await using var settingsLease = await WindowsGameSessionHost.OpenLeaseAsync(root, ".hosted-servers.lock", default);
            if ((await session.SnapshotAsync()).TryGetValue(id, out var running) && IsActive(running))
                throw new IOException("Stop the server before deleting it.");
            var saved = await ReadAsync();
            if (HostedServer.FindSaved(saved, id) is not { } server)
                return;

            string home = ServerFolder(server.Id);
            using var parent = Directory.Exists(home) ? new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(home)!) : null;
            string? recycled = null;
            if (parent is not null)
            {
                using (new WindowsFiles.DirectoryHandles(home)) { }
                recycled = WindowsRecycleBin.Recycle(home, owner);
            }
            try
            {
                await folder.WriteAsync(ServersFile, HostedServerJson.Write(saved.Remove(server)), default);
            }
            catch (Exception error) when (recycled is not null && (error is IOException or UnauthorizedAccessException))
            {
                try { WindowsRecycleBin.Restore(recycled, home, owner); }
                catch (IOException restoreError)
                {
                    throw new IOException("The server settings couldn’t be removed. Its folder is in the Recycle Bin.",
                        new AggregateException(error, restoreError));
                }
                throw;
            }
        }
        finally { writes.Release(); }
    }
}
