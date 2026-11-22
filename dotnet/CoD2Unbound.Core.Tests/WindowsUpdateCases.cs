using System.Net;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void WindowsUpdateCases()
    {
        const string version = "0.5.1";
        const string name = "CoD2-Unbound-0.5.1-windows-x64-setup.exe";
        const string root = "https://github.com/cod2unbound/cod2-unbound/releases/download/v0.5.1/";
        var setup = new ReleaseAsset(name, 3, new(root + name));
        var sums = new ReleaseAsset("SHA256SUMS", 100, new(root + "SHA256SUMS"));

        Check("Windows offer requires the exact versioned installer and checksum paths", () =>
        {
            Equal(setup, WindowsReleaseAssets.Offer(version, [setup, sums])?.Archive);
            Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(version, [setup, setup, sums]));
            Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(version, [setup with { Size = long.MaxValue }, sums]));
            Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(version, [setup, sums with { Size = 65537 }]));
            Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer("0.05.1", [setup, sums]));
            foreach (string invalid in new[] { "https://elsewhere.example/" + name, root + name + "?next=no",
                root.Replace("v0.5.1", "v0.5.0") + name, root.Replace("https://", "https://user@") + name,
                root + "folder/../" + name, root + "other.exe" })
                Equal<ReleaseOffer?>(null, WindowsReleaseAssets.Offer(version, [setup with { Url = new(invalid) }, sums]));
            Equal(false, WindowsReleaseAssets.IsNewer("0.5.1", version));
            Equal(false, WindowsReleaseAssets.IsNewer("0.5.0", version));
            Equal(false, WindowsReleaseAssets.IsNewer("0.5.2-preview", version));
            Equal(true, WindowsReleaseAssets.IsNewer("0.5.10", version));
        });

        Check("Windows download redirects stay on allowed hosts and conserve the declared length", () =>
        {
            var cdn = new Uri("https://release-assets.githubusercontent.com/setup?token=example");
            var link = new Uri("https://elsewhere.example/setup");
            foreach (var destination in new[] { cdn, link })
            {
                using var handler = new UpdateResponses(Redirect(destination), Bytes([1, 2, 3]));
                using var http = new HttpClient(handler);
                var download = new ReleaseDownload(http);
                using var output = new MemoryStream();
                if (destination == cdn)
                {
                    download.CopyAsync(setup.Url, output, 3, exactBytes: 3, progress: null, default).GetAwaiter().GetResult();
                    Equal(3L, output.Length);
                    Equal(2, handler.Requests);
                }
                else
                {
                    Refused(() => download.CopyAsync(setup.Url, output, 3, 3, null, default).GetAwaiter().GetResult());
                    Equal(1, handler.Requests);
                }
            }
            foreach (byte[] body in new byte[][] { [1, 2], [1, 2, 3, 4] })
            {
                using var handler = new UpdateResponses(Bytes(body));
                using var http = new HttpClient(handler);
                using var output = new MemoryStream();
                Refused(() => new ReleaseDownload(http).CopyAsync(setup.Url, output, 3, 3, null, default).GetAwaiter().GetResult());
                Equal(true, output.Length <= 3);
            }
        });

        static void Refused(Action download)
        {
            try { download(); }
            catch (LauncherUpdateException) { return; }
            throw new InvalidOperationException("The download should have been refused.");
        }

        static HttpResponseMessage Redirect(Uri destination) =>
            new(HttpStatusCode.Redirect) { Headers = { Location = destination } };
        static HttpResponseMessage Bytes(byte[] data) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    }

    private sealed class UpdateResponses(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses[Requests++]);
    }
}
