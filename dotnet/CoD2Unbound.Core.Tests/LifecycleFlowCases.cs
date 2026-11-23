using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void LifecycleFlowCases()
    {
        Check("Studio cancellation and restart restore exact temporary selection", () =>
        {
            var files = new FixtureProfileFiles();
            var store = new ConfigProfileStore(files, ConfigDialect.Cod2x);
            var main = store.Create("Main", "Main", new("seta sensitivity 2\r\n"u8, ConfigDialect.Cod2x));
            var other = store.Create("Other", "Other", new("seta sensitivity 3\r\n"u8, ConfigDialect.Cod2x));
            files.Files[ActivePath] = [.. "Main\r\n// retained selection\r\n"u8];
            var original = files.Files[ActivePath];
            var clock = new FixedClock();
            var identity = new GameProcessIdentity(100, clock.GetUtcNow());
            bool running = false;
            var session = new ConfigStudioSession(files, ConfigDialect.Cod2x, () => running, clock);
            session.Refresh();
            session.Focus("Other");
            session.SetDraft(other.Document.SetSetting("sensitivity", "4"));
            ProfileRejects(ConfigProfileFailure.Conflict, () => session.PrepareLaunch());
            files.Files["main/players/Other/config_mp.cfg"] = [.. "seta sensitivity 8\r\n"u8];
            session.Refresh();
            ProfileRejects(ConfigProfileFailure.Conflict, () => session.SaveDraft("Stale"));
            session.RebaseDraft(session.State.Current!.Version);
            _ = session.SaveDraft("Reapply from new version");
            Equal("4", store.Read("Other").Document.EffectiveSetting("sensitivity"));
            Equal("Other", session.PrepareLaunch(connectionProfileId: "Other")!.ProfileId);
            Equal("Other", store.ActiveSelection().ProfileId);
            ProfileRejects(ConfigProfileFailure.GameRunning, () => session.PrepareLaunch());
            session.CancelLaunch();
            Sequence(original, files.Files[ActivePath]);

            var fix = new ConfigConnectionLaunch("localhost", ImmutableDictionary<string, string>.Empty.Add("com_maxfps", "250"));
            var launch = session.PrepareLaunch(LaunchLink.Direct("localhost"), "Main", fix, main.Version);
            Equal(true, launch!.ConnectionIsolated);
            Equal(true, launch.ProfileId.StartsWith("csconn", StringComparison.Ordinal));
            session.CommitLaunch(identity);
            running = true;
            var restarted = new ConfigStudioSession(files, ConfigDialect.Cod2x, () => running, clock);
            restarted.ReconcileGames([identity]);
            Equal(true, restarted.IsReadOnly);
            Equal(false, restarted.State.Profiles.Any(profile => profile.Id == launch.ProfileId));
            files.Files[ConfigPath] = [.. "seta sensitivity 5\r\n"u8];
            running = false;
            restarted.ReconcileGames([]);
            restarted.ReconcileGames([]);
            Sequence(original, files.Files[ActivePath]);
            Equal(null, restarted.State.Profiles.FirstOrDefault(profile => profile.Id == launch.ProfileId));
            Equal(1, store.History("Main").Count(entry => entry.Source == ConfigHistorySource.GameSession));
        });

        Check("Game deduplicates starts and drives reconnect and quit on virtual time", () =>
        {
            var clock = new AdvancingClock();
            var processes = new TestProcessSupervisor();
            var files = new MemoryPlatform("C:/data");
            var host = new TestGameHost(processes, clock);
            var lifecycle = new GameLifecycle(processes, host, files, clock);
            var request = new ClientLaunchRequest(GameEdition.Cod2x, new GameSettings(), "C:/data", "C:/home");
            Equal(GameStartState.NeedsSetup, lifecycle.StartAsync(request, false).GetAwaiter().GetResult());
            Equal(GameStartState.MissingData, lifecycle.StartAsync(request, true).GetAwaiter().GetResult());
            foreach (string archive in GameArchives.Required)
                files.PutFile(archive);
            Equal(GameStartState.Started, lifecycle.StartAsync(request, true).GetAwaiter().GetResult());
            Equal(GameStartState.AlreadyRunning, lifecycle.StartAsync(request, true).GetAwaiter().GetResult());
            Equal(1, processes.Starts.Count);
            Equal(1, host.Reservations);
            Equal<string?>(null, processes.Starts[0].EnvironmentChanges[CrashReporting.ConsentVariable]);
            var consenting = request with { Settings = request.Settings with { SendCoD2xReports = true } };
            var consentPlan = WindowsLaunchPlan.Client(consenting, new(GameEdition.Cod2x, EngineRole.Client, "C:/engine.exe"), "C:/home");
            Equal("1", consentPlan.Start.EnvironmentChanges[CrashReporting.ConsentVariable]);
            var target = lifecycle.State.Games.Single();
            Equal(GameLinkDelivery.Delivered, lifecycle.ConnectToRunningAsync(target, LaunchLink.Direct("localhost")).GetAwaiter().GetResult());
            Equal(3, host.Deliveries);
            Equal(TimeSpan.FromMilliseconds(400), clock.GetElapsedTime(0));
            Equal(true, lifecycle.QuitGameAsync(target).GetAwaiter().GetResult());
            Sequence(new[] { false, true }, host.QuitCalls);
            Equal(TimeSpan.FromMilliseconds(10_650), clock.GetElapsedTime(0));
            Equal(false, lifecycle.State.Running);
            Equal(false, lifecycle.State.LastExit!.Value.Unexpected);
            host.Observed = [new(target.Id, target.CreatedAt.AddSeconds(1))];
            Equal(GameStartState.Attached, lifecycle.StartAsync(request, true).GetAwaiter().GetResult());
            Equal(GameLinkDelivery.Failed, lifecycle.ConnectToRunningAsync(target, LaunchLink.Direct("localhost")).GetAwaiter().GetResult());
        });

        Check("Studio launch records game-written settings once and releases a failed reservation", () =>
        {
            var files = new FixtureProfileFiles();
            var clock = new AdvancingClock();
            var processes = new TestProcessSupervisor();
            var gameFiles = new MemoryPlatform("C:/data");
            foreach (string archive in GameArchives.Required)
                gameFiles.PutFile(archive);
            var store = new ConfigProfileStore(files, ConfigDialect.Stock);
            var main = store.Create("Main", "Main", new("seta sensitivity 2\r\nseta r_mode 800x600\r\n"u8, ConfigDialect.Stock));
            store.Select("Main", null);
            var studio = new ConfigStudioSession(files, ConfigDialect.Stock, () => false, clock);
            var host = new TestGameHost(processes, clock, studio);
            var lifecycle = new GameLifecycle(processes, host, gameFiles, clock);
            var request = new ClientLaunchRequest(GameEdition.Original, new GameSettings(), "C:/data", "C:/home");

            processes.FailStart = true;
            try
            {
                _ = lifecycle.StartAsync(request, true).GetAwaiter().GetResult();
                throw new InvalidOperationException("The fixture launch should have failed.");
            }
            catch (IOException error) when (error.Message == "Fixture spawn failed.") { }
            Equal(false, studio.IsReadOnly);
            Equal(0, store.History("Main").Count(entry => entry.Source == ConfigHistorySource.GameSession));

            processes.FailStart = false;
            Equal(GameStartState.Started, lifecycle.StartAsync(request, true).GetAwaiter().GetResult());
            Equal("Main", host.Request!.Studio!.ProfileId);
            Equal("800x600", host.Request.Loadout!["r_mode"]);
            Equal(true, studio.IsReadOnly);
            files.Files[ConfigPath] = [.. "seta sensitivity 5\r\n"u8];
            processes.Process!.Complete(0);
            lifecycle.ReconcileAsync().GetAwaiter().GetResult();
            lifecycle.ReconcileAsync().GetAwaiter().GetResult();

            var match = store.History("Main").Single(entry => entry.Source == ConfigHistorySource.GameSession);
            Sequence(main.Version.Config, match.Before.Config);
            Sequence(files.Files[ConfigPath], match.After.Config);
            Equal(false, studio.IsReadOnly);
            var restoredBefore = store.Restore("Main", match.Id, store.Read("Main").Version, ConfigSnapshotRevision.Before);
            Sequence(match.Before.Config, restoredBefore.After.Config);
            var restoredAfter = store.Restore("Main", match.Id, store.Read("Main").Version, ConfigSnapshotRevision.After);
            Sequence(match.After.Config, restoredAfter.After.Config);
        });

        Check("Hosting publishes private config, polls status, reaps a server and exports", () =>
        {
            var processes = new TestProcessSupervisor();
            var clock = new AdvancingClock();
            var files = new MemoryPlatform("C:/data");
            var hostingFiles = new TestHostingFiles();
            var udp = new StatusUdp();
            foreach (string archive in GameArchives.Required)
                files.PutFile(archive);
            var session = new HostingSession(processes, hostingFiles, files, udp, clock, port => port != 28961, "127.0.0.1");
            var server = HostedServer.New("test") with { Name = "Test", JoinPassword = "join secret", AdminPassword = "admin secret" };
            var binary = new GameBinary(server.Engine, EngineRole.Dedicated, "C:/engine.exe");
            Equal(HostedPhase.PortBusy, session.StartAsync(server with { Port = 28961 }, binary, "C:/data", "C:/hosting").GetAwaiter().GetResult().Phase);
            Equal(HostedPhase.Starting, session.StartAsync(server, binary, "C:/data", "C:/hosting").GetAwaiter().GetResult().Phase);
            var process = processes.Process!;
            var start = processes.Starts.Single();
            Equal("127.0.0.1", start.EngineArguments[start.EngineArguments.IndexOf("net_ip") + 1]);
            Equal("main/server.cfg", start.PrivateFiles.Single().RelativePath);
            Equal(true, start.PrivateFiles.Single().Data.AsSpan().StartsWith("sets g_competitive \"0\"\n"u8));
            Equal(2, start.LogSecrets.Length);
            session.PollAsync().GetAwaiter().GetResult();
            var states = Sync(session.SnapshotAsync());
            Equal(HostedPhase.Running, states[server.Id].Phase);
            Equal("mp_toujane", states[server.Id].Status!.Map);
            Equal(UpdateBlock.ServersRunning, ReleasePackage.Block(false, true));
            Equal(true, session.NextMapAsync(server.Id).GetAwaiter().GetResult());
            Equal(TimeSpan.FromMilliseconds(550), clock.GetElapsedTime(0));
            process.Complete(2);
            session.PollAsync().GetAwaiter().GetResult();
            Equal(HostedPhase.Failed, Sync(session.SnapshotAsync())[server.Id].Phase);
            Equal(false, session.NextMapAsync(server.Id).GetAwaiter().GetResult());
            Equal("C:/exports/test-server", Sync(session.ExportAsync(server, "C:/exports", null, null, false, false)));
            Equal(true, hostingFiles.Export!.ContainsKey("server.cfg"));
            _ = session.StartAsync(server, binary, "C:/data", "C:/hosting").GetAwaiter().GetResult();
            session.StopAllAsync().GetAwaiter().GetResult();
            Equal(HostedPhase.Stopped, Sync(session.SnapshotAsync())[server.Id].Phase);
        });
    }

    private sealed class TestProcessSupervisor : IProcessSupervisor
    {
        public List<EngineProcessStart> Starts { get; } = [];
        public TestProcess? Process { get; private set; }
        public bool FailStart { get; set; }
        public ValueTask<IEngineProcess> StartAsync(EngineProcessStart start, CancellationToken cancellationToken)
        {
            if (FailStart)
                throw new IOException("Fixture spawn failed.");
            Starts.Add(start);
            Process = new(Starts.Count);
            return ValueTask.FromResult<IEngineProcess>(Process);
        }
        public ValueTask<ImmutableArray<int>> FindEngineProcessesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<ImmutableArray<int>>([]);
    }

    private sealed class TestProcess(int id) : IEngineProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => id;
        public Task<int> Exited => completion.Task;
        public void Complete(int code) => completion.TrySetResult(code);
        public ValueTask StopAsync(CancellationToken cancellationToken) { Complete(0); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestGameHost(TestProcessSupervisor processes, TimeProvider clock, ConfigStudioSession? studio = null) : IGameSessionHost
    {
        public int Reservations { get; private set; }
        public int Deliveries { get; private set; }
        public List<bool> QuitCalls { get; } = [];
        public ImmutableArray<GameProcessIdentity> Observed { get; set; } = [];
        public ValueTask<ImmutableArray<GameProcessIdentity>> FindClientsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Observed);
        public GameProcessIdentity IdentityOf(IEngineProcess process) => new(process.Id, clock.GetUtcNow());
        public ValueTask<IGameLaunchReservation> ReserveAsync(ClientLaunchRequest request, CancellationToken cancellationToken)
        {
            Reservations++;
            Request = studio?.PrepareClientLaunch(request) ?? request;
            return ValueTask.FromResult<IGameLaunchReservation>(new TestReservation(WindowsLaunchPlan.Client(Request,
                new(request.Edition, EngineRole.Client, "C:/engine.exe"), "C:/home"), studio));
        }
        public ClientLaunchRequest? Request { get; private set; }
        public ValueTask ReconcileProfilesAsync(ImmutableArray<GameProcessIdentity> games, CancellationToken cancellationToken)
        {
            if (studio is not null && !studio.State.LaunchReserved)
                studio.ReconcileGames(games);
            return ValueTask.CompletedTask;
        }
        public ValueTask<GameLinkDelivery> DeliverAsync(GameProcessIdentity target, LaunchLink link, CancellationToken cancellationToken) =>
            ValueTask.FromResult(++Deliveries == 3 ? GameLinkDelivery.Delivered : GameLinkDelivery.Pending);
        public ValueTask RequestQuitAsync(GameProcessIdentity target, bool force, CancellationToken cancellationToken)
        {
            QuitCalls.Add(force);
            if (force) processes.Process!.Complete(0);
            return ValueTask.CompletedTask;
        }
        public (string? Log, string? Crash) FindExitFiles(DateTimeOffset since) => ("/fixture/log", "/fixture/crash");
        private sealed class TestReservation(WindowsLaunchPlan plan, ConfigStudioSession? studio) : IGameLaunchReservation
        {
            public WindowsLaunchPlan Plan => plan;
            public ValueTask CommitAsync(GameProcessIdentity game, bool intro, CancellationToken cancellationToken)
            {
                studio?.CommitLaunch(game);
                return ValueTask.CompletedTask;
            }
            public ValueTask DisposeAsync()
            {
                if (studio is not null && studio.State.LaunchReserved)
                    studio.CancelLaunch();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestHostingFiles : IHostingFiles
    {
        public ImmutableDictionary<string, string>? Export { get; private set; }
        public bool HasPackage(ZpamLine line) => false;
        public ValueTask<string?> PrepareAsync(string id, string home, ZpamLine? package, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
        public ValueTask<string> PublishExportAsync(string parent, string baseName, ImmutableDictionary<string, string> files,
            CancellationToken cancellationToken)
        {
            Export = files;
            return ValueTask.FromResult(parent + "/" + baseName);
        }
    }

    private sealed class StatusUdp : IUdpTransport
    {
        public Task<UdpReplies> ExchangeAsync(UdpExchange exchange, CancellationToken cancellationToken)
        {
            Equal("127.0.0.1", exchange.Endpoint.Host);
            string command = Encoding.Latin1.GetString(exchange.Request.AsSpan()[4..]);
            if (command != "getstatus")
                return Task.FromResult(new UdpReplies([]));
            return Task.FromResult(new UdpReplies([[.. WireParser.Marker, .. "statusResponse\n\\mapname\\mp_toujane\\g_gametype\\dm\n"u8]]));
        }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private long ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new VirtualTimer();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (timer.Disposed)
                    return;
                Interlocked.Add(ref ticks, dueTime.Ticks);
                callback(state);
            });
            return timer;
        }

        private sealed class VirtualTimer : ITimer
        {
            private bool disposed;
            public bool Disposed => Volatile.Read(ref disposed);
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Volatile.Write(ref disposed, true);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
