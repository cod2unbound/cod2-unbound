using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsGameSessionHost(WindowsEngineInventory inventory, IGameFiles files, string home,
    Func<GameSettings> settings, Func<WindowsRenderMode> renderer, Func<ValueTask> saveIntro) : IGameSessionHost
{
    private string? latestLog;
    private WeakReference<WindowsEngineProcess>? ownedClient;
    private ImmutableHashSet<GameProcessIdentity>? reconciledGames;
    private bool reserving;
    private bool migrationComplete;
    internal string? MigrationProblem { get; private set; }

    public ValueTask<ImmutableArray<GameProcessIdentity>> FindClientsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(inventory.FindRunning(EngineRole.Client, cancellationToken));

    public GameProcessIdentity IdentityOf(IEngineProcess process)
    {
        var child = (WindowsEngineProcess)process;
        latestLog = child.LogPath;
        ownedClient = new(child);
        return child.Identity;
    }

    public async ValueTask<IGameLaunchReservation> ReserveAsync(ClientLaunchRequest request, CancellationToken cancellationToken)
    {
        var lease = await OpenLeaseAsync(home, ".launcher-start.lock", cancellationToken).ConfigureAwait(false);
        FileStream? profileLease = null;
        ConfigStudioSession? studio = null;
        try
        {
            profileLease = await OpenLeaseAsync(home, "main/players/.studio.lock", cancellationToken).ConfigureAwait(false);
            var binary = inventory.Installed.FirstOrDefault(binary =>
                binary.Role == EngineRole.Client && binary.Edition == request.Edition)
                ?? throw new IOException("The game executable is missing.");
            TryMigrate(profileLease);
            studio = OpenStudio(request.Edition, profileLease);
            request = studio.PrepareClientLaunch(request);
            string? requestedMonitor = request.Loadout?.GetValueOrDefault("r_monitor");
            var display = WindowsDisplays.ForLoadout(WindowsDisplayInventory.ReadForGame(), request.Settings.WindowsMonitor, requestedMonitor);
            var plan = WindowsLaunchPlan.Client(request, binary, home, renderer(), files, display: display);
            var adjusted = WindowsDisplays.Adjustment(request.Loadout?.GetValueOrDefault("r_mode") ?? request.Settings.Resolution, display);
            int? monitor = display?.Monitor.ToString(System.Globalization.CultureInfo.InvariantCulture) == requestedMonitor ? null : display?.Monitor;
            if (request.Studio is { } loadout && (adjusted.HasValue || monitor.HasValue))
                studio.ProtectLaunchDisplay(loadout.ProfileId, adjusted, monitor);
            reserving = true;
            return new Reservation(this, plan, lease, profileLease, studio, saveIntro);
        }
        catch
        {
            try
            {
                if (studio is not null && studio.State.LaunchReserved)
                    studio.CancelLaunch();
            }
            finally
            {
                profileLease?.Dispose();
                lease.Dispose();
            }
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

    private ConfigStudioSession OpenStudio(GameEdition edition, FileStream profileLease) =>
        new(new WindowsConfigProfiles(home, 0, profileLease), edition == GameEdition.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock,
            () => !inventory.FindRunning(EngineRole.Client, default).IsEmpty, TimeProvider.System);

    public async ValueTask ReconcileProfilesAsync(ImmutableArray<GameProcessIdentity> games, CancellationToken cancellationToken)
    {
        // StartAsync checks for clients again while the reservation already holds both leases.
        if (reserving || reconciledGames?.SetEquals(games) == true)
            return;
        await using var launch = await OpenLeaseAsync(home, ".launcher-start.lock", cancellationToken).ConfigureAwait(false);
        await using var profile = await OpenLeaseAsync(home, "main/players/.studio.lock", cancellationToken).ConfigureAwait(false);
        var verified = games.ToImmutableHashSet().Union(inventory.FindRunning(EngineRole.Client, cancellationToken));
        OpenStudio(settings().Engine, profile).ReconcileGames([.. verified]);
        if (verified.IsEmpty)
            TryMigrate(profile);
        reconciledGames = verified;
    }

    private void TryMigrate(FileStream profileLease)
    {
        if (migrationComplete)
            return;
        var current = settings();
        try
        {
            var studio = OpenStudio(current.Engine, profileLease);
            if (studio.IsReadOnly)
                return;
            studio.MigrateLegacySettings(current);
            migrationComplete = true;
            MigrationProblem = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LaunchPlanException)
        {
            MigrationProblem = $"Couldn’t move existing settings into a loadout. {error.Message}";
        }
    }

    public ValueTask<GameLinkDelivery> DeliverAsync(GameProcessIdentity target, LaunchLink link, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ownedClient?.TryGetTarget(out var process) == true && process.Identity == target)
            process.Protect(link.Password);

        return new(Task.Run(() => WindowsRunningGameJoin.Deliver(target, link), cancellationToken));
    }

    public ValueTask<bool> RequestQuitAsync(GameProcessIdentity target, bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if UPDATE_FIXTURES
        if (WindowsLauncherSession.HasFixtureHome &&
            Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_CLOSE_GAME") is { } closeFixture &&
            (closeFixture == "refuse" || (closeFixture == "force" && !force)))
            return ValueTask.FromResult(false);
#endif
        try
        {
            using var client = Process.GetProcessById(target.Id);
            if (WindowsEngineProcess.CreationTime(client.SafeHandle) != target.CreatedAt || client.HasExited)
                return ValueTask.FromResult(false);
            if (force)
                return ValueTask.FromResult(WindowsEngineProcess.TerminateProcess(client.SafeHandle, 0));
            nint window = WindowsRunningGameJoin.ClientWindow(client);
            return ValueTask.FromResult(window != 0 && PostMessage(window, 0x0010, 0, 0)); // WM_CLOSE
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or IOException)
        {
            return ValueTask.FromResult(false);
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    public (string? Log, string? Crash) FindExitFiles(DateTimeOffset since)
    {
        using var directory = new WindowsFiles.DirectoryHandles(home);
        var reports = new DirectoryInfo(home).EnumerateFiles()
            .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(entry => new CrashFile(entry.Name, entry.FullName, entry.LastWriteTimeUtc));
        return (latestLog, CrashFiles.Since(reports, since).LastOrDefault()?.Path);
    }

    private sealed class Reservation(WindowsGameSessionHost host, WindowsLaunchPlan plan, FileStream lease, FileStream profileLease,
        ConfigStudioSession studio, Func<ValueTask> saveIntro) : IGameLaunchReservation
    {
        public WindowsLaunchPlan Plan => plan;

        public async ValueTask CommitAsync(GameProcessIdentity game, bool playedIntro, CancellationToken cancellationToken)
        {
            if (playedIntro)
                await saveIntro().ConfigureAwait(false);
            studio.CommitLaunch(game);
            host.reconciledGames = ImmutableHashSet.Create(game);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (studio.State.LaunchReserved)
                {
                    host.reconciledGames = null;
                    studio.CancelLaunch();
                }
            }
            finally
            {
                host.reserving = false;
                profileLease.Dispose();
                lease.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }
}
