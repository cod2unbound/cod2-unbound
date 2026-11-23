using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public enum GameStartState { Started, AlreadyRunning, Attached, NeedsSetup, MissingData }
public enum GameLinkDelivery { Pending, Delivered, Failed }

/// <summary>Identify a verified client by process ID and creation time, so PID reuse cannot route a command.</summary>
public readonly record struct GameProcessIdentity(int Id, DateTimeOffset CreatedAt);
public sealed record RunningGameActivity(GameProcessIdentity Game, string? ServerAddress, string? DemoName);

public sealed record GameSessionState(DateTimeOffset? StartedAt, ImmutableArray<GameProcessIdentity> Games, GameExit? LastExit,
    bool Quitting = false, RunningGameActivity? Activity = null)
{
    public bool Running => !Games.IsEmpty;
    public GameProcessIdentity? ActiveGame => Activity?.Game ?? (Running ? Games[0] : null);

    public string? PlayingFor(DateTimeOffset now)
    {
        if (ActiveGame is not { } game)
            return null;
        int minutes = (int)Math.Max(0, (now - game.CreatedAt).TotalMinutes);
        return minutes switch
        {
            0 => "Playing for less than a minute",
            < 60 => $"Playing for {minutes} min",
            _ => $"Playing for {minutes / 60} h {minutes % 60} min"
        };
    }
}

public interface IGameLaunchReservation : IAsyncDisposable
{
    WindowsLaunchPlan Plan { get; }
    // A failed commit leaves a recoverable reservation. Disposing a reservation rolls back only
    // uncommitted changes and relinquishes the cross-process launch lease.
    ValueTask CommitAsync(GameProcessIdentity game, bool playedIntro, CancellationToken cancellationToken);
}

public interface IGameSessionHost
{
    // All observed games must be verified active clients from the install inventory, never hosted
    // servers. Controls and query results must be bound to the creation-time identity.
    ValueTask<ImmutableArray<GameProcessIdentity>> FindClientsAsync(CancellationToken cancellationToken);
    GameProcessIdentity IdentityOf(IEngineProcess process);
    ValueTask<IGameLaunchReservation> ReserveAsync(ClientLaunchRequest request, CancellationToken cancellationToken);
    ValueTask ReconcileProfilesAsync(ImmutableArray<GameProcessIdentity> games, CancellationToken cancellationToken);
    ValueTask<GameLinkDelivery> DeliverAsync(GameProcessIdentity target, LaunchLink link, CancellationToken cancellationToken);
    ValueTask<bool> RequestQuitAsync(GameProcessIdentity target, bool force, CancellationToken cancellationToken);
    (string? Log, string? Crash) FindExitFiles(DateTimeOffset since);
}

public sealed class GameLifecycle(IProcessSupervisor processes, IGameSessionHost host, IGameFiles files, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1);
    private IEngineProcess? owned;
    private GameProcessIdentity? ownedIdentity;
    private int? lastExitCode;
    private int quitting;
    private GameSessionState state = new(null, [], null);

    public GameSessionState State => Volatile.Read(ref state) with { Quitting = Volatile.Read(ref quitting) != 0 };

    public async Task<GameStartState> StartAsync(ClientLaunchRequest request, bool setupComplete,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool alreadyKnown = State.Running;
            await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
            if (State.Running)
                return alreadyKnown ? GameStartState.AlreadyRunning : GameStartState.Attached;
            if (!setupComplete)
                return GameStartState.NeedsSetup;
            if (!GameArchives.Survey(files, request.DataPath).IsComplete)
                return GameStartState.MissingData;

            await using var reservation = await host.ReserveAsync(request, cancellationToken).ConfigureAwait(false);
            // Check again inside the cross-process lease after any preflight or other launcher wins it.
            await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
            if (State.Running)
                return GameStartState.Attached;

            var start = reservation.Plan.Start;
            if (request.Link is { HasPassword: true } link)
                start = start with { LogSecrets = [.. start.LogSecrets, link.Password] };
            var process = await processes.StartAsync(start, cancellationToken).ConfigureAwait(false);
            try
            {
                var identity = host.IdentityOf(process);
                await reservation.CommitAsync(identity, reservation.Plan.PlaysIntroFilms, CancellationToken.None).ConfigureAwait(false);
                owned = process;
                ownedIdentity = identity;
                lastExitCode = null;
                string? address = request.Link is { } target ? LaunchLink.CanonicalAddress(target.Address) : null;
                Publish(new(clock.GetUtcNow(), [identity], null,
                    Activity: new(identity, address, Path.GetFileNameWithoutExtension(request.Demo))));
                return GameStartState.Started;
            }
            catch
            {
                try { await process.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { await process.DisposeAsync().ConfigureAwait(false); }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task WatchAsync(IProgress<GameSessionState> updates, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
            updates.Report(State);
            await Task.Delay(TimeSpan.FromMilliseconds(250), clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileLockedAsync(CancellationToken cancellationToken)
    {
        if (owned is { Exited.IsCompleted: true } exited)
        {
            lastExitCode = await exited.Exited.ConfigureAwait(false);
            owned = null;
            ownedIdentity = null;
            await exited.DisposeAsync().ConfigureAwait(false);
        }

        var observed = await host.FindClientsAsync(cancellationToken).ConfigureAwait(false);
        var games = observed.ToImmutableHashSet();
        if (ownedIdentity is { } identity)
            games = games.Add(identity);
        var ordered = games.OrderBy(game => game.CreatedAt).ThenBy(game => game.Id).ToImmutableArray();
        await host.ReconcileProfilesAsync(ordered, cancellationToken).ConfigureAwait(false);
        var previous = State;
        if (!ordered.IsEmpty)
        {
            var activity = previous.Activity is { } active && ordered.Contains(active.Game) ? active : null;
            Publish(new(previous.StartedAt ?? clock.GetUtcNow(), ordered, null, Activity: activity));
        }
        else if (previous.StartedAt is { } since)
        {
            int? status = lastExitCode;
            lastExitCode = null;
            var reports = status is 0 or 1 ? (Log: (string?)null, Crash: (string?)null) : host.FindExitFiles(since);
            Publish(new(null, [], GameExit.Classify(status, reports.Log, reports.Crash)));
        }
    }

    public async Task<GameLinkDelivery> ConnectToRunningAsync(GameProcessIdentity target, LaunchLink link,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            if (!State.Games.Contains(target))
                return GameLinkDelivery.Failed;
            var result = await host.DeliverAsync(target, link, cancellationToken).ConfigureAwait(false);
            if (result == GameLinkDelivery.Delivered)
            {
                // The engine accepted the request. Record it even if the caller's time budget
                // expired while delivery was in progress.
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (State.Games.Contains(target))
                        Publish(State with { Activity = new(target, LaunchLink.CanonicalAddress(link.Address), null) });
                }
                finally { gate.Release(); }
            }
            if (result != GameLinkDelivery.Pending)
                return result;
            await Task.Delay(TimeSpan.FromMilliseconds(200), clock, cancellationToken).ConfigureAwait(false);
        }
        return GameLinkDelivery.Pending;
    }

    public async Task<bool> QuitGameAsync(GameProcessIdentity target, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref quitting, 1, 0) != 0)
            return false;
        try
        {
            long started = clock.GetTimestamp();
            var grace = TimeSpan.FromSeconds(10);
            var deadline = TimeSpan.FromSeconds(20);
            var interval = TimeSpan.FromMilliseconds(250);
            bool requested = false;
            bool forced = false;
            while (true)
            {
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                if (!State.Games.Contains(target))
                    return true;

                var elapsed = clock.GetElapsedTime(started);
                if (!forced && elapsed >= grace)
                {
                    await host.RequestQuitAsync(target, force: true, cancellationToken).ConfigureAwait(false);
                    forced = true;
                }
                else if (!requested && elapsed < grace)
                    requested = await host.RequestQuitAsync(target, force: false, cancellationToken).ConfigureAwait(false);
                if (elapsed >= deadline)
                    return false;

                var untilBoundary = (forced ? deadline : grace) - clock.GetElapsedTime(started);
                if (untilBoundary > TimeSpan.Zero)
                    await Task.Delay(untilBoundary < interval ? untilBoundary : interval, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref quitting, 0); }
    }

    private void Publish(GameSessionState next) => Volatile.Write(ref state, next);
}
