using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void UpdateTrustCases()
    {
        Check("Public Windows release source ignores environment and file fixtures", () =>
        {
            const string origin = "COD2_UNBOUND_TEST_UPDATE_ORIGIN";
            const string fixture = "COD2_UNBOUND_TEST_RELEASES";
            string? savedOrigin = Environment.GetEnvironmentVariable(origin);
            string? savedFixture = Environment.GetEnvironmentVariable(fixture);
            try
            {
                Environment.SetEnvironmentVariable(origin, "http://127.0.0.1:8123/");
                Environment.SetEnvironmentVariable(fixture, Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings.json"));
                Equal<Uri?>(null, WindowsReleaseSource.LocalOrigin);
                Equal<string?>(null, WindowsReleaseSource.Fixture);
                var source = new WindowsReleaseSource(testMode: true);
                try { source.ReleasesAsync(TimeZoneInfo.Utc, default).GetAwaiter().GetResult(); }
                catch (IOException error)
                {
                    Equal("No local release fixture was supplied.", error.Message);
                    return;
                }
                throw new InvalidOperationException("A public source read the local release fixture.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(origin, savedOrigin);
                Environment.SetEnvironmentVariable(fixture, savedFixture);
            }
        });

        Check("Loopback fixture feeds stay on their origin without website fallback or public news", () =>
        {
            var origin = new Uri("http://127.0.0.1:8123/");
            string feed = origin + "releases";
            string root = origin + "releases/download/v0.5.1/";
            using var http = new UpdateHttp(Reply(HttpStatusCode.OK, UpdateRelease(root, GitHubNotes)),
                Reply(HttpStatusCode.OK, SetupSums), Reply(HttpStatusCode.NotFound));
            var result = RunCheck(http.Loopback(origin), PriorNews(), origin);
            Equal("Cached", result.Library.NewsNext?.Title);
            Equal(false, result.State.Offer?.ListedByGitHub);
            Equal(root + SetupName, result.State.Offer?.Archive.Url.AbsoluteUri);
            RefusedUpdate(() => DownloadAndCheck(http.Loopback(origin), result.State.Offer!));
            http.AssertRequests(feed, root + SumsName, root + SetupName);

            using var down = new UpdateHttp(Reply(HttpStatusCode.InternalServerError));
            Equal(UpdateFailureStage.Check, RunCheck(down.Loopback(origin), loopbackOrigin: origin).State.FailedStage);
            down.AssertRequests(feed);

            using var redirect = new UpdateHttp(Redirect(MirrorReleases));
            Equal(UpdateFailureStage.Check, RunCheck(redirect.Loopback(origin), loopbackOrigin: origin).State.FailedStage);
            redirect.AssertRequests(feed);
        });

        Check("The production HTTP client returns a loopback feed redirect without following it", () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var listener = TcpListener.Create(0);
            listener.Start();
            var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            var server = ReplyToLoopbackFeedAsync(listener, deadline.Token);
            using var client = ReleaseDownload.CreateClient(loopbackOnly: true);
            var download = new ReleaseDownload(client, origin);
            RefusedUpdate(() => ReleaseNotes.FetchAsync(download, TimeZoneInfo.Utc, deadline.Token).GetAwaiter().GetResult());
            Equal("GET /releases HTTP/1.1", server.GetAwaiter().GetResult());
            Equal(false, listener.Pending());
        });

        Check("Invalid fixture origins cannot reach the website or a different loopback host", () =>
        {
            foreach (string address in new[] { "http://invalid.invalid/", "https://cod2unbound.com/", "http://localhost:8123/",
                "http://127.0.0.2/", "http://user@127.0.0.1/", "http://127.0.0.1/test", "http://127.0.0.1/?test" })
            {
                var origin = new Uri(address);
                using var http = new UpdateHttp();
                Equal(false, WindowsReleaseAssets.IsLoopbackOrigin(origin));
                Equal(UpdateFailureStage.Check, RunCheck(http.Loopback(origin), loopbackOrigin: origin).State.FailedStage);
                http.AssertRequests();
            }
        });

        Check("Website Setup must match the accepted root and publisher EKU before handoff", () =>
        {
            const string codeSigning = "1.3.6.1.5.5.7.3.3";
            const string publisher = "1.3.6.1.4.1.32473.55.1";
            const string otherPublisher = "1.3.6.1.4.1.32473.55.2";
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var root = Certificate("Expected root", key);
            using var wrongRoot = Certificate("Different root", key);
            using var accepted = Certificate("Expected Setup publisher", key, codeSigning, publisher);
            using var different = Certificate("Different Setup publisher", key, codeSigning, otherPublisher);
            using var noCodeSigning = Certificate("Missing Code Signing", key, publisher);
            using var noProfile = Certificate("Missing profile", key, codeSigning);
            SetupPublisher.PublisherRule[] rules = [new(root.GetCertHashString(HashAlgorithmName.SHA256), publisher)];

            foreach (var (signer, anchor, valid) in new[] { (accepted, root, true), (different, root, false),
                (accepted, wrongRoot, false), (noCodeSigning, root, false), (noProfile, root, false) })
            {
                using var http = new UpdateHttp(Reply(HttpStatusCode.InternalServerError),
                    Reply(HttpStatusCode.OK, UpdateRelease(MirrorRoot, MirrorNotes)), Reply(HttpStatusCode.NotFound),
                    Reply(HttpStatusCode.OK, SetupSums), Reply(HttpStatusCode.OK, SetupBytes));
                var updates = CreateUpdater(http.Download);
                Equal(UpdatePhase.Offered, updates.CheckAsync(new()).GetAwaiter().GetResult().State.Phase);
                var installer = new PublisherFixtureInstaller(http.Download, () => SetupPublisher.VerifyIdentity(signer, anchor, rules));
                var state = updates.InstallAsync(installer).GetAwaiter().GetResult();
                Equal(valid, installer.HandedOff);
                Equal(valid ? UpdatePhase.Installing : UpdatePhase.Idle, state.Phase);
                Equal(valid ? UpdateIssue.None : UpdateIssue.Refused, state.Issue);
                Equal<UpdateFailureStage?>(valid ? null : UpdateFailureStage.Install, state.FailedStage);
                http.AssertRequests(GitHubReleases, MirrorReleases, GitHubNews, MirrorRoot + SumsName, MirrorRoot + SetupName);
            }
        });
    }

    private static async Task<string?> ReplyToLoopbackFeedAsync(TcpListener listener, CancellationToken token)
    {
        using var remote = await listener.AcceptTcpClientAsync(token);
        using var stream = remote.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        string? request = await reader.ReadLineAsync(token);
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 302 Found\r\nLocation: /elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
        return request;
    }

    private static X509Certificate2 Certificate(string name, ECDsa key, params string[] usages)
    {
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        if (usages.Length != 0)
        {
            var oids = new OidCollection();
            foreach (string usage in usages)
                oids.Add(new Oid(usage));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
        }
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    }

    // WinVerifyTrust supplies the accepted leaf and root on Windows. These fixtures exercise the
    // production identity rule on that pair and the portable workflow's verification gate.
    private sealed class PublisherFixtureInstaller(ReleaseDownload download, Action verifyPublisher) :
        IWindowsLauncherUpdate, IStagedLauncherUpdate, IUpdateInstallLease
    {
        internal bool HandedOff { get; private set; }
        public LauncherActivity Activity => new(false, false);
        public void CheckInstallLocation() { }
        public ValueTask<IUpdateInstallLease> AcquireInstallLeaseAsync(CancellationToken token) =>
            ValueTask.FromResult<IUpdateInstallLease>(this);

        public ValueTask<IStagedLauncherUpdate> DownloadAsync(ReleaseOffer offer, Action<long> progress, CancellationToken token)
        {
            if (!DownloadAndCheck(download, offer, progress))
                throw new LauncherUpdateException("The Setup checksum was wrong.");
            return ValueTask.FromResult<IStagedLauncherUpdate>(this);
        }

        public ValueTask VerifyAsync(CancellationToken token)
        {
            verifyPublisher();
            return ValueTask.CompletedTask;
        }

        public ValueTask InstallAsync(CancellationToken token)
        {
            HandedOff = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
