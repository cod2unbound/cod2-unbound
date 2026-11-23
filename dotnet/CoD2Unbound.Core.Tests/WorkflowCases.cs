using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void WorkflowCases()
    {
        Check("Update rechecks game and hosting under the install lease", () =>
        {
            using var fixture = Fixtures("settings");
            var row = fixture.RootElement.GetProperty("release")[1];
            var clock = new AdvancingClock();
            var releases = new TestReleases([.. Encoding.UTF8.GetBytes(row.GetProperty("json").GetString()!)]);
            string version = row.GetProperty("latestVersion").GetString()!;
            var updates = new LauncherUpdates(releases, clock, "0.0.1",
                (item, assets) => ReleasePackage.Offer(item, $"CoD2-Unbound-{item}-macos-arm64.zip", assets), TimeZoneInfo.Utc);
            var result = updates.CheckAsync(new(), automatic: true).GetAwaiter().GetResult();
            Equal(version, result.NotifyVersion);
            Equal(version, result.State.AvailableVersion);
            Equal(true, result.State.Offer is not null);
            Equal(clock.GetUtcNow(), result.Library.DispatchesCheckedAt);
            var installer = new TestInstaller(new(false, true));
            var blocked = updates.InstallAsync(installer).GetAwaiter().GetResult();
            Equal(UpdatePhase.Ready, blocked.Phase);
            Equal(UpdateBlock.ServersRunning, blocked.Hold);
            Equal(1, installer.Downloads);
            Equal(1, installer.Verified);
            Equal(0, installer.Disposed);
            Equal(0, installer.Installed);
            Equal(false, blocked.CanCancel);
            installer.Busy = new(false, false);
            updates.RefreshInstallBlock(installer.Busy);
            Equal<UpdateBlock?>(null, updates.State.Hold);
            Equal(0, installer.Installed);
            installer.Failure = new InvalidOperationException("simulated process start failure");
            var failedInstall = updates.InstallAsync(installer).GetAwaiter().GetResult();
            Equal(UpdatePhase.Idle, failedInstall.Phase);
            Equal(UpdateFailureStage.Install, failedInstall.FailedStage);
            Equal(false, failedInstall.CanCancel);
            Equal(1, installer.Downloads);
            Equal(1, installer.Disposed);
            installer.Failure = null;
            Equal(UpdatePhase.Installing, updates.InstallAsync(installer).GetAwaiter().GetResult().Phase);
            Equal(2, installer.Downloads);
            Equal(1, installer.Installed);
            updates.RestoreInstallResult(installed: true);
            Equal(UpdatePhase.Installed, updates.State.Phase);
            updates.RestoreInstallResult(installed: false);
            Equal(UpdateFailureStage.Install, updates.State.FailedStage);
            Equal(false, LauncherUpdates.AutoCheckDue(clock.GetUtcNow(), null, result.Library.DispatchesCheckedAt, false));
            Equal(TimeSpan.FromHours(1), LauncherUpdates.WaitForAutomaticCheck(clock.GetUtcNow(), null, result.Library.DispatchesCheckedAt));
        });

        Check("Settings debounce and reset preserve setup flags across saving", () =>
        {
            var clock = new AdvancingClock();
            var files = new MemoryPlatform("C:/home");
            var store = new GameSettingsStorage(files);
            var settings = new LauncherSettingsSession(new() { IntroFilmsPlayed = true, CrashReportsAsked = true }, store, clock);
            settings.Replace(settings.Working with { Sensitivity = 8 });
            settings.SaveAfterQuietAsync().GetAwaiter().GetResult();
            Equal(8.0, Sync(store.ReadAsync()).Sensitivity);
            Equal(TimeSpan.FromMilliseconds(250), clock.GetElapsedTime(0));
            settings.Reset(new());
            settings.FlushAsync().GetAwaiter().GetResult();
            Equal(true, Sync(store.ReadAsync()).IntroFilmsPlayed);
            Equal(true, Sync(store.ReadAsync()).CrashReportsAsked);
            Equal(new GameSettings().Sensitivity, Sync(store.ReadAsync()).Sensitivity);
        });

        Check("Signed Discover setup can be saved and reviewed with its published provenance", () =>
        {
            using var fixture = Fixtures("directory");
            var root = fixture.RootElement;
            var index = new Uri(root.String("indexURL")!);
            var signature = new Uri(root.String("signatureURL")!);
            var trust = new ConfigDirectoryConfiguration(index, signature, [.. root.Bytes("publicKey")]);
            var row = root.GetProperty("cases").EnumerateArray().Single(item => item.String("id") == "signed");
            var fetcher = new DirectoryFetcher();
            fetcher.Responses[index] = new([.. row.Bytes("index")], index, null);
            fetcher.Responses[signature] = new([.. row.Bytes("signature")], signature, null);
            var discover = new ConfigDiscover(new(trust, new DirectoryStorage(), fetcher), ConfigCatalog.LoadBundled());
            discover.LoadAsync().GetAwaiter().GetResult();
            Equal(ConfigDiscoverPhase.Loaded, discover.State.Phase);
            var entry = discover.Entries.Single();
            Equal(entry.Id, ConfigDiscover.Heroes(discover.Entries).Single().Id);
            Equal(entry.Id, ConfigDiscover.Setups(discover.Entries, ConfigSetupFilter.Modern, "wren", CultureInfo.InvariantCulture).Single().Id);
            Equal("Saved for later.", discover.SetSavedAsync(entry.Id, true).GetAwaiter().GetResult());
            Equal(entry.Id, discover.State.Saved.Single().Id);
            fetcher.Responses[entry.ConfigUrl] = new([.. root.Bytes("profile")], entry.ConfigUrl, "text/plain");
            var review = discover.ReviewAsync(entry.Id, ConfigDialect.Stock, null).GetAwaiter().GetResult();
            Equal(ConfigImportPlatform.Windows, review.DestinationPlatform);
            Equal(entry.SourceUrl.AbsoluteUri, review.Origin.Source);
            Equal(entry.Credit, review.Origin.Credit);
            Equal(24.7, Math.Round(ConfigDiscover.SetupTurnDistance(review, 800)!.Value, 1));
            Equal<double?>(null, ConfigDiscover.SetupTurnDistance(review, 0));
            fetcher.Responses.Clear();
            discover.LoadAsync(retry: true).GetAwaiter().GetResult();
            Equal(ConfigDiscoverPhase.Loaded, discover.State.Phase);
            Equal(true, discover.State.Problem is not null);
            Equal("Removed from Saved.", discover.SetSavedAsync(entry.Id, false).GetAwaiter().GetResult());
        });

        Check("Setup mouse distance follows the last reviewed sensitivity and the setup yaw", () =>
        {
            var catalog = new ConfigImportCatalogAdapter(ConfigCatalog.LoadBundled());
            var bundle = Sync(new ConfigImportLoader(catalog).LoadAsync(new ConfigImportInput.Paste(
                ImportBytes("seta sensitivity 2.4\nseta sensitivity 2.1\nseta m_yaw 0.011\n")), ConfigDialect.Stock));
            var review = new ConfigImportSession(bundle, ConfigDialect.Stock, catalog, "Mouse setup");
            Equal(49.5, Math.Round(ConfigDiscover.SetupTurnDistance(review, 800)!.Value, 1));
            Equal(24.7, Math.Round(ConfigDiscover.SetupTurnDistance(review, 1600)!.Value, 1));
        });

        Check("zPAM download cancellation returns the same verified offer and retries", () =>
        {
            using var archive = ZpamZip([("main/server.cfg", "text", 0), ("main/zpam336.iwd", "archive", 0)]);
            var source = new TestZpamSource(archive.ToArray(), ZpamSample(archive));
            var storage = new PackageStorage();
            var download = new ZpamDownload(source, storage);
            download.LookUpAsync(ZpamLine.Three).GetAwaiter().GetResult();
            Equal(ZpamDownloadPhase.Missing, download.State(ZpamLine.Three).Phase);
            var release = download.State(ZpamLine.Three).Release;
            using var cancellation = new CancellationTokenSource();
            source.FirstProgress = cancellation.Cancel;
            download.GetAsync(ZpamLine.Three, cancellation.Token).GetAwaiter().GetResult();
            Equal(release, download.State(ZpamLine.Three).Release);
            Equal(ZpamDownloadPhase.Missing, download.State(ZpamLine.Three).Phase);
            Equal(false, storage.Published);
            source.FirstProgress = null;
            download.GetAsync(ZpamLine.Three).GetAwaiter().GetResult();
            Equal(ZpamDownloadPhase.Ready, download.State(ZpamLine.Three).Phase);
            Equal(true, storage.Published);
        });

        Check("Browser batches cached addresses offline and scopes edition selection", () =>
        {
            var clock = new AdvancingClock();
            var udp = new BrowserUdp();
            var query = new ServerQueryClient(udp, clock, () => Guid.Empty);
            var browser = new ServerBrowser(query, clock);
            var observed = new List<ServerRefreshState>();
            var library = new LauncherLibrary { Favorites = ImmutableHashSet.Create("localhost:28960") };
            var refreshed = browser.RefreshAsync(library, [], new DirectProgress<ServerRefreshState>(observed.Add)).GetAwaiter().GetResult();
            Equal(6, udp.Count);
            Equal(1, refreshed.Cache.Length);
            Equal(true, observed[^1].MastersDidNotAnswer);
            Equal(1, observed[^1].Asked);
            Equal(TimeSpan.FromMilliseconds(40), clock.GetElapsedTime(0));
            var link = LaunchLink.Direct("LOCALHOST");
            var admission = new ServerAdmission();
            var installed = ImmutableHashSet.Create(GameEdition.Original, GameEdition.Cod2x);
            Equal(ServerAdmissionAction.LookUp, admission.Evaluate(link, null, GameEdition.Original, installed, false).Action);
            Equal(ServerAdmissionAction.CheckInProgress, admission.Evaluate(link, null, GameEdition.Original, installed, false).Action);
            admission.LookUpFinished(link.Address, false);
            var target = refreshed.Cache[0];
            Equal(ServerAdmissionAction.ConfirmVersion, admission.Evaluate(link, target, GameEdition.Original, installed, false).Action);
            admission.AcceptVersion(link.Address, GameEdition.Cod2x);
            Equal(GameEdition.Cod2x, admission.Evaluate(link, target, GameEdition.Original, installed, false).Edition);
            admission.EndGame();
            Equal(ServerAdmissionAction.MissingCod2x,
                admission.Evaluate(link, target, GameEdition.Original, ImmutableHashSet.Create(GameEdition.Original), false).Action);
        });

        Check("Session log is bounded across chunk boundaries and retains only the allowed sessions", () =>
        {
            var output = new GameSessionLog(["synthetic-key"], "/fake");
            Equal(0, output.Append("syn"u8).Length);
            Sequence("[Private connection or key details omitted]\n"u8.ToArray(), output.Append("thetic-key\n"u8));
            Equal(0, output.Append(Enumerable.Repeat((byte)'x', 16_385).ToArray()).Length);
            Sequence("[Long output line omitted]\n"u8.ToArray(), output.Append("\n"u8));
            output.Protect("after-launch");
            Sequence("[Private connection or key details omitted]\n"u8.ToArray(), output.Append("reply after-launch\n"u8));
            Sequence("final line\n"u8.ToArray(), output.Append("final line"u8, complete: true));
            Equal(0, output.Append("late"u8).Length);
            var files = Enumerable.Range(0, 12).Select(index => new MediaFile($"game-{index:D2}.log", null, 4L << 20));
            Equal(7, GameSessionLog.Prune("game", "game-11.log", files).Length);
        });
    }

    private sealed class TestReleases(ImmutableArray<byte> release) : ILauncherReleaseSource
    {
        public ValueTask<ReleaseFeed> ReleasesAsync(TimeZoneInfo timeZone, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ReleaseNotes.Parse(release.AsSpan(), timeZone: timeZone));
        public ValueTask<ImmutableArray<byte>?> NewsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<ImmutableArray<byte>?>(null);
    }

    private sealed class TestInstaller(LauncherActivity activity) : IWindowsLauncherUpdate, IStagedLauncherUpdate
    {
        public LauncherActivity Busy { get; set; } = activity;
        public int Downloads { get; private set; }
        public int Verified { get; private set; }
        public int Installed { get; private set; }
        public int Disposed { get; private set; }
        public Exception? Failure { get; set; }
        public void CheckInstallLocation() { }
        public ValueTask<IStagedLauncherUpdate> DownloadAsync(ReleaseOffer offer, Action<long> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            return ValueTask.FromResult<IStagedLauncherUpdate>(this);
        }
        public ValueTask<IUpdateInstallLease> AcquireInstallLeaseAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IUpdateInstallLease>(new TestLease(Busy));
        public ValueTask VerifyAsync(CancellationToken cancellationToken) { Verified++; return ValueTask.CompletedTask; }
        public ValueTask InstallAsync(CancellationToken cancellationToken)
        {
            if (Failure is { } error)
                throw error;
            Installed++;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }

        private sealed record TestLease(LauncherActivity Activity) : IUpdateInstallLease
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestZpamSource(byte[] bytes, ZpamRelease sample) : IZpamDownloadSource
    {
        public Action? FirstProgress { get; set; }
        public ValueTask<ImmutableArray<byte>> ListAsync(CancellationToken cancellationToken)
        {
            string json = $$"""
                [{"assets":[{"name":"{{sample.Asset}}","browser_download_url":"https://github.com/eyza-cod2/zpam3/releases/download/v3.36/{{sample.Asset}}",
                "size":{{sample.Bytes}},"digest":"sha256:{{sample.Sha256}}"}]}]
                """;
            return ValueTask.FromResult<ImmutableArray<byte>>([.. Encoding.UTF8.GetBytes(json)]);
        }
        public string? InstalledVersion(ZpamLine line) => null;
        public ValueTask<Stream> DownloadAsync(ZpamRelease release, Action<long> progress, CancellationToken cancellationToken)
        {
            progress(bytes.Length / 2);
            FirstProgress?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            progress(bytes.Length);
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes));
        }
    }

    private sealed class BrowserUdp : IUdpTransport
    {
        public int Count { get; private set; }
        public Task<UdpReplies> ExchangeAsync(UdpExchange exchange, CancellationToken cancellationToken)
        {
            Count++;
            if (!exchange.Endpoint.Host.Equals("localhost", StringComparison.Ordinal))
                return Task.FromResult(new UdpReplies([]));
            string command = Encoding.UTF8.GetString(exchange.Request.AsSpan()[4..]);
            return Task.FromResult(command.StartsWith("getstatus", StringComparison.Ordinal)
                ? new UdpReplies([[.. WireParser.Marker, .. "statusResponse\n\\shortversion\\1.4.6.8\\mapname\\mp_toujane\n"u8]]) : new UdpReplies([]));
        }
    }
    private sealed class DirectProgress<T>(Action<T> publish) : IProgress<T> { public void Report(T value) => publish(value); }
}
