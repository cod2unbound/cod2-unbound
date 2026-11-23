using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using CoD2Unbound.Core;
using Microsoft.Win32.SafeHandles;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsProcessSupervisor(WindowsEngineInventory inventory, string logs, bool logInEngineHome = false) : IProcessSupervisor
{
    public ValueTask<ImmutableArray<int>> FindEngineProcessesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ImmutableArray<int>>([..inventory.FindRunning(role: null, cancellationToken).Select(game => game.Id)]);

    public async ValueTask<IEngineProcess> StartAsync(EngineProcessStart start, CancellationToken cancellationToken)
    {
        using var home = new WindowsFiles.DirectoryHandles(start.HomeDirectory, create: true);
        var privateFiles = new List<string>();
        WindowsSessionLog? log = null;
        AnonymousPipeServerStream? pipe = null;
        try
        {
            foreach (var file in start.PrivateFiles)
            {
                string path = WindowsFiles.Under(start.HomeDirectory, file.RelativePath);
                using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!, create: true);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                privateFiles.Add(file.RelativePath);
                await stream.WriteAsync(file.Data.AsMemory(), cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            log = new WindowsSessionLog(logInEngineHome ? Path.Join(start.HomeDirectory, "logs") : logs, start);
            pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            cancellationToken.ThrowIfCancellationRequested();
            var handle = NativeEngineStart.Start(start, pipe, out int id);
            try
            {
                pipe.DisposeLocalCopyOfClientHandle();
                return new WindowsEngineProcess(handle, id, pipe, log, () => DeletePrivateFiles(start.HomeDirectory, privateFiles));
            }
            catch
            {
                WindowsEngineProcess.TerminateProcess(handle, 2);
                handle.Dispose();
                throw;
            }
        }
        catch
        {
            pipe?.Dispose();
            log?.Dispose();
            DeletePrivateFiles(start.HomeDirectory, privateFiles);
            throw;
        }
    }

    private static void DeletePrivateFiles(string home, List<string> files)
    {
        foreach (string name in files)
        {
            try
            {
                string path = WindowsFiles.Under(home, name);
                using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!);
                File.Delete(path);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}

internal sealed class WindowsEngineProcess : IEngineProcess
{
    private readonly SafeProcessHandle process;
    private readonly WindowsSessionLog sessionLog;
    internal string LogPath { get; }
    internal GameProcessIdentity Identity { get; }

    internal WindowsEngineProcess(SafeProcessHandle process, int id, AnonymousPipeServerStream pipe,
        WindowsSessionLog log, Action cleanUp)
    {
        this.process = process;
        sessionLog = log;
        Id = id;
        Identity = new(id, CreationTime(process));
        LogPath = log.Path;
        Exited = ObserveAsync(pipe, log, cleanUp);
    }

    internal void Protect(string secret) => sessionLog.Protect(secret);

    public int Id { get; }
    public Task<int> Exited { get; }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (!Exited.IsCompleted)
        {
            try
            {
                using var child = Process.GetProcessById(Id);
                if (CreationTime(child.SafeHandle) == Identity.CreatedAt)
                    child.CloseMainWindow();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
            try { await Exited.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                if (!TerminateProcess(process, 2) && !Exited.IsCompleted &&
                    (!GetExitCodeProcess(process, out int code) || code == 259))
                    throw NativeEngineStart.LastError("Windows could not stop Call of Duty 2.");
            }
        }
        await Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Exited.ConfigureAwait(false); }
        finally { process.Dispose(); }
    }

    private async Task<int> ObserveAsync(AnonymousPipeServerStream pipe, WindowsSessionLog log, Action cleanUp)
    {
        using var stopDrain = new CancellationTokenSource();
        var output = log.DrainAsync(pipe, stopDrain.Token);
        try
        {
            using var wait = new EventWaitHandle(false, EventResetMode.AutoReset)
            {
                SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false)
            };
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => complete.SetResult(), null,
                Timeout.Infinite, executeOnlyOnce: true);
            try { await complete.Task.ConfigureAwait(false); }
            finally { registration.Unregister(null); }

            // A descendant may have inherited the writer. Give buffered output a bounded drain after child exit.
            stopDrain.CancelAfter(TimeSpan.FromSeconds(2));
            await output.ConfigureAwait(false);
            if (!GetExitCodeProcess(process, out int status))
                throw NativeEngineStart.LastError("Windows could not read the engine's exit code.");
            return status;
        }
        finally
        {
            await stopDrain.CancelAsync().ConfigureAwait(false);
            await output.ConfigureAwait(false);
            try { cleanUp(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static DateTimeOffset CreationTime(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out long created, out _, out _, out _))
            throw NativeEngineStart.LastError("Windows could not identify the engine process.");
        return DateTimeOffset.FromFileTime(created).ToUniversalTime();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint status);
}

internal sealed class WindowsSessionLog : IDisposable
{
    private const string Prefix = "engine";
    private readonly FileStream file;
    private readonly GameSessionLog filter;

    internal WindowsSessionLog(string folder, EngineProcessStart start)
    {
        using var directory = new WindowsFiles.DirectoryHandles(folder, create: true);
        string name = FormattableString.Invariant($"{Prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.log");
        Path = System.IO.Path.Join(folder, name);
        var existing = new DirectoryInfo(folder).EnumerateFiles(Prefix + "-*.log")
            .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(entry => new MediaFile(entry.Name, entry.LastWriteTimeUtc, entry.Length));
        foreach (string old in GameSessionLog.Prune(Prefix, name, existing))
        {
            // Another launcher or dedicated server can still be writing a log.
            try { File.Delete(System.IO.Path.Join(folder, old)); }
            catch (IOException) { }
        }
        // The launcher can read the filtered log while the engine is still running.
        file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1);
        filter = new GameSessionLog(start.LogSecrets, start.HomeDirectory);
    }

    internal string Path { get; }

    internal void Protect(string secret) => filter.Protect(secret);

    internal async Task DrainAsync(AnonymousPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using (pipe)
        using (this)
        {
            var buffer = new byte[8192];
            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                    PeekNamedPipe(pipe.SafePipeHandle, 0, 0, 0, out int available, 0))
                {
                    if (available == 0)
                    {
                        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    int count = pipe.Read(buffer, 0, Math.Min(available, buffer.Length));
                    if (count == 0)
                        break;
                    file.Write(filter.Append(buffer.AsSpan(0, count)).AsSpan());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
            try { file.Write(filter.Append([], complete: true).AsSpan()); }
            catch (IOException) { }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, nint buffer, uint size, nint read, out int available, nint remaining);

    public void Dispose() => file.Dispose();
}
