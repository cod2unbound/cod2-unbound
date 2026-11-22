using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public sealed class UsageEndpoint
{
    private readonly string key;
    public Uri Address { get; }

    private UsageEndpoint(string key, Uri address) => (this.key, Address) = (key, address);

    public static UsageEndpoint? Create(string? key, string? loopback = null)
    {
        if (key is null || key.Length > 204 || !Regex.IsMatch(key, @"\Aphc_[A-Za-z0-9_-]{1,200}\z", RegexOptions.CultureInvariant))
            return null;
        if (loopback is null)
            return new(key, new("https://eu.i.posthog.com/batch/"));
        if (!Uri.TryCreate(loopback, UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1" ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is not ("/" or "/batch" or "/batch/"))
            return null;
        return new(key, new UriBuilder(uri) { Path = "/batch/" }.Uri);
    }

    internal byte[] Encode(IEnumerable<UsageOccurrence> events, Guid install)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, UsageOccurrence.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("api_key", key);
            writer.WriteStartArray("batch");
            foreach (var occurrence in events)
                writer.WriteRawValue(occurrence.Encode(install)!);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }
}

public sealed class PrivateReportTransport : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async ValueTask<int> PostAsync(Uri address, ReadOnlyMemory<byte> body, string contentType, string userAgent,
        string? authorization, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = new ReadOnlyMemoryContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.UserAgent.ParseAdd(userAgent);
        if (authorization is not null)
        {
            request.Headers.Add("X-Sentry-Auth", authorization);
            request.Headers.AcceptLanguage.ParseAdd(CrashUpload.Language);
        }
        using var reply = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        return (int)reply.StatusCode;
    }

    public void Dispose() => client.Dispose();
}

internal sealed record UsageOccurrence(Guid Id, long Minute, UsageEvent Event, UsageFacts Facts)
{
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NewLine = "\n"
    };

    internal byte[]? Encode(Guid install, bool indented = false)
    {
        var fields = Facts.Properties();
        if (Event.AddProperties(fields) is not { } name || Minute is < 0 or > 4_223_371_679)
            return null;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions with { Indented = indented }))
        {
            writer.WriteStartObject();
            writer.WriteString("distinct_id", install);
            writer.WriteString("event", name);
            writer.WriteStartObject("properties");
            foreach (var (key, value) in fields)
            {
                switch (value)
                {
                    case string text: writer.WriteString(key, text); break;
                    case int number: writer.WriteNumber(key, number); break;
                    case bool flag: writer.WriteBoolean(key, flag); break;
                }
            }
            writer.WriteEndObject();
            writer.WriteString("timestamp", DateTimeOffset.FromUnixTimeSeconds(Minute * 60)
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("uuid", Id);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }
}

/// A single launcher owns the journal. Storage is serialized and HTTP runs outside the storage gate.
public sealed partial class UsageData
{
    public const int QueueLimit = 300;
    public const int SetupLimit = 24;
    public const string JournalFile = "usage-data.json";
    public const string SetupFile = "usage-setup.json";
    private const int BatchLimit = 32;
    private const int Age = 7 * 24 * 60;

    private sealed record Step(long Minute, SetupStep Name, bool Skipped, bool OwnFiles);
    private sealed record Steps(int SchemaVersion, Step[] Items);
    private sealed record Journal(int SchemaVersion, Guid? Install, long? LastOpenedDay, UsageOccurrence[] Events);
    [JsonSerializable(typeof(Steps))]
    [JsonSerializable(typeof(Journal))]
    private sealed partial class UsageJournalJson : JsonSerializerContext;

    private sealed record Answer(UsageConsent Consent, Guid? Install);
    private readonly ISettingsFolder storage;
    private readonly UsageEndpoint? endpoint;
    private readonly Func<UsageFacts> facts;
    private readonly Func<Uri, byte[], CancellationToken, ValueTask<bool>> send;
    private readonly TimeProvider clock;
    private readonly TimeSpan firstDelay;
    private readonly TimeSpan firstRetry;
    private readonly Func<Guid> newEventId;
    private readonly SemaphoreSlim gate = new(1);
    private Journal journal = new(1, null, null, []);
    private Step[] steps = [];
    private volatile Answer answer = new(UsageConsent.NotAsked, null);
    private CancellationTokenSource? delivery;
    private bool held = true;
    private bool opened;

    public UsageData(ISettingsFolder storage, UsageEndpoint? endpoint, Func<UsageFacts> facts,
        Func<Uri, byte[], CancellationToken, ValueTask<bool>> send, TimeProvider? clock = null,
        TimeSpan? firstDelay = null, TimeSpan? firstRetry = null, Func<Guid>? newEventId = null)
    {
        this.storage = storage;
        this.endpoint = endpoint;
        this.facts = facts;
        this.send = send;
        this.clock = clock ?? TimeProvider.System;
        this.firstDelay = firstDelay ?? TimeSpan.FromSeconds(2);
        this.firstRetry = firstRetry ?? TimeSpan.FromSeconds(5);
        this.newEventId = newEventId ?? Guid.NewGuid;
    }

    public bool Available => endpoint is not null;
    public UsageConsent Consent => answer.Consent;
    public bool ShouldAsk => Available && Consent == UsageConsent.NotAsked;
    public Guid? InstallNumber => answer.Install;
    public int QueuedCount => Volatile.Read(ref journal).Events.Length;
    private long Minute => clock.GetUtcNow().ToUnixTimeSeconds() / 60;

    public async Task InitializeAsync(UsageConsent saved, bool setupActive)
    {
        held = setupActive;
        if (!Available)
            return;
        if (await ReadAsync<Journal>(JournalFile, 512 * 1024).ConfigureAwait(false) is { SchemaVersion: 1, Events: not null } loaded)
        {
            journal = journal with { LastOpenedDay = loaded.LastOpenedDay };
            if (saved == UsageConsent.Yes && loaded.Install is { } install && install != Guid.Empty)
                journal = loaded with { Events = Prune(loaded.Events) };
        }
        if (saved == UsageConsent.Yes && journal.Install is null)
            saved = UsageConsent.No;
        answer = new(saved, journal.Install);
        if (saved == UsageConsent.NotAsked)
        {
            if (await ReadAsync<Steps>(SetupFile, 32 * 1024).ConfigureAwait(false) is { SchemaVersion: 1, Items: not null } stored)
                steps = stored.Items.Where(step => step is not null && Current(step.Minute) && ValidStep(step.Name))
                    .TakeLast(SetupLimit).ToArray();
        }
        else
            await DeleteAsync(SetupFile).ConfigureAwait(false);
        if (saved != UsageConsent.Yes)
            await ClearJournalAsync().ConfigureAwait(false);
    }

    public string? Preview() => Available ? Encoding.UTF8.GetString(new UsageOccurrence(newEventId(), Minute,
        UsageEvent.Join(UsageJoin.List, GameEdition.Cod2x), facts()).Encode(InstallNumber ?? Guid.Empty, indented: true)!) : null;

    public async Task<bool> SetConsentAsync(UsageConsent choice, UsageSource source, Func<UsageConsent, Task> persist)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Available || choice == UsageConsent.NotAsked)
                return false;
            if (choice == Consent)
            {
                await persist(choice).ConfigureAwait(false);
                return choice != UsageConsent.No ||
                    (await ClearJournalAsync().ConfigureAwait(false) & await DeleteAsync(SetupFile).ConfigureAwait(false));
            }
            if (choice == UsageConsent.No)
            {
                Stop();
                answer = new(choice, null);
                steps = [];
                journal = journal with { Install = null, Events = [] };
                bool cleared = await ClearJournalAsync().ConfigureAwait(false) & await DeleteAsync(SetupFile).ConfigureAwait(false);
                await persist(choice).ConfigureAwait(false);
                return cleared;
            }

            long stamp = Minute;
            var machine = facts();
            var candidate = journal with
            {
                Install = Guid.NewGuid(),
                Events = [new(newEventId(), stamp, new(UsageKind.TurnedOn, (int)source), machine),
                    .. steps.Where(step => Current(step.Minute)).Select(step => new UsageOccurrence(newEventId(), step.Minute,
                        UsageEvent.Setup(step.Name, step.Skipped, step.OwnFiles), machine))]
            };
            if (opened)
                candidate = AddDailyOpen(candidate, stamp, machine);
            if (!await WriteAsync(JournalFile, candidate).ConfigureAwait(false))
                return false;
            try { await persist(choice).ConfigureAwait(false); }
            catch
            {
                await ClearJournalAsync().ConfigureAwait(false);
                throw;
            }
            journal = candidate;
            answer = new(choice, journal.Install);
            steps = [];
            await DeleteAsync(SetupFile).ConfigureAwait(false);
            Schedule();
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ResetAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (InstallNumber is null)
                return false;
            Stop();
            var fresh = journal with { Install = Guid.NewGuid(), Events = [] };
            if (!await WriteAsync(JournalFile, fresh).ConfigureAwait(false))
            {
                Schedule();
                return false;
            }
            journal = fresh;
            answer = new(UsageConsent.Yes, fresh.Install);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task HoldAsync(bool value)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            held = value;
            if (held) Stop(); else Schedule();
        }
        finally { gate.Release(); }
    }

    public async Task OpenedAsync()
    {
        var machine = InstallNumber is null ? null : facts();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            opened = true;
            if (InstallNumber is null || machine is null)
                return;
            journal = AddDailyOpen(journal, Minute, machine);
            await SaveAsync().ConfigureAwait(false);
            Schedule();
        }
        finally { gate.Release(); }
    }

    public async Task RecordAsync(UsageEvent? item)
    {
        if (!Available || item is null || item.Kind is UsageKind.AppOpened or UsageKind.TurnedOn)
            return;
        var machine = InstallNumber is null ? null : facts();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            long stamp = Minute;
            if (InstallNumber is not null && machine is not null)
            {
                var occurrence = new UsageOccurrence(newEventId(), stamp, item, machine);
                if (item.AddProperties(new(StringComparer.Ordinal)) is null)
                    return;
                journal = journal with { Events = Prune([.. journal.Events, occurrence]) };
                await SaveAsync().ConfigureAwait(false);
                Schedule();
            }
            else if (Consent == UsageConsent.NotAsked && held && item.IsSetup && ValidStep((SetupStep)item.Choice))
            {
                steps = steps.Where(step => Current(step.Minute)).Append(new(stamp, (SetupStep)item.Choice,
                    item.Kind == UsageKind.SetupSkipped, item.Choice == (int)SetupStep.Data && item.Other == 1))
                    .TakeLast(SetupLimit).ToArray();
                await WriteAsync(SetupFile, new Steps(1, steps)).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }

    public async Task FlushAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
    }

    private Journal AddDailyOpen(Journal current, long stamp, UsageFacts machine) =>
        stamp / 1440 > (current.LastOpenedDay ?? -1)
            ? current with { LastOpenedDay = stamp / 1440,
                Events = Prune([.. current.Events, new(newEventId(), stamp, new(UsageKind.AppOpened), machine)]) }
            : current;

    private bool Current(long stamp) => stamp <= Minute && stamp >= Minute - Age;
    private static bool ValidStep(SetupStep step) => Enum.IsDefined(step) && step != SetupStep.Complete;
    private UsageOccurrence[] Prune(IEnumerable<UsageOccurrence> values) =>
        values.Where(item => item is { Event: not null, Facts: not null } && item.Id != Guid.Empty && Current(item.Minute) &&
            item.Event.AddProperties(new(StringComparer.Ordinal)) is not null).TakeLast(QueueLimit).ToArray();
    private Task<bool> SaveAsync() => WriteAsync(JournalFile, journal);
    private async Task<bool> ClearJournalAsync() =>
        journal.LastOpenedDay is not null && await SaveAsync().ConfigureAwait(false) || await DeleteAsync(JournalFile).ConfigureAwait(false);

    private void Schedule()
    {
        if (held || delivery is not null || InstallNumber is not { } install || journal.Events.Length == 0)
            return;
        var active = new CancellationTokenSource();
        delivery = active;
        _ = Task.Run(() => DeliverAsync(install, active));
    }

    private void Stop()
    {
        var active = delivery;
        delivery = null;
        active?.Cancel();
    }

    private async Task DeliverAsync(Guid install, CancellationTokenSource active)
    {
        var token = active.Token;
        var delay = firstDelay;
        var retry = firstRetry;
        UsageOccurrence[] batch = [];
        byte[]? bytes = null;
        try
        {
            while (true)
            {
                await Task.Delay(delay, clock, token).ConfigureAwait(false);
                await gate.WaitAsync(token).ConfigureAwait(false);
                bool saved;
                try
                {
                    journal = journal with { Events = Prune(journal.Events) };
                    if (journal.Events.Length == 0)
                        return;
                    if (bytes is null || batch.Any(item => !journal.Events.Any(queued => queued.Id == item.Id)))
                    {
                        batch = journal.Events.Take(BatchLimit).ToArray();
                        bytes = endpoint!.Encode(batch, install);
                    }
                    saved = await SaveAsync().ConfigureAwait(false);
                }
                finally { gate.Release(); }

                bool sent = false;
                if (saved)
                {
                    token.ThrowIfCancellationRequested();
                    try { sent = await send(endpoint!.Address, bytes, token).ConfigureAwait(false); }
                    catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { }
                }

                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (sent)
                    {
                        var accepted = batch.Select(item => item.Id).ToHashSet();
                        journal = journal with { Events = journal.Events.Where(item => !accepted.Contains(item.Id)).ToArray() };
                        await SaveAsync().ConfigureAwait(false);
                        bytes = null;
                        retry = firstRetry;
                    }
                    delay = sent ? TimeSpan.Zero : retry;
                    if (!sent) retry = TimeSpan.FromSeconds(Math.Min(3600, retry.TotalSeconds * 2));
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(delivery, active))
                {
                    delivery = null;
                    Schedule();
                }
                active.Dispose();
            }
            finally { gate.Release(); }
        }
    }

    private async Task<T?> ReadAsync<T>(string name, int limit)
    {
        try
        {
            if (await storage.ReadAsync(name, default).ConfigureAwait(false) is { } data && data.Length <= limit)
                return JsonSerializer.Deserialize<T>(data.AsSpan(), UsageJournalJson.Default.Options);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return default;
    }

    private async Task<bool> WriteAsync<T>(string name, T data)
    {
        try
        {
            ImmutableArray<byte> bytes = [.. JsonSerializer.SerializeToUtf8Bytes(data, UsageJournalJson.Default.Options)];
            await storage.WriteAsync(name, bytes, default).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task<bool> DeleteAsync(string name)
    {
        try { await storage.DeleteAsync(name, default).ConfigureAwait(false); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
