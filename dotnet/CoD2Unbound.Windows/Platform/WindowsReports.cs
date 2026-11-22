using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsReports(string home, WindowsSettingsFolder folder, string version)
{
    private readonly PrivateReportTransport transport = new();
    private readonly SentryDsn? crashDsn = GetCrashDsn();
    private CancellationTokenSource? crashSend;
    internal WindowsReportFacts Facts { get; } = new(version);
    internal bool CrashAvailable => crashDsn is not null;
    internal UsageData Usage { get; private set; } = null!;

    internal async Task InitializeAsync(GameSettings settings, bool setupComplete)
    {
        Usage = new(folder, GetUsageEndpoint(), Facts.ReadAsync, async (address, body, token) =>
            await transport.PostAsync(address, body, "application/json", "cod2unbound-launcher", null,
                TimeSpan.FromSeconds(20), token).ConfigureAwait(false) is >= 200 and < 300);
        await Usage.InitializeAsync(settings.UsageConsent, setupActive: !setupComplete);
        await Usage.OpenedAsync();
    }

    internal string CrashPreview() => Encoding.UTF8.GetString(
        CrashReporting.WindowsPreview(Facts.Crash(), DateTimeOffset.UtcNow).AsSpan());

    internal void CancelCrash() => crashSend?.Cancel();

    internal async Task SendCrashesAsync(DateTimeOffset since, GameEdition edition, int process, string? cdKey, bool enabled)
    {
        if (!enabled || crashDsn is null || crashSend is not null)
            return;
        using var request = new CancellationTokenSource();
        crashSend = request;
        try
        {
            await Task.Run(async () =>
            {
                using var root = new WindowsFiles.DirectoryHandles(home);
                var files = new DirectoryInfo(home).EnumerateFiles(WindowsCrashReport.FilePrefix + "*.txt")
                    .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        file.LastWriteTimeUtc >= since && WindowsCrashReport.IsReportName(file.Name) &&
                        file.Name.EndsWith("_" + process.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt", StringComparison.Ordinal))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(WindowsCrashReport.ReportsPerExit)
                    .Select(file => new CrashFile(file.Name, file.FullName, file.LastWriteTimeUtc)).ToArray();
                if (files.Length > 0)
                {
                    var reporter = new CrashReporting(new CrashService(home, folder, transport), TimeProvider.System);
                    await reporter.ReportWindowsAsync(files, edition, crashDsn, Facts.Crash(),
                        [.. WindowsReportFacts.PrivateNames(), cdKey ?? ""], request.Token).ConfigureAwait(false);
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        finally { crashSend = null; }
    }

    private static string? Metadata(string key) => typeof(WindowsReports).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == key)?.Value;

    private static UsageEndpoint? GetUsageEndpoint()
    {
#if LOCAL_REPORT_RECEIVERS
        return Environment.GetEnvironmentVariable("COD2_USAGE_URL") is { } address
            ? UsageEndpoint.Create(Metadata("UsagePostHogKey"), address) : null;
#else
        return UsageEndpoint.Create(Metadata("UsagePostHogKey"));
#endif
    }

    private static SentryDsn? GetCrashDsn()
    {
        if (Metadata("CrashReportDsn") is not { } value || SentryDsn.Parse(value) is not { } dsn)
            return null;
#if LOCAL_REPORT_RECEIVERS
        return Environment.GetEnvironmentVariable("COD2_CRASH_URL") is { } address ? dsn.ForLoopback(address) : null;
#else
        return dsn.EnvelopeUrl.Scheme == "https" && !dsn.EnvelopeUrl.IsLoopback ? dsn : null;
#endif
    }

    private sealed class CrashService(string home, WindowsSettingsFolder folder, PrivateReportTransport transport) : ICrashReportService
    {
        public async ValueTask<ImmutableArray<byte>?> ReadAsync(CrashFile file, int maximumBytes, CancellationToken cancellationToken)
        {
            using var input = WindowsFiles.Open(home, file.Name);
            var data = new byte[Math.Min(maximumBytes, (int)Math.Min(int.MaxValue, input.Length))];
            await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            return [.. data];
        }

        public async ValueTask<ICrashReportLease> AcquireAsync(CancellationToken cancellationToken)
        {
            var file = await WindowsGameSessionHost.OpenLeaseAsync(folder.Root, ".crash-reports.lock", cancellationToken).ConfigureAwait(false);
            try
            {
                var digests = await folder.ReadAsync(CrashReporting.LedgerName, cancellationToken).ConfigureAwait(false);
                string[] history = [];
                if (digests is { } data && data.Length <= 40 * 1024)
                {
                    try { history = JsonSerializer.Deserialize(data.AsSpan(), WindowsCrashLedgerJson.Default.StringArray) ?? []; }
                    catch (JsonException) { }
                }
                return new Ledger(file, history.Where(digest => digest is { Length: 64 } &&
                    digest.All(c => char.IsAsciiHexDigitLower(c))).TakeLast(WindowsCrashReport.LedgerLimit), folder, transport);
            }
            catch
            {
                await file.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private sealed class Ledger(FileStream lease, IEnumerable<string> history, ISettingsFolder folder, PrivateReportTransport transport)
        : ICrashReportLease
    {
        private readonly Queue<string> sent = new(history);

        public bool WasSent(string digest) => sent.Contains(digest);

        public ValueTask<int> SendAsync(CrashUpload request, CancellationToken cancellationToken) =>
            transport.PostAsync(request.Endpoint, request.Body.AsMemory(), CrashUpload.ContentType, CrashUpload.UserAgent,
                request.Authorization, request.Timeout, cancellationToken);

        public async ValueTask RecordSentAsync(string digest, CancellationToken cancellationToken)
        {
            sent.Enqueue(digest);
            if (sent.Count > WindowsCrashReport.LedgerLimit)
                sent.Dequeue();
            var data = JsonSerializer.SerializeToUtf8Bytes(sent.ToArray(), WindowsCrashLedgerJson.Default.StringArray);
            await folder.WriteAsync(CrashReporting.LedgerName, [.. data], cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class WindowsCrashLedgerJson : JsonSerializerContext;
