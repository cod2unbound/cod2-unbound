using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public enum CrashOwner { Unbound, Cod2x }
public enum CrashReportStatus { Sent, Cod2x, Unreadable, TooLarge, Key, Offline, Refused }

public sealed partial class SentryDsn
{
    private SentryDsn(string text, string key, string? secret, Uri endpoint) =>
        (Text, PublicKey, SecretKey, EnvelopeUrl) = (text, key, secret, endpoint);

    internal string Text { get; }
    public string PublicKey { get; }
    private string? SecretKey { get; }
    public Uri EnvelopeUrl { get; }

    public static SentryDsn? Parse(string input)
    {
        string text = input.Trim();
        if (text.Length > 512 || text.Any(char.IsControl) || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Host.Length == 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IdnHost is "localhost" or "127.0.0.1" or "::1")))
            return null;
        string[] auth = Uri.UnescapeDataString(uri.UserInfo).Split(':', 2);
        string? secret = auth.Length == 2 && auth[1].Length > 0 ? auth[1] : null;
        string[] path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!KeyPattern().IsMatch(auth[0]) || (secret is not null && !KeyPattern().IsMatch(secret)) || path.Length == 0 ||
            !ProjectPattern().IsMatch(path[^1]) || path.Any(part => part.Contains('%')))
            return null;
        var endpoint = new UriBuilder(uri) { UserName = "", Password = "", Path = "/" + string.Join('/', path.SkipLast(1).Append("api").Append(path[^1]).Append("envelope")) + "/" };
        return new(text, auth[0], secret, endpoint.Uri);
    }

    public SentryDsn? ForLoopback(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1" ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            return null;
        var endpoint = new UriBuilder(uri) { Path = EnvelopeUrl.AbsolutePath }.Uri;
        return new(Text, PublicKey, SecretKey, endpoint);
    }

    public string Authorization(string client) =>
        $"Sentry sentry_version=7, sentry_client={client}, sentry_key={PublicKey}" +
        (SecretKey is null ? "" : ", sentry_secret=" + SecretKey);

    public override string ToString() => "Sentry report endpoint";

    [GeneratedRegex(@"\A[A-Za-z0-9]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\A[0-9]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectPattern();
}

public sealed record CrashReportFacts(string Release, string OperatingSystem, string OsVersion, string Model);
public sealed record CrashUpload(Uri Endpoint, ImmutableArray<byte> Body, TimeSpan Timeout, string Authorization)
{
    public const string ContentType = "application/x-sentry-envelope";
    public const string UserAgent = "CoD2-Unbound-Launcher";
    public const string Language = "en";
    public override string ToString() => "Crash report upload";
}

public interface ICrashReportService
{
    // Sidecars and logs are inspected only in the private verified home. Read at most maximumBytes
    // (return that many to signal a too-large file), and refuse reparse points or nonregular files.
    ValueTask<ImmutableArray<byte>?> ReadAsync(CrashFile file, int maximumBytes, CancellationToken cancellationToken);
    // Serialize lookups, submission and ledger publication across launchers. Keep the last 500
    // digests. Transport forwards no cookies, ambient credentials or redirects.
    ValueTask<ICrashReportLease> AcquireAsync(CancellationToken cancellationToken);
}

public interface ICrashReportLease : IAsyncDisposable
{
    bool WasSent(string digest);
    ValueTask<int> SendAsync(CrashUpload request, CancellationToken cancellationToken);
    ValueTask RecordSentAsync(string digest, CancellationToken cancellationToken);
}

public sealed partial class CrashReporting(ICrashReportService service, TimeProvider clock, Func<Guid>? newEventId = null)
{
    public const string ConsentVariable = "COD2_UNBOUND_COD2X_REPORTS";
    public const string LedgerName = "crash-reports-sent.json";
    public const int SizeLimit = 1 << 20;
    private static readonly CrashJson EnvelopeJson = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
    private static readonly string[] PrivateWords =
    [
        "password", "passwd", "cdkey", "codkey", "cd key", "cd-key", "rcon", "authorization",
        "getkeyauthorize", "getchallenge", "userinfo", "cod2x://", "cod2://"
    ];

    public static void ApplyConsent(IDictionary<string, string> environment, GameEdition edition, bool cod2xReports)
    {
        if (edition == GameEdition.Cod2x && cod2xReports)
            environment[ConsentVariable] = "1";
        else
            environment.Remove(ConsentVariable);
    }

    public static CrashOwner Owner(string name, bool hasRegularSidecar) =>
        hasRegularSidecar && name.StartsWith("cod2x_", StringComparison.Ordinal) ? CrashOwner.Cod2x : CrashOwner.Unbound;

    public static string StatusMessage(CrashReportStatus status) => status switch
    {
        CrashReportStatus.Sent => "Report sent.",
        CrashReportStatus.Cod2x => "The CoD2x project gets this report the next time you play CoD2x.",
        CrashReportStatus.Unreadable => "Report not sent: it couldn’t be read.",
        CrashReportStatus.TooLarge => "Report not sent: it was too large.",
        CrashReportStatus.Key => "Report not sent: it might contain your CD key, so it stayed on this Mac.",
        CrashReportStatus.Offline => "Report not sent: couldn’t reach the report service. Check your connection.",
        CrashReportStatus.Refused => "Report not sent: the report service didn’t accept it.",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static bool ContainsKey(string text) => text.Contains("codkey", StringComparison.OrdinalIgnoreCase) ||
        KeyCandidates().Matches(text).Cast<Match>().Any(match => CdKey.IsValid(match.Groups[1].Value));

    public static string? Redact(string text, string home)
    {
        if (ContainsKey(text))
            return null;
        string result = home.Length > 0 && home is not ("/" or "\\") ? text.Replace(home, "~", StringComparison.Ordinal) : text;
        result = MacHomes().Replace(result, "~");
        string redacted = string.Join('\n', result.Split('\n').Where(line => !CommandKey().IsMatch(line) && !CommandValue().IsMatch(line)));
        return ContainsKey(redacted) ? null : redacted;
    }

    // Windows logs can carry credentials and identifiers in addition to the Mac report format.
    internal static string? RedactForSessionLog(string text, string home) =>
        Redact(text, home) is { } redacted ? WindowsHomes().Replace(redacted, "~") : null;

    private static string? RedactWindows(string text, string home, IEnumerable<string> secrets)
    {
        if (Redact(text, home) is not { } redacted)
            return null;
        var known = secrets.Where(secret => secret.Length > 0).ToArray();
        return string.Join('\n', redacted.Split('\n').Where(line => !IsPrivate(line, known) && !Identifiers().IsMatch(line))
            .Select(line => InternetAddresses().Replace(WindowsHomes().Replace(line, "~"), "[address omitted]")));
    }

    public static string Reason(string text, string name)
    {
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Trim().ToLowerInvariant() is not ("crash" or "reason" or "signal"))
                continue;
            string reason = line[(colon + 1)..].Trim(' ', '\t');
            if (reason.Length > 0)
                return LauncherText.Prefix(reason, 200);
        }
        return name.Contains("freeze", StringComparison.Ordinal) ? "The game stopped responding" : "The game crashed";
    }

    public static ImmutableArray<byte> Envelope(string name, string redacted, DateTimeOffset occurred, CrashReportFacts facts,
        SentryDsn dsn, Guid id, DateTimeOffset sentAt) =>
        EncodeEnvelope(name, redacted, occurred, facts, dsn, id, sentAt, null);

    public static ImmutableArray<byte> WindowsPreview(CrashReportFacts facts, DateTimeOffset now) =>
        EncodeEnvelope(WindowsCrashReport.AttachmentName, WindowsCrashReport.Example, now, facts, null, Guid.Empty, now, GameEdition.Original);

    private static ImmutableArray<byte> EncodeEnvelope(string name, string redacted, DateTimeOffset occurred, CrashReportFacts facts,
        SentryDsn? dsn, Guid id, DateTimeOffset sentAt, GameEdition? windowsEdition)
    {
        if (!CrashFiles.IsReportName(name) || name.Any(char.IsControl))
            throw new ArgumentException("Select a crash report from the private game home.", nameof(name));
        if (ContainsKey(redacted) || ContainsKey(name))
            throw new InvalidDataException("Crash report contains a possible key.");
        string eventId = id.ToString("N");
        bool mac = facts.OperatingSystem == "macOS";
        string engine = windowsEdition is { } edition ? (edition == GameEdition.Cod2x ? "cod2x" : "original") :
            name.StartsWith("cod2x_", StringComparison.Ordinal) ? "cod2x" : "original";
        if (windowsEdition is not null)
            facts = new(UsageEvent.Version(facts.Release) ?? "unknown", "Windows",
                UsageFacts.SafeWindows(facts.OsVersion), UsageFacts.SafeModel(facts.Model));
        var tags = new Dictionary<string, string> { ["engine"] = engine, [mac ? "macos" : "windows"] = facts.OsVersion,
            [mac ? "mac_model" : windowsEdition is not null ? "pc_model" : "windows_arch"] = facts.Model };
        var root = new CrashEvent(eventId, occurred.ToUnixTimeSeconds(), "native", "fatal",
            new(windowsEdition is not null ? WindowsCrashReport.Reason(redacted) : Reason(redacted, name)), facts.Release, tags,
            new(new("os", facts.OperatingSystem, facts.OsVersion), new("device", facts.Model)),
            new("cod2-unbound.launcher", facts.Release, new("never")));
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(root, EnvelopeJson.CrashEvent);
        byte[] attachment = Encoding.UTF8.GetBytes(redacted);
        var sent = new CrashHeader(eventId, dsn?.Text,
            sentAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(sent, EnvelopeJson.CrashHeader);
        return [.. header, 10,
            .. JsonSerializer.SerializeToUtf8Bytes(new CrashItem("event", body.Length), EnvelopeJson.CrashItem),
            10, .. body, 10, .. JsonSerializer.SerializeToUtf8Bytes(new CrashItem("attachment", attachment.Length,
                name, "text/plain"), EnvelopeJson.CrashItem), 10, .. attachment, 10];
    }

    public async Task<CrashReportStatus?> ReportAsync(IEnumerable<(CrashFile File, CrashOwner Owner)> reports,
        string home, bool sendOurs, bool sendCod2x, SentryDsn? dsn, CrashReportFacts facts,
        IEnumerable<string>? secrets = null, CancellationToken cancellationToken = default)
    {
        var entries = reports.ToArray();
        var ours = entries.Where(entry => entry.Owner == CrashOwner.Unbound).Select(entry => entry.File).ToArray();
        if (!sendOurs || dsn is null || ours.Length == 0)
            return sendCod2x && ours.Length < entries.Length ? CrashReportStatus.Cod2x : null;

        await using var lease = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
        CrashReportStatus? status = null;
        foreach (var file in ours)
        {
            var result = await SendAsync(file).ConfigureAwait(false);
            if (status is null or CrashReportStatus.Sent)
                status = result;
        }
        return status;

        async ValueTask<CrashReportStatus?> SendAsync(CrashFile file)
        {
            if (!CrashFiles.IsReportName(file.Name) || file.Name.Any(char.IsControl))
                return CrashReportStatus.Unreadable;
            if (ContainsKey(file.Name))
                return CrashReportStatus.Key;
            ImmutableArray<byte>? bytes;
            try { bytes = await service.ReadAsync(file, SizeLimit + 1, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return CrashReportStatus.Unreadable; }
            if (bytes is null) return CrashReportStatus.Unreadable;
            if (bytes.Value.Length > SizeLimit) return CrashReportStatus.TooLarge;
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes.Value.AsSpan()));
            if (lease.WasSent(digest)) return null;
            string? text = RedactWindows(Encoding.UTF8.GetString(bytes.Value.AsSpan()), home, secrets ?? []);
            if (text is null) return CrashReportStatus.Key;
            var upload = new CrashUpload(dsn.EnvelopeUrl, Envelope(file.Name, text, file.Modified, facts, dsn,
                newEventId?.Invoke() ?? Guid.NewGuid(), clock.GetUtcNow().ToUniversalTime()), TimeSpan.FromSeconds(15),
                dsn.Authorization("cod2-unbound-launcher/" + facts.Release));
            int code;
            try { code = await lease.SendAsync(upload, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when ((error is IOException or HttpRequestException or OperationCanceledException) &&
                !cancellationToken.IsCancellationRequested) { return CrashReportStatus.Offline; }
            if (code is < 200 or >= 300) return CrashReportStatus.Refused;
            await lease.RecordSentAsync(digest, CancellationToken.None).ConfigureAwait(false);
            return CrashReportStatus.Sent;
        }
    }

    public async Task<CrashReportStatus?> ReportWindowsAsync(IEnumerable<CrashFile> reports, GameEdition edition,
        SentryDsn dsn, CrashReportFacts facts, IEnumerable<string> privateValues, CancellationToken cancellationToken)
    {
        await using var lease = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
        CrashReportStatus? result = null;
        foreach (var file in reports.Where(file => WindowsCrashReport.IsReportName(file.Name)).Take(WindowsCrashReport.ReportsPerExit))
        {
            ImmutableArray<byte>? bytes;
            try { bytes = await service.ReadAsync(file, SizeLimit + 1, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { result = CrashReportStatus.Unreadable; continue; }
            if (bytes is null) { result = CrashReportStatus.Unreadable; continue; }
            if (bytes.Value.Length > SizeLimit) { result = CrashReportStatus.TooLarge; continue; }
            string? text = WindowsCrashReport.Parse(bytes.Value, privateValues);
            if (text is null) { result = CrashReportStatus.Key; continue; }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.ASCII.GetBytes(file.Name));
            hash.AppendData(bytes.Value.AsSpan());
            string digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (lease.WasSent(digest))
                continue;
            // Retry the same Sentry ID if a response or ledger write is interrupted.
            var id = newEventId?.Invoke() ?? new Guid(Convert.FromHexString(digest[..32]));
            var upload = new CrashUpload(dsn.EnvelopeUrl,
                EncodeEnvelope(WindowsCrashReport.AttachmentName, text, file.Modified, facts, null, id, clock.GetUtcNow(), edition),
                TimeSpan.FromSeconds(15), dsn.Authorization("cod2-unbound-launcher/" + (UsageEvent.Version(facts.Release) ?? "unknown")));
            int code;
            try { code = await lease.SendAsync(upload, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when ((error is IOException or HttpRequestException or OperationCanceledException) &&
                !cancellationToken.IsCancellationRequested) { return CrashReportStatus.Offline; }
            if (code is < 200 or >= 300)
                return CrashReportStatus.Refused;
            await lease.RecordSentAsync(digest, CancellationToken.None).ConfigureAwait(false);
            result = CrashReportStatus.Sent;
        }
        return result;
    }

    internal static bool IsPrivate(string line, IEnumerable<string> secrets)
    {
        string plain = ColorMarkup().Replace(line, "");
        return PrivateWords.Any(word => plain.Contains(word, StringComparison.OrdinalIgnoreCase) || line.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
            secrets.Any(secret => secret.Length > 0 && line.Contains(secret, StringComparison.Ordinal)) || ContainsKey(line);
    }

    [GeneratedRegex(@"\^\p{Nd}", RegexOptions.CultureInvariant)]
    private static partial Regex ColorMarkup();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?=((?:[A-Za-z0-9]{4}[- ]?){4}[A-Fa-f0-9]{4})(?![A-Za-z0-9]))", RegexOptions.CultureInvariant)]
    private static partial Regex KeyCandidates();

    [GeneratedRegex("""/Users/[^/\s"'<>:]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MacHomes();

    [GeneratedRegex("""(?:[A-Z]:|\\\\[^\\/\s]+[\\/][^\\/\s]+)[\\/]Users[\\/][^\\/\r\n"'<>:]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsHomes();

    [GeneratedRegex(@"^\s*(command|command ?line|cmdline|arguments|argv|args)\s*[:=]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandKey();

    [GeneratedRegex(@"\+(set|seta|exec|connect|password|demo)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandValue();

    [GeneratedRegex(@"\b(hwid\d*|serial(?:[ _-]*number)?|machine[ _-]*guid)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Identifiers();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|(?<![\w:/])[0-9a-f]*:[0-9a-f]*:(?:[0-9a-f]*:)*[0-9a-f]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InternetAddresses();
}

internal sealed record CrashEvent(string EventId, long Timestamp, string Platform, string Level, CrashLogEntry Logentry,
    string Release, Dictionary<string, string> Tags, CrashContexts Contexts, CrashSdk Sdk);
internal sealed record CrashLogEntry(string Formatted);
internal sealed record CrashContexts(CrashOs Os, CrashDevice Device);
internal sealed record CrashOs(string Type, string Name, string Version);
internal sealed record CrashDevice(string Type, string Model);
internal sealed record CrashSdk(string Name, string Version, CrashSdkSettings Settings);
internal sealed record CrashSdkSettings(string InferIp);
internal sealed record CrashHeader(string EventId, string? Dsn, string SentAt);
internal sealed record CrashItem(string Type, int Length, string? Filename = null, string? ContentType = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CrashEvent))]
[JsonSerializable(typeof(CrashHeader))]
[JsonSerializable(typeof(CrashItem))]
internal sealed partial class CrashJson : JsonSerializerContext;
