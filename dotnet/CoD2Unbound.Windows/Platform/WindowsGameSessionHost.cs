using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsGameSessionHost(WindowsEngineInventory inventory, IGameFiles files, string home,
    ISettingsFolder gameFolder, Func<WindowsRenderMode> renderer, Func<ValueTask> saveIntro) : IGameSessionHost
{
    private string? latestLog;

    public ValueTask<ImmutableArray<GameProcessIdentity>> FindClientsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(inventory.FindRunning(EngineRole.Client, cancellationToken));

    public GameProcessIdentity IdentityOf(IEngineProcess process)
    {
        var child = (WindowsEngineProcess)process;
        latestLog = child.LogPath;
        return child.Identity;
    }

    public async ValueTask<IGameLaunchReservation> ReserveAsync(ClientLaunchRequest request, CancellationToken cancellationToken)
    {
        var lease = await OpenLeaseAsync(home, ".launcher-start.lock", cancellationToken).ConfigureAwait(false);
        FileStream? profileLease = null;
        try
        {
            profileLease = await OpenLeaseAsync(home, "main/players/.studio.lock", cancellationToken).ConfigureAwait(false);
            var binary = inventory.Installed.FirstOrDefault(binary =>
                binary.Role == EngineRole.Client && binary.Edition == request.Edition)
                ?? throw new IOException("The game executable is missing.");
            var profile = await ConfigProfileStore.ReadActiveForLaunchAsync(gameFolder,
                request.Edition == GameEdition.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock, cancellationToken).ConfigureAwait(false);
            if (profile is { } loadout)
                request = request with { Studio = loadout.Studio, Loadout = loadout.Values };
            // The Settings switch records consent. Windows reporting ships separately.
            var plan = WindowsLaunchPlan.Client(request with { Settings = request.Settings with { SendCoD2xReports = false } },
                binary, home, renderer(), files);
            return new Reservation(plan, lease, profileLease, saveIntro);
        }
        catch
        {
            profileLease?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    internal static async ValueTask<FileStream> OpenLeaseAsync(string home, string name, CancellationToken cancellationToken)
    {
        string path = WindowsFiles.Under(home, name);
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!, create: true);
        for (int retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return WindowsFiles.OpenExclusiveLock(path);
            }
            catch (IOException error) when ((error.HResult & 0xffff) == 32 && retry < 40)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask ReconcileProfilesAsync(ImmutableArray<GameProcessIdentity> games, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask<GameLinkDelivery> DeliverAsync(GameProcessIdentity target, LaunchLink link, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GameLinkDelivery.Failed);

    public ValueTask RequestQuitAsync(GameProcessIdentity target, bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var client = Process.GetProcessById(target.Id);
            if (WindowsEngineProcess.CreationTime(client.SafeHandle) == target.CreatedAt)
            {
                if (force)
                    client.Kill();
                else
                    client.CloseMainWindow();
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
        return ValueTask.CompletedTask;
    }

    public (string? Log, string? Crash) FindExitFiles(DateTimeOffset since)
    {
        using var directory = new WindowsFiles.DirectoryHandles(home);
        var reports = new DirectoryInfo(home).EnumerateFiles()
            .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(entry => new CrashFile(entry.Name, entry.FullName, entry.LastWriteTimeUtc));
        return (latestLog, CrashFiles.Since(reports, since).LastOrDefault()?.Path);
    }

    private sealed class Reservation(WindowsLaunchPlan plan, FileStream lease, FileStream profileLease, Func<ValueTask> saveIntro)
        : IGameLaunchReservation
    {
        public WindowsLaunchPlan Plan => plan;

        public async ValueTask CommitAsync(GameProcessIdentity game, bool playedIntro, CancellationToken cancellationToken)
        {
            if (playedIntro)
                await saveIntro().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            profileLease.Dispose();
            lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
