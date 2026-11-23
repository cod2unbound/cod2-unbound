using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public enum HostedPhase { Stopped, Starting, Running, Stopping, Failed, PortBusy }
public sealed record HostedRunState(HostedPhase Phase, HostedStatus? Status = null, DateTimeOffset? Since = null,
    string? Problem = null, int? NextPort = null);

public interface IHostingFiles
{
    // Stage and publish a private server home. Remove previous managed archives only. Source
    // zPAM archives from a verified installed package, and return that package's optional template.
    ValueTask<string?> PrepareAsync(string serverId, string home, ZpamLine? package, CancellationToken cancellationToken);
    bool HasPackage(ZpamLine line);
    // Publish as a new directory with a unique name, never replacing a previous export.
    ValueTask<string> PublishExportAsync(string parent, string baseName, ImmutableDictionary<string, string> files,
        CancellationToken cancellationToken);
}

public sealed class HostingSession(IProcessSupervisor processes, IHostingFiles files, IGameFiles gameFiles,
    IUdpTransport udp, TimeProvider clock, Func<int, bool> portAvailable, string listenAddress = "0.0.0.0")
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Slot> slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HostedAdminQueue admin = new(udp, clock);
    private bool shuttingDown;

    private sealed class Slot(HostedServer server)
    {
        public HostedServer Server { get; } = server;
        public volatile HostedRunState State = new(HostedPhase.Starting);
        public volatile IEngineProcess? Process;
    }

    public async Task<HostedRunState> StartAsync(HostedServer server, GameBinary? binary, string dataRoot,
        string hostingRoot, CancellationToken cancellationToken = default)
    {
        if (!SafeId(server.Id))
            return new(HostedPhase.Failed, Problem: "hosting.invalidServerId");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (shuttingDown)
                return new(HostedPhase.Failed, Problem: "CoD2 Unbound is closing.");
            if (slots.TryGetValue(server.Id, out var existing) && existing.Process is not null)
                return existing.State;

            var slot = new Slot(server);
            slots[server.Id] = slot;
            if (binary is null || binary.Role != EngineRole.Dedicated || binary.Edition != server.Engine)
                return slot.State = new(HostedPhase.Failed, Problem: server.Engine == GameEdition.Cod2x
                    ? "This copy of CoD2 Unbound doesn’t include the CoD2x server. Reinstall CoD2 Unbound to restore it."
                    : "This copy of CoD2 Unbound doesn’t include the original 1.3 server. Reinstall CoD2 Unbound to restore it.");
            if (!GameArchives.Survey(gameFiles, dataRoot).IsComplete)
                return slot.State = new(HostedPhase.Failed,
                    Problem: "The server uses your game files, and they aren’t set up yet. Choose your Call of Duty 2 folder in Settings › Game Data.");
            ZpamLine? package = server.Rules == HostedRules.Zpam ? server.ZpamLine : null;
            if (package is { } line && !files.HasPackage(line))
                return slot.State = new(HostedPhase.Failed,
                    Problem: "This server needs zPAM. Get it in the server’s settings, then start again.");
            if (server.Port is < 1025 or > 65535)
                return slot.State = new(HostedPhase.Failed, Problem: "hosting.invalidEndpoint");

            bool Free(int port) => slots.Values.All(other => other.Process is null || other.Server.Port != port) && portAvailable(port);
            if (!Free(server.Port))
                return slot.State = new(HostedPhase.PortBusy, NextPort: HostingConfig.OtherPort(server, Free));

            string home = hostingRoot.TrimEnd('/', '\\') + "/servers/" + server.Id;
            try
            {
                var plan = WindowsLaunchPlan.Dedicated(binary, home, dataRoot, home,
                    server.Port, server.Reach == HostedReach.Listed, listenAddress);
                string? template = await files.PrepareAsync(server.Id, home, package, cancellationToken).ConfigureAwait(false);
                string config = HostingConfig.Render(server, template);
                // The dedicated CoD2x dvar is normally only sent in system info.
                // Advertise the actual policy in status so browsers can review public matches.
                if (server.Engine == GameEdition.Cod2x && server.Rules == HostedRules.Public)
                    config = "sets g_competitive \"0\"\n" + config;
                var start = plan.Start with
                {
                    PrivateFiles = [new("main/server.cfg", [.. Encoding.UTF8.GetBytes(config)])],
                    LogSecrets = [HostingConfig.Password(server.JoinPassword), HostingConfig.Password(server.AdminPassword)]
                };
                slot.Process = await processes.StartAsync(start, cancellationToken).ConfigureAwait(false);
                return slot.State;
            }
            catch (OperationCanceledException)
            {
                slot.State = new(HostedPhase.Stopped);
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or LaunchPlanException)
            {
                return slot.State = new(HostedPhase.Failed, Problem: "The server couldn’t start. Its log says why.");
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask<ImmutableDictionary<string, HostedRunState>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return slots.ToImmutableDictionary(entry => entry.Key, entry => entry.Value.State, StringComparer.OrdinalIgnoreCase); }
        finally { gate.Release(); }
    }

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        (Slot Slot, IEngineProcess Process)[] active;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { active = [.. slots.Values.Where(slot => slot.Process is not null).Select(slot => (slot, slot.Process!))]; }
        finally { gate.Release(); }

        foreach (var (slot, observed) in active)
        {
            HostedStatus? status = null;
            if (!observed.Exited.IsCompleted)
            {
                var timeout = TimeSpan.FromSeconds(1);
                var replies = await udp.ExchangeAsync(new(new("127.0.0.1", slot.Server.Port),
                    [.. WireParser.Marker, .. "getstatus"u8], timeout, timeout, 1, 16_384), cancellationToken).ConfigureAwait(false);
                foreach (var packet in replies.Packets)
                {
                    if (packet.AsSpan().StartsWith(WireParser.Marker) &&
                        HostedStatus.Parse(Encoding.Latin1.GetString(packet.AsSpan()[4..])) is { } parsed)
                    {
                        status = parsed;
                        break;
                    }
                }
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(slot.Process, observed))
                    continue;
                if (observed.Exited.IsCompleted)
                    await ReapAsync(slot, observed).ConfigureAwait(false);
                else if (slot.State.Phase == HostedPhase.Running || status is not null && slot.State.Phase == HostedPhase.Starting)
                    slot.State = new(HostedPhase.Running, status, slot.State.Since ?? clock.GetUtcNow());
            }
            finally { gate.Release(); }
        }
    }

    public async Task WatchAsync(IProgress<ImmutableDictionary<string, HostedRunState>> updates, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollAsync(cancellationToken).ConfigureAwait(false);
            updates.Report(await SnapshotAsync(cancellationToken).ConfigureAwait(false));
            await Task.Delay(TimeSpan.FromSeconds(3), clock, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(string id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (slots.TryGetValue(id, out var slot) && slot.Process is { } process)
                await StopLockedAsync(slot, process, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public Task StopAllAsync(CancellationToken cancellationToken = default) =>
        StopAllAsync(shutdown: false, cancellationToken);

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        StopAllAsync(shutdown: true, cancellationToken);

    private async Task StopAllAsync(bool shutdown, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(slots.Values.Where(slot => slot.Process is not null)
                .Select(slot => StopLockedAsync(slot, slot.Process!, cancellationToken))).ConfigureAwait(false);
            shuttingDown |= shutdown;
        }
        finally { gate.Release(); }
    }

    public Task<bool> NextMapAsync(string id, CancellationToken cancellationToken = default) =>
        QueueAsync(id, null, cancellationToken);

    public Task<bool> KickAsync(string id, HostedStatusPlayer player, CancellationToken cancellationToken = default) =>
        QueueAsync(id, player, cancellationToken);

    public ValueTask<string> ExportAsync(HostedServer server, string parent, string? template, ZpamRelease? release,
        bool passwords, bool matchSettings, CancellationToken cancellationToken = default) =>
        files.PublishExportAsync(parent, HostingConfig.FileName(server) + "-server",
            HostingConfig.Export(server, template, release, passwords, matchSettings), cancellationToken);

    private async Task<bool> QueueAsync(string id, HostedStatusPlayer? player, CancellationToken cancellationToken)
    {
        Slot? slot;
        IEngineProcess? process;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            slots.TryGetValue(id, out slot);
            process = slot?.Process;
            if (slot is null || process is null || slot.State.Phase is not (HostedPhase.Starting or HostedPhase.Running))
                return false;
        }
        finally { gate.Release(); }

        bool StillOwned() => ReferenceEquals(slot.Process, process) &&
            !process.Exited.IsCompleted && slot.State.Phase is HostedPhase.Starting or HostedPhase.Running &&
            (player is null || slot.State.Status is { } status && HostedDashboard.CanKick(status.Players, player));
        return player is null
            ? await admin.NextMapAsync(slot.Server, StillOwned, cancellationToken).ConfigureAwait(false)
            : await admin.KickAsync(slot.Server, player, StillOwned, cancellationToken).ConfigureAwait(false);
    }

    private static async Task StopLockedAsync(Slot slot, IEngineProcess process, CancellationToken cancellationToken)
    {
        if (process.Exited.IsCompleted)
        {
            await ReapAsync(slot, process).ConfigureAwait(false);
            return;
        }
        slot.State = new(HostedPhase.Stopping);
        await process.StopAsync(cancellationToken).ConfigureAwait(false);
        await ReapAsync(slot, process).ConfigureAwait(false);
    }

    private static async Task ReapAsync(Slot slot, IEngineProcess process)
    {
        int code = await process.Exited.ConfigureAwait(false);
        slot.Process = null;
        slot.State = code == 0 || slot.State.Phase == HostedPhase.Stopping
            ? new(HostedPhase.Stopped)
            : new(HostedPhase.Failed, Problem: slot.State.Phase == HostedPhase.Starting
                ? "The server stopped while starting. Its log, below, says why."
                : "The server stopped with an error. Its log says why.");
        await process.DisposeAsync().ConfigureAwait(false);
    }

    private static bool SafeId(string id) =>
        id.Length is > 0 and <= 64 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
}
