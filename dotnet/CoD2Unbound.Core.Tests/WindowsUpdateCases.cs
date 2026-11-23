using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private const string UpdateVersion = "0.5.1";
    private const string SetupName = "CoD2-Unbound-0.5.1-windows-x64-setup.exe";
    private const string SumsName = "SHA256SUMS";
    private const string GitHubRoot = "https://github.com/cod2unbound/cod2-unbound/releases/download/v0.5.1/";
    private const string MirrorRoot = "https://cod2unbound.com/releases/v0.5.1/";
    private const string GitHubReleases = "https://api.github.com/repos/cod2unbound/cod2-unbound/releases?per_page=10";
    private const string MirrorReleases = "https://cod2unbound.com/update/releases.json";
    private const string GitHubNews = "https://raw.githubusercontent.com/cod2unbound/cod2-unbound/main/docs/news.json";
    private const string MirrorNews = "https://cod2unbound.com/update/news.json";
    private const string GitHubNotes = "https://github.com/cod2unbound/cod2-unbound/releases/tag/v0.5.1";
    private const string MirrorNotes = "https://cod2unbound.com/whats-new/0.5.1";
    private const string GitHubNext = """{"next":{"title":"From GitHub"}}""";
    private const string MirrorNext = """{"next":{"title":"From the website"}}""";
    private const string SetupBytes = "exe bytes";
    private static readonly string SetupHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SetupBytes))).ToLowerInvariant();
    private static readonly string SetupSums = $"{SetupHash}  {SetupName}\n";

    private static void WindowsUpdateCases()
    {
        UpdateReleaseCases();
        UpdateNewsCases();
        UpdateDownloadCases();
        UpdateTrustCases();
    }

    private static void UpdateReleaseCases()
    {
        Check("Windows accepts exact versioned Setup and checksum addresses from both lists", () =>
        {
            foreach (var (root, link) in new[] { (GitHubRoot, GitHubNotes), (MirrorRoot, MirrorNotes) })
            {
                var feed = ReleaseNotes.Parse(Encoding.UTF8.GetBytes(UpdateRelease(root, link)));
                var offer = WindowsReleaseAssets.Offer(UpdateVersion, feed.LatestAssets)!;
                Equal(root + SetupName, offer.Archive.Url.AbsoluteUri);
                Equal(root + SumsName, offer.Checksums.AbsoluteUri);
                Equal(SetupSums.Length, (int)offer.ChecksumsSize);
                Equal(link, feed.Dispatches.Single().Link);
                Equal(link, feed.LatestUrl?.AbsoluteUri);
            }
        });

        Check("Windows offer requires the exact versioned installer and checksum paths", () =>
        {
            var offer = UpdateOffer();
            var setup = offer.Archive;
            var sums = new ReleaseAsset(SumsName, offer.ChecksumsSize, offer.Checksums);
            foreach (ReleaseAsset[] bad in new ReleaseAsset[][] {
                [setup], [sums], [setup, setup, sums], [setup, sums, sums],
                [setup with { Size = long.MaxValue }, sums], [setup, sums with { Size = 65537 }],
                [setup with { Size = 0 }, sums], [setup, sums with { Size = 0 }] })
                Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(UpdateVersion, bad));
            foreach (string version in new[] { "0.05.1", "0.5", "0.5.0" })
                Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(version, [setup, sums]));
            Equal(false, WindowsReleaseAssets.IsNewer(UpdateVersion, UpdateVersion));
            Equal(false, WindowsReleaseAssets.IsNewer("0.5.0", UpdateVersion));
            Equal(false, WindowsReleaseAssets.IsNewer("0.5.2-preview", UpdateVersion));
            Equal(true, WindowsReleaseAssets.IsNewer("0.5.10", UpdateVersion));
        });

        foreach (var root in new[] { GitHubRoot, MirrorRoot })
            Check($"Windows refuses unsafe Setup and checksum URLs under {root}", () =>
            {
                var offer = UpdateOffer(root);
                var setup = offer.Archive;
                var sums = new ReleaseAsset(SumsName, offer.ChecksumsSize, offer.Checksums);
                foreach (string incorrect in new[] { "https://elsewhere.example/", root.Replace("https:", "http:"),
                    root.Replace(".com", ".com.example"), root.Replace("https://", "https://user@"),
                    root.Replace("https://", "https://@"), root.Replace("/releases/", "/other/"),
                    root.Replace("v0.5.1", "v0.5.0"), root + "folder/../", root + "folder/%2e%2e/",
                    root + "folder/.%2e/", root + "%2e%2e%2f", root + "nested/" })
                {
                    Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(UpdateVersion, [setup with { Url = new(incorrect + SetupName) }, sums]));
                    Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(UpdateVersion, [setup, sums with { Url = new(incorrect + SumsName) }]));
                }
                foreach (string suffix in new[] { "?", "?next=no", "#", "#fragment", ".sig" })
                {
                    Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(UpdateVersion, [setup with { Url = new(root + SetupName + suffix) }, sums]));
                    Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(UpdateVersion, [setup, sums with { Url = new(root + SumsName + suffix) }]));
                }
            });

        Check("Release notes links accept the two exact hosts and refuse unsafe addresses", () =>
        {
            foreach (string link in new[] { "https://cod2unbound.com.example/whats-new/0.5.1",
                "http://cod2unbound.com/whats-new/0.5.1", "https://user@cod2unbound.com/whats-new/0.5.1",
                "https://example.com/whats-new/0.5.1", MirrorNotes + "?query", GitHubNotes + "#fragment",
                "https://cod2unbound.com/whats-new/../0.5.1", "https://github.com/whats-new/%2e%2e/0.5.1" })
            {
                var feed = ReleaseNotes.Parse(Encoding.UTF8.GetBytes(UpdateRelease(MirrorRoot, link)));
                Equal<string?>(null, feed.Dispatches.Single().Link);
                Equal<Uri?>(null, feed.LatestUrl);
            }
        });

        Check("Portable release offers keep the historical GitHub roots and accept the mirror", () =>
        {
            const string mac = "CoD2-Unbound-0.5.1-macos-arm64.zip";
            foreach (string root in new[] { GitHubRoot, MirrorRoot,
                "https://github.com/STiXzoOR/cod2-silicon/releases/download/v0.5.1/",
                "https://github.com/cod2unbound/cod2-silicon/releases/download/v0.5.1/" })
            {
                var archive = new ReleaseAsset(mac, 1, new(root + mac));
                var sums = new ReleaseAsset(SumsName, 1, new(root + SumsName));
                Equal(archive, ReleasePackage.Offer(UpdateVersion, mac, [archive, sums])?.Archive);
                foreach (string bad in new[] { root + "nested/../" + mac, root + "nested/%2E%2e/" + mac,
                    root + mac + "?query", root + mac + "#fragment" })
                    Equal<ReleaseOffer?>(null, ReleasePackage.Offer(UpdateVersion, mac, [archive with { Url = new(bad) }, sums]));
            }
        });

        Check("Release check uses GitHub first and links the stable release even when a preview is listed first", () =>
        {
            string stable = UpdateRelease(GitHubRoot, GitHubNotes);
            string withPreview = """[{"tag_name":"v0.5.2-rc","prerelease":true,"html_url":"https://github.com/preview"},""" + stable[1..];
            using var http = new UpdateHttp(Reply(HttpStatusCode.OK, withPreview), Reply(HttpStatusCode.NotFound));
            var result = RunCheck(http.Download);
            Equal(UpdateVersion, result.State.Offer?.Version);
            Equal(true, result.State.Offer?.ListedByGitHub);
            Equal(GitHubNotes, result.State.ReleaseUrl?.AbsoluteUri);
            http.AssertRequests(GitHubReleases, GitHubNews);
            Sequence(["application/vnd.github+json", ""], http.Accepts);
        });

        foreach (var (reason, response) in FeedErrors().Concat(new (string, Func<HttpResponseMessage>)[] {
            ("404", Reply(HttpStatusCode.NotFound)), ("garbage", Reply(HttpStatusCode.OK, "garbage")),
            ("invalid asset size", Reply(HttpStatusCode.OK, """[{"assets":[{"size":"wrong"}]}]""")),
            ("oversized body", Reply(HttpStatusCode.OK, new string(' ', 1_000_001))) }))
            Check($"Release check falls back once after {reason}", () =>
            {
                using var http = new UpdateHttp(response, Reply(HttpStatusCode.OK, UpdateRelease(MirrorRoot, MirrorNotes)), Reply(HttpStatusCode.NotFound));
                var result = RunCheck(http.Download);
                Equal(MirrorRoot + SetupName, result.State.Offer?.Archive.Url.AbsoluteUri);
                Equal(MirrorRoot + SumsName, result.State.Offer?.Checksums.AbsoluteUri);
                Equal(false, result.State.Offer?.ListedByGitHub);
                Equal(MirrorNotes, result.State.ReleaseUrl?.AbsoluteUri);
                http.AssertRequests(GitHubReleases, MirrorReleases, GitHubNews);
                Sequence(["application/vnd.github+json", "", ""], http.Accepts);
            });

        Check("Two unavailable release lists leave the check retryable", () =>
        {
            using var http = new UpdateHttp(Reply(HttpStatusCode.ServiceUnavailable), Reply(HttpStatusCode.ServiceUnavailable),
                Reply(HttpStatusCode.OK, UpdateRelease(GitHubRoot, GitHubNotes)), Reply(HttpStatusCode.NotFound));
            var updates = CreateUpdater(http.Download);
            var result = updates.CheckAsync(new()).GetAwaiter().GetResult();
            Equal(UpdateFailureStage.Check, result.State.FailedStage);
            Equal(UpdateIssue.CheckUnavailable, result.State.Issue);
            Equal<ReleaseOffer?>(null, result.State.Offer);
            Equal<DateTimeOffset?>(null, result.State.CheckedAt);

            var retry = updates.CheckAsync(result.Library).GetAwaiter().GetResult();
            Equal(UpdateIssue.None, retry.State.Issue);
            Equal(UpdatePhase.Offered, retry.State.Phase);
            http.AssertRequests(GitHubReleases, MirrorReleases, GitHubReleases, GitHubNews);
        });

        Check("Release check refuses bad asset metadata from a usable GitHub list", () =>
        {
            using var http = new UpdateHttp(Reply(HttpStatusCode.OK, UpdateRelease(GitHubRoot + "bad/", GitHubNotes)),
                Reply(HttpStatusCode.NotFound));
            var result = RunCheck(http.Download);
            Equal(UpdateIssue.Refused, result.State.Issue);
            Equal<ReleaseOffer?>(null, result.State.Offer);
            Equal(GitHubNotes, result.State.ReleaseUrl?.AbsoluteUri);
            http.AssertRequests(GitHubReleases, GitHubNews);
        });

        Check("Release check stops after user cancellation", () =>
        {
            using var cancel = new CancellationTokenSource();
            using var http = new UpdateHttp(() =>
            {
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            });
            var result = RunCheck(http.Download, token: cancel.Token);
            Equal(false, result.State.WasChecked);
            http.AssertRequests(GitHubReleases);
        });
    }

    private static void UpdateNewsCases()
    {
        Check("Loopback update checks load news without using public hosts", () =>
        {
            var origin = new Uri("http://127.0.0.1:18081/");
            using var http = new UpdateHttp(Reply(HttpStatusCode.OK, "[]"), Reply(HttpStatusCode.OK,
                """{"items":[{"title":"Local news","date":"2026-01-01"}],"next":{"title":"Local next"}}"""),
                Reply(HttpStatusCode.OK, "[]"), Reply(HttpStatusCode.NotFound));
            var result = RunCheck(http.Loopback(origin), PriorNews(), origin);
            Equal("Local news", result.Library.News.Single().Title);
            Equal("Local next", result.Library.NewsNext?.Title);
            var fallback = RunCheck(http.Loopback(origin), result.Library, origin);
            Equal("Local next", fallback.Library.NewsNext?.Title);
            http.AssertRequests("http://127.0.0.1:18081/releases", "http://127.0.0.1:18081/news.json",
                "http://127.0.0.1:18081/releases", "http://127.0.0.1:18081/news.json");
        });

        foreach (var (reason, response) in FeedErrors())
            Check($"News uses the website after {reason}", () =>
            {
                using var http = new UpdateHttp(Reply(HttpStatusCode.OK, "[]"), response, Reply(HttpStatusCode.OK, MirrorNext));
                var result = RunCheck(http.Download, PriorNews());
                Equal("From the website", result.Library.NewsNext?.Title);
                http.AssertRequests(GitHubReleases, GitHubNews, MirrorNews);
            });

        foreach (var (reason, primary, title) in new (string, Func<HttpResponseMessage>, string?)[] {
            ("success", Reply(HttpStatusCode.OK, GitHubNext), "From GitHub"),
            ("404", Reply(HttpStatusCode.NotFound), null),
            ("invalid JSON", Reply(HttpStatusCode.OK, "garbage"), "Cached"),
            ("wrong JSON shape", Reply(HttpStatusCode.OK, """{"items":{}}"""), "Cached"),
            ("empty 200", Reply(HttpStatusCode.OK), "Cached"),
            ("oversized 200", Reply(HttpStatusCode.OK, new string(' ', 65_537)), "Cached") })
            Check($"Primary news {reason} ends the check", () =>
            {
                using var http = new UpdateHttp(Reply(HttpStatusCode.OK, "[]"), primary);
                Equal(title, RunCheck(http.Download, PriorNews()).Library.NewsNext?.Title);
                http.AssertRequests(GitHubReleases, GitHubNews);
            });

        foreach (var (reason, mirror) in new[] { ("404", Reply(HttpStatusCode.NotFound)),
            ("500", Reply(HttpStatusCode.InternalServerError)), ("invalid JSON", Reply(HttpStatusCode.OK, "garbage")) })
            Check($"Website news {reason} preserves cached news", () =>
            {
                using var http = new UpdateHttp(Reply(HttpStatusCode.OK, "[]"), Reply(HttpStatusCode.InternalServerError), mirror);
                Equal("Cached", RunCheck(http.Download, PriorNews()).Library.NewsNext?.Title);
                http.AssertRequests(GitHubReleases, GitHubNews, MirrorNews);
            });
    }

    private static void UpdateDownloadCases()
    {
        var offer = UpdateOffer();
        var website = UpdateOffer(MirrorRoot) with { ListedByGitHub = false };
        string gitSums = GitHubRoot + SumsName, gitSetup = GitHubRoot + SetupName;
        string webSums = MirrorRoot + SumsName, webSetup = MirrorRoot + SetupName;
        var sum = Reply(HttpStatusCode.OK, SetupSums);
        var setup = Reply(HttpStatusCode.OK, SetupBytes);

        Check("Unavailable GitHub checksum and Setup each retry once at the website", () =>
        {
            using var http = new UpdateHttp(Reply(HttpStatusCode.NotFound), sum, Reply(HttpStatusCode.InternalServerError), setup);
            Equal(true, DownloadAndCheck(http.Download, offer));
            http.AssertRequests(gitSums, webSums, gitSetup, webSetup);
        });

        Check("A partial GitHub transfer is cleared before retrying", () =>
        {
            using var http = new UpdateHttp(sum, () =>
            {
                var content = new StreamContent(new InterruptedAssetStream());
                content.Headers.ContentLength = SetupBytes.Length;
                return new(HttpStatusCode.OK) { Content = content };
            }, setup);
            var progress = new List<long>();
            Equal(true, DownloadAndCheck(http.Download, offer, progress.Add));
            Sequence([1L, 0L, SetupBytes.Length], progress);
            http.AssertRequests(gitSums, gitSetup, webSetup);
        });

        Check("A Setup of the right size with the wrong bytes fails SHA-256 without fallback", () =>
        {
            using var http = new UpdateHttp(sum, Reply(HttpStatusCode.OK, "bad bytes"));
            Equal(false, DownloadAndCheck(http.Download, offer));
            http.AssertRequests(gitSums, gitSetup);
        });

        Check("A checksum file of the right size without the correct line fails without fallback", () =>
        {
            using var http = new UpdateHttp(Reply(HttpStatusCode.OK, new string('x', SetupSums.Length)));
            RefusedUpdate(() => DownloadAndCheck(http.Download, offer));
            http.AssertRequests(gitSums);
        });

        foreach (string wrongSize in new[] { SetupBytes[..^1], SetupBytes + "!" })
            Check($"A Setup with {wrongSize.Length} bytes fails the size check without fallback", () =>
            {
                using var http = new UpdateHttp(sum, Reply(HttpStatusCode.OK, wrongSize));
                RefusedUpdate(() => DownloadAndCheck(http.Download, offer));
                http.AssertRequests(gitSums, gitSetup);
            });

        Check("A streamed Setup that exceeds its listed size is refused without fallback", () =>
        {
            using var http = new UpdateHttp(() => new(HttpStatusCode.OK)
            {
                Content = new UnmeasuredAsset(Encoding.UTF8.GetBytes(SetupBytes + "!"))
            });
            using var output = new MemoryStream();
            RefusedUpdate(() => http.Download.CopySetupAsync(offer, output, null, default).GetAwaiter().GetResult());
            Equal(true, output.Length <= offer.Archive.Size);
            http.AssertRequests(gitSetup);
        });

        Check("A checksum file of the wrong size fails without fallback", () =>
        {
            using var http = new UpdateHttp(Reply(HttpStatusCode.OK, SetupSums + "!"));
            RefusedUpdate(() => DownloadAndCheck(http.Download, offer));
            http.AssertRequests(gitSums);
        });

        Check("An unavailable Setup at both hosts has no third attempt", () =>
        {
            using var http = new UpdateHttp(sum, Reply(HttpStatusCode.NotFound), Reply(HttpStatusCode.NotFound));
            RefusedUpdate(() => DownloadAndCheck(http.Download, offer));
            http.AssertRequests(gitSums, gitSetup, webSetup);
        });

        Check("Website listing with GitHub assets uses only the listed addresses", () =>
        {
            using var http = new UpdateHttp(Reply(HttpStatusCode.InternalServerError),
                Reply(HttpStatusCode.OK, UpdateRelease(GitHubRoot, MirrorNotes)), Reply(HttpStatusCode.NotFound),
                sum, Reply(HttpStatusCode.NotFound));
            var listing = RunCheck(http.Download).State.Offer!;
            Equal(false, listing.ListedByGitHub);
            RefusedUpdate(() => DownloadAndCheck(http.Download, listing));
            http.AssertRequests(GitHubReleases, MirrorReleases, GitHubNews, gitSums, gitSetup);
        });

        Check("Website listing uses website assets as offered", () =>
        {
            using var http = new UpdateHttp(sum, setup);
            Equal(true, DownloadAndCheck(http.Download, website));
            http.AssertRequests(webSums, webSetup);
        });

        Check("GitHub listing with a website asset tries that address only once", () =>
        {
            using var http = new UpdateHttp(sum, Reply(HttpStatusCode.NotFound));
            RefusedUpdate(() => DownloadAndCheck(http.Download, website with { ListedByGitHub = true }));
            http.AssertRequests(webSums, webSetup);
        });

        Check("Plain HTTP asset redirects are refused and the GitHub file can use its website copy", () =>
        {
            using var http = new UpdateHttp(sum, Redirect("http://files.example/setup"), setup);
            Equal(true, DownloadAndCheck(http.Download, offer));
            http.AssertRequests(gitSums, gitSetup, webSetup);
        });

        foreach (var (listing, start, destination) in new[] {
            (offer, gitSetup, "https://release-assets.githubusercontent.com/setup?token=example"),
            (website, webSetup, "https://files.example/setup?token=example") })
            Check($"Download follows the HTTPS file-store redirect from {listing.Archive.Url.Host}", () =>
            {
                using var http = new UpdateHttp(Redirect(destination), setup);
                using var output = new MemoryStream();
                http.Download.CopySetupAsync(listing, output, null, default).GetAwaiter().GetResult();
                Equal(SetupBytes, Encoding.UTF8.GetString(output.ToArray()));
                http.AssertRequests(start, destination);
            });

        Check("Caller cancellation during asset transfer skips the other host", () =>
        {
            using var cancel = new CancellationTokenSource();
            using var http = new UpdateHttp(() =>
            {
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            });
            using var output = new MemoryStream();
            try { http.Download.CopySetupAsync(offer, output, null, cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { http.AssertRequests(gitSetup); return; }
            throw new InvalidOperationException("The download ignored cancellation.");
        });
    }

    private static (string, Func<HttpResponseMessage>)[] FeedErrors() =>
    [
        ("timeout", () => throw new OperationCanceledException()),
        ("connection refusal", () => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused.")),
        ("451", Reply(HttpStatusCode.UnavailableForLegalReasons)),
        ("500", Reply(HttpStatusCode.InternalServerError)),
        ("redirect", Redirect("https://cod2unbound.com/unexpected"))
    ];

    private static LauncherLibrary PriorNews() => new() { NewsNext = new("NEXT", "Cached", "", null) };

    private static LauncherUpdateCheck RunCheck(ReleaseDownload download, LauncherLibrary? library = null,
        Uri? loopbackOrigin = null, CancellationToken token = default) =>
        CreateUpdater(download, loopbackOrigin).CheckAsync(library ?? new(), cancellationToken: token).GetAwaiter().GetResult();

    private static LauncherUpdates CreateUpdater(ReleaseDownload download, Uri? loopbackOrigin = null) =>
        new(new HttpUpdateSource(download), TimeProvider.System, "0.5.0",
            (version, assets) => WindowsReleaseAssets.Offer(version, assets, loopbackOrigin), TimeZoneInfo.Utc, WindowsReleaseAssets.IsNewer);

    private static string UpdateRelease(string root, string link) => $$"""
        [{"tag_name":"v{{UpdateVersion}}","html_url":"{{link}}","assets":[
            {"name":"{{SetupName}}","size":{{SetupBytes.Length}},"browser_download_url":"{{root}}{{SetupName}}"},
            {"name":"{{SumsName}}","size":{{SetupSums.Length}},"browser_download_url":"{{root}}{{SumsName}}"}]}]
        """;

    private static ReleaseOffer UpdateOffer(string root = GitHubRoot)
    {
        var feed = ReleaseNotes.Parse(Encoding.UTF8.GetBytes(UpdateRelease(root, GitHubNotes)));
        return WindowsReleaseAssets.Offer(UpdateVersion, feed.LatestAssets)!;
    }

    private static bool DownloadAndCheck(ReleaseDownload download, ReleaseOffer offer, Action<long>? progress = null)
    {
        using var output = new MemoryStream();
        string hash = download.ReadSetupChecksumAsync(offer, default).GetAwaiter().GetResult();
        download.CopySetupAsync(offer, output, progress, default).GetAwaiter().GetResult();
        output.Position = 0;
        return ReleasePackage.VerifyArchive(output, offer.Archive.Size, hash);
    }

    private static void RefusedUpdate(Action action)
    {
        try { action(); }
        catch (LauncherUpdateException) { return; }
        throw new InvalidOperationException("The update should have been refused.");
    }

    private static Func<HttpResponseMessage> Reply(HttpStatusCode status, string body = "") =>
        () => new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private static Func<HttpResponseMessage> Redirect(string destination) =>
        () => new(HttpStatusCode.Redirect) { Headers = { Location = new(destination) } };

    private sealed class HttpUpdateSource(ReleaseDownload download) : ILauncherReleaseSource
    {
        public ValueTask<ReleaseFeed> ReleasesAsync(TimeZoneInfo timeZone, CancellationToken token) =>
            ReleaseNotes.FetchAsync(download, timeZone, token);

        public ValueTask<ImmutableArray<byte>?> NewsAsync(CancellationToken token) => NewsFeed.FetchAsync(download, token);
    }

    private sealed class UpdateHttp : HttpMessageHandler
    {
        private readonly HttpClient http;
        private readonly Func<HttpResponseMessage>[] responses;
        private readonly List<string> requests = [];
        internal readonly List<string> Accepts = [];

        internal UpdateHttp(params Func<HttpResponseMessage>[] responses)
        {
            this.responses = responses;
            http = new(this, disposeHandler: false);
        }

        internal ReleaseDownload Download => new(http);
        internal ReleaseDownload Loopback(Uri origin) => new(http, origin);
        internal void AssertRequests(params string[] expected) => Sequence(expected, requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Accepts.Add(string.Join(", ", request.Headers.Accept));
            if (requests.Count > responses.Length)
                throw new InvalidOperationException($"Unexpected request to {request.RequestUri}.");
            return Task.FromResult(responses[requests.Count - 1]());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                http.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class InterruptedAssetStream() : MemoryStream([1])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == 0 ? base.ReadAsync(buffer, cancellationToken) :
                ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "The transfer ended early."));
    }

    private sealed class UnmeasuredAsset(byte[] bytes) : ByteArrayContent(bytes)
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
