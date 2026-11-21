using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void LibraryCases()
    {
        using var fixture = Fixtures("library");
        var root = fixture.RootElement;
        foreach (var row in root.GetProperty("demos").EnumerateArray())
            Check("Swift demo name " + row.GetProperty("name").GetString(), () =>
            {
                string name = row.GetProperty("name").GetString()!;
                Equal(row.GetProperty("alias").GetString(), MediaName.Alias(name));
                Equal(row.GetProperty("playable").GetBoolean(), MediaName.Playable(name));
            });
        foreach (var row in root.GetProperty("maps").EnumerateArray())
            Check("Swift map and mode " + row.GetProperty("map").GetString(), () =>
            {
                string name = row.GetProperty("map").GetString()!;
                Equal(row.GetProperty("key").GetString(), MapCatalog.Key(name));
                Equal(row.GetProperty("display").GetString(), MapCatalog.DisplayName(name));
                Equal(row.GetProperty("region").GetString(), MapCatalog.Region(name));
                Equal(row.GetProperty("scenery").GetInt32(), (int)MapCatalog.Scenery(name));
                Equal(row.GetProperty("mode").GetString(), GameModes.Long(name));
            });
        foreach (var row in root.GetProperty("media").EnumerateArray())
            Check("Swift media facts", () =>
            {
                Equal(row.GetProperty("size").GetString(), MediaFacts.SizeLabel(row.GetProperty("bytes").GetInt64()));
                Equal(row.GetProperty("map").GetString(), MediaFacts.MapHint(row.GetProperty("name").GetString()!));
            });
        foreach (var row in root.GetProperty("crashes").EnumerateArray())
            Check("Swift crash and console redaction", () =>
            {
                string text = row.GetProperty("text").GetString()!;
                string home = row.GetProperty("home").GetString()!;
                Equal(row.GetProperty("containsKey").GetBoolean(), CrashReporting.ContainsKey(text));
                string? result = CrashReporting.Redact(text, home);
                Equal(row.GetProperty("redacted").GetString(), result);
                Equal(row.GetProperty("reason").GetString(), result is null ? null : CrashReporting.Reason(result, row.GetProperty("name").GetString()!));
                Equal(row.GetProperty("filtered").GetString(), GameSessionLog.Filter(text.Split('\n')[0], ["synthetic-secret"], home));
            });
        Check("Swift Sentry envelope framing, redaction metadata and endpoint", () =>
        {
            var dsn = SentryDsn.Parse("https://exampleKey:exampleSecret@fixture.invalid/reporting/path/123")!;
            Equal(root.GetProperty("endpoint").GetString(), dsn.EnvelopeUrl.AbsoluteUri);
            Equal(root.GetProperty("authorization").GetString(), dsn.Authorization("fixture/1"));
            var now = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("news").GetProperty("now").GetInt64());
            byte[] expected = root.GetProperty("envelope").GetBytesFromBase64();
            var actual = CrashReporting.Envelope("cod2_crash_1.txt", "Reason: synthetic log\n", now,
                new("0.5.0", "macOS", "26.0", "Fixture"), dsn, Guid.Parse("00000000-0000-0000-0000-000000000001"), now);
            string[] wanted = Encoding.UTF8.GetString(expected).Split('\n');
            string[] got = Encoding.UTF8.GetString(actual.AsSpan()).Split('\n');
            Equal(wanted.Length, got.Length);
            for (int index = 0; index < 4; index++)
            {
                using var a = JsonDocument.Parse(wanted[index]);
                using var b = JsonDocument.Parse(got[index]);
                Equal(true, JsonElement.DeepEquals(a.RootElement, b.RootElement));
            }
            Sequence(wanted.Skip(4), got.Skip(4));
        });
        Check("Swift project news feed", () =>
        {
            var news = root.GetProperty("news");
            var parsed = NewsFeed.Parse(news.GetProperty("input").GetBytesFromBase64(),
                DateTimeOffset.FromUnixTimeSeconds(news.GetProperty("now").GetInt64()), TimeZoneInfo.Utc);
            var expected = news.GetProperty("items").EnumerateArray().ToArray();
            Equal(expected.Length, parsed.Items.Length);
            foreach (var (row, item) in expected.Zip(parsed.Items))
                CheckDispatch(row, item);
            CheckDispatch(news.GetProperty("next"), parsed.Next!);
        });
        Check("Copy demos by collision-free private alias and reuse exact bytes", () =>
        {
            var sink = new MemoryMedia([DemoPublishResult.Occupied, DemoPublishResult.Identical]);
            var library = new MediaLibrary(sink);
            Equal("round__1_-2", library.PrepareDemoAsync(new Uri("file:///tmp/recording.dm_1"), "round (1).dm_1").AsTask().GetAwaiter().GetResult());
            Sequence(new[] { "round__1_.dm_1", "round__1_-2.dm_1" }, sink.Copies);
            Equal(false, MediaLibrary.Accepts("CON.dm_1", MediaKind.Demo));
            Equal(false, MediaLibrary.Accepts("C:\\demos\\demo.dm_1", MediaKind.Demo));
            Equal(false, MediaLibrary.Accepts("image.png:alternate", MediaKind.Screenshot));
            Equal(true, MediaLibrary.Accepts("image.TGA", MediaKind.Screenshot));
        });
        Check("Crash consent gates access and only a redacted unique report can upload", () =>
        {
            var sink = new MemoryCrash();
            var dsn = SentryDsn.Parse("http://publicKey@127.0.0.1:9/123");
            var reporting = new CrashReporting(sink, new FixedClock(), () => Guid.Empty);
            (CrashFile, CrashOwner)[] reports = [(new("cod2_crash_1.txt", "/fixture", DateTimeOffset.UnixEpoch), CrashOwner.Unbound)];
            var facts = new CrashReportFacts("0.5.0", "Windows", "11", "x64");
            Equal<CrashReportStatus?>(null, reporting.ReportAsync(reports, @"C:\Users\fixture", false, false, dsn, facts).GetAwaiter().GetResult());
            Equal(0, sink.Acquisitions);
            Equal<CrashReportStatus?>(CrashReportStatus.Sent, reporting.ReportAsync(reports, @"C:\Users\fixture", true, false, dsn, facts).GetAwaiter().GetResult());
            string body = Encoding.UTF8.GetString(sink.Upload!.Body.AsSpan());
            Equal(false, body.Contains("login-secret", StringComparison.Ordinal));
            Equal(false, body.Contains("203.0.113.1", StringComparison.Ordinal));
            Equal(false, body.Contains(@"C:\Users\fixture", StringComparison.Ordinal));
            Equal<CrashReportStatus?>(null, reporting.ReportAsync(reports, @"C:\Users\fixture", true, false, dsn, facts).GetAwaiter().GetResult());
            Equal(1, sink.Sends);
        });

        static void CheckDispatch(JsonElement row, NewsDispatch item) =>
            Equal((row.GetProperty("stamp").GetString(), row.GetProperty("title").GetString(), row.GetProperty("summary").GetString(),
                row.GetProperty("link").GetString(), row.GetProperty("open").GetString()),
                (item.Stamp, item.Title, item.Summary, item.Link, item.Open));
    }

    private sealed class MemoryMedia(IEnumerable<DemoPublishResult> outcomes) : IMediaFiles
    {
        private readonly Queue<DemoPublishResult> outcomes = new(outcomes);
        public List<string> Copies { get; } = [];
        public ImmutableArray<MediaFile> List(MediaKind kind) => [];
        public ValueTask<DemoPublishResult> PublishDemoAsync(Uri source, string fileName, CancellationToken cancellationToken)
        {
            Copies.Add(fileName);
            return ValueTask.FromResult(outcomes.Dequeue());
        }
    }

    private sealed class MemoryCrash : ICrashReportService, ICrashReportLease
    {
        private readonly HashSet<string> ledger = [];
        public CrashUpload? Upload { get; private set; }
        public int Acquisitions { get; private set; }
        public int Sends { get; private set; }
        public ValueTask<ImmutableArray<byte>?> ReadAsync(CrashFile file, int limit, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ImmutableArray<byte>?>([.. "Reason: synthetic\npassword login-secret\nconnect 203.0.113.1\nC:\\Users\\fixture\\test"u8]);
        public ValueTask<ICrashReportLease> AcquireAsync(CancellationToken cancellationToken)
        {
            Acquisitions++;
            return ValueTask.FromResult<ICrashReportLease>(this);
        }
        public bool WasSent(string digest) => ledger.Contains(digest);
        public ValueTask RecordSentAsync(string digest, CancellationToken cancellationToken) { ledger.Add(digest); return ValueTask.CompletedTask; }
        public ValueTask<int> SendAsync(CrashUpload request, CancellationToken cancellationToken)
        {
            Sends++;
            Upload = request;
            return ValueTask.FromResult(204);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
