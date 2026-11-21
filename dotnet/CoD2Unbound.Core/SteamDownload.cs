using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public enum SteamPhase { Offer, Starting, Qr, Downloading, Checking, NotOwned, Stopped, Done }
public enum SteamSignal { Connecting, Qr, SignedIn, NotOwned, SignInFailed, Depot, File, Checking, Finished }
public enum SteamStopReason { None, User, NoResponse, SignInExpired, Stalled }

/// <summary>The process adapter emits only recognized events. QR data must stay in memory.</summary>
public sealed record SteamProcessEvent(SteamSignal Signal, string? Text = null, int DepotId = 0, double Percent = 0)
{
    public override string ToString() => Signal.ToString();
}

public sealed record SteamDownloadState(SteamPhase Phase, long DoneBytes, long TotalBytes, string CurrentFile = "",
    int? SecondsLeft = null, string? Qr = null, bool ReachingSteam = false, string? Title = null, string? Detail = null,
    string? Retry = null, string? ValidatedRoot = null, long DownloadedBytes = 0)
{
    public override string ToString() => Phase.ToString();
}

public sealed class SteamAttempt(string folder, SteamLanguage language, TimeProvider clock)
{
    private readonly SteamDownloadProgress progress = new(language);
    private readonly Queue<(long Time, long Bytes)> samples = new();
    private readonly long started = clock.GetTimestamp();
    private long lastActivity = clock.GetTimestamp();
    private long? firstCode;
    private bool sawWrites, signedIn, notOwned, signInFailed, finished;

    public SteamDownloadState State { get; private set; } =
        new(SteamPhase.Starting, 0, new SteamDownloadProgress(language).Total);

    public void Receive(SteamProcessEvent message)
    {
        lastActivity = clock.GetTimestamp();
        switch (message.Signal)
        {
            case SteamSignal.Connecting:
                State = State with { ReachingSteam = true };
                break;
            case SteamSignal.Qr when !signedIn && !string.IsNullOrEmpty(message.Text):
                firstCode ??= lastActivity;
                State = State with { Phase = SteamPhase.Qr, Qr = message.Text };
                break;
            case SteamSignal.SignedIn:
                signedIn = true;
                State = State with { Phase = SteamPhase.Downloading, Qr = null };
                break;
            case SteamSignal.NotOwned: notOwned = true; break;
            case SteamSignal.SignInFailed: signInFailed = true; break;
            case SteamSignal.Depot: progress.Depot(message.DepotId); break;
            case SteamSignal.File:
                progress.File(message.Percent);
                State = State with { CurrentFile = Relative(message.Text) };
                break;
            case SteamSignal.Checking:
                State = State with { CurrentFile = Relative(message.Text) };
                break;
            case SteamSignal.Finished: finished = true; break;
        }
        if (signedIn)
            State = State with { DoneBytes = progress.Done };
    }

    public SteamStopReason Tick(long written)
    {
        long now = clock.GetTimestamp();
        if (!signedIn)
        {
            if (firstCode is long code)
                return clock.GetElapsedTime(code, now).TotalSeconds > 300 ? SteamStopReason.SignInExpired : SteamStopReason.None;
            return clock.GetElapsedTime(started, now).TotalSeconds > 60 ? SteamStopReason.NoResponse : SteamStopReason.None;
        }

        long before = progress.Done;
        sawWrites |= written > 0;
        progress.Wrote(written);
        if (progress.Done != before)
            lastActivity = now;
        samples.Enqueue((now, progress.Done));
        while (clock.GetElapsedTime(samples.Peek().Time, now).TotalSeconds > 30)
            samples.Dequeue();

        var first = samples.Peek();
        double duration = clock.GetElapsedTime(first.Time, now).TotalSeconds;
        int? left = State.SecondsLeft;
        if (duration >= 5 && progress.Done > first.Bytes)
        {
            double rate = (progress.Done - first.Bytes) / duration;
            left = (int)Math.Min(int.MaxValue, (progress.Total - progress.Done) / rate);
        }
        State = State with { DoneBytes = progress.Done, SecondsLeft = left };
        return clock.GetElapsedTime(lastActivity, now).TotalSeconds > (sawWrites ? 120 : 900)
            ? SteamStopReason.Stalled : SteamStopReason.None;
    }

    public bool Exited(int status, SteamStopReason reason)
    {
        State = State with { Qr = null, SecondsLeft = null };
        if (notOwned)
            State = State with { Phase = SteamPhase.NotOwned };
        else if (!signedIn)
            Stop("The sign-in didn’t go through", reason switch
            {
                SteamStopReason.NoResponse => "Steam didn’t answer. Check your connection and try again.",
                SteamStopReason.SignInExpired => "The code wasn’t approved in time. Nothing was downloaded.",
                _ when signInFailed => "Steam didn’t accept the sign-in. Nothing was downloaded.",
                _ => "The connection to Steam closed before the sign-in finished. Nothing was downloaded."
            }, "Try Again");
        else if (reason != SteamStopReason.None)
            Interrupted(reason == SteamStopReason.User ? "You stopped it." : "The connection to Steam was lost.");
        else if (!finished || status != 0)
            Interrupted("The download tool stopped unexpectedly.");
        else
        {
            State = State with { Phase = SteamPhase.Checking };
            return true;
        }
        return false;
    }

    public void Checked(SteamFolderCheck check)
    {
        if (check.ValidatedRoot is { Length: > 0 } && check.Archives.IsComplete && check.HasLocalizedArchive)
        {
            progress.Finish();
            State = State with { Phase = SteamPhase.Done, DoneBytes = progress.Done, ValidatedRoot = check.ValidatedRoot,
                DownloadedBytes = Math.Max(0, check.Bytes) };
            return;
        }
        string missing = check.Archives.Missing.Length switch
        {
            0 => "The files for the language you chose are missing.",
            1 => $"{check.Archives.Missing[0]} is missing.",
            2 => $"{check.Archives.Missing[0]} and 1 more game archive are missing.",
            _ => $"{check.Archives.Missing[0]} and {check.Archives.Missing.Length - 1} more game archives are missing."
        };
        Stop("Steam finished, but the game isn’t complete", missing + " Resume checks what is there and fetches the rest.", "Resume");
    }

    internal void CouldNotStart() =>
        Stop("The download couldn’t start", "The download couldn’t start. Check the folder and try again.", "Try Again");

    private string Relative(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        string path = text.Replace('\\', '/');
        string prefix = folder.Replace('\\', '/').TrimEnd('/') + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
    }

    private void Interrupted(string detail) =>
        Stop($"The download stopped at {SteamDownloadProgress.Gigabytes(progress.Done)} of {SteamDownloadProgress.Gigabytes(progress.Total)} GB",
            $"{detail} What is downloaded is kept in {folder}.", "Resume");

    private void Stop(string title, string detail, string retry) =>
        State = State with { Phase = SteamPhase.Stopped, Qr = null, SecondsLeft = null, Title = title, Detail = detail, Retry = retry };
}

public sealed record SteamFolderCheck(string? ValidatedRoot, GameArchiveSurvey Archives, bool HasLocalizedArchive, long Bytes);
public sealed record SteamDownloadNote(string Folder, string Language);

public interface ISteamDownloadFiles
{
    // Observe the downloader's own marker and survey readable regular files. Publish completion by
    // removing only this folder's downloader marker, after the adapter verifies the same destination.
    bool HasUnfinishedMarker(string folder);
    ValueTask<SteamFolderCheck> CheckAsync(string folder, SteamLanguage language, CancellationToken cancellationToken);
    ValueTask CompleteAsync(string folder, CancellationToken cancellationToken);
}

public interface ISteamDownloadProcess
{
    // Use a private sign-in home for this attempt. Failed starts and disposal must erase it.
    ValueTask<ISteamDownloadRun> StartAsync(string folder, SteamLanguage language, CancellationToken cancellationToken);
}

public interface ISteamDownloadRun : IAsyncDisposable
{
    long BytesWritten { get; }
    Task<int> Exited { get; }
    IAsyncEnumerable<SteamProcessEvent> ReadEventsAsync(CancellationToken cancellationToken);
    // This stops the exact process and waits until it exits. The adapter bounds graceful shutdown.
    ValueTask StopAsync();
}

public sealed class SteamDownload(ISteamDownloadProcess process, ISteamDownloadFiles files, ISettingsFolder storage, TimeProvider clock)
{
    private const string NoteFile = "steam-download.json";
    private int running;

    public async ValueTask<SteamDownloadNote?> ReadRecoveryAsync(CancellationToken cancellationToken = default)
    {
        if (await storage.ReadAsync(NoteFile, cancellationToken).ConfigureAwait(false) is not { Length: <= 8192 } data)
            return null;
        try
        {
            var note = JsonSerializer.Deserialize(data.AsSpan(), SteamJson.Default.SteamDownloadNote);
            if (note is { Folder.Length: > 0 } && files.HasUnfinishedMarker(note.Folder))
                return note;
        }
        catch (JsonException) { return null; }
        return null;
    }

    public async Task<SteamDownloadState> RunAsync(string folder, SteamLanguage language, long? freeBytes,
        IProgress<SteamDownloadState> updates, CancellationToken stop = default, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException("A Steam download is already running.");

        try
        {
            var attempt = new SteamAttempt(folder, language, clock);
            if (freeBytes < new SteamDownloadProgress(language).RoomNeeded)
                return attempt.State with { Phase = SteamPhase.Offer };

            var note = new SteamDownloadNote(folder, language.ToString().ToLowerInvariant());
            await storage.WriteAsync(NoteFile, [.. JsonSerializer.SerializeToUtf8Bytes(note, SteamJson.Default.SteamDownloadNote)],
                cancellationToken).ConfigureAwait(false);
            updates.Report(attempt.State);
            ISteamDownloadRun run;
            try { run = await process.StartAsync(folder, language, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                attempt.CouldNotStart();
                updates.Report(attempt.State);
                return attempt.State;
            }

            attempt = new(folder, language, clock);
            SteamStopReason reason = SteamStopReason.None;
            int status;
            await using (run.ConfigureAwait(false))
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
                await using var events = run.ReadEventsAsync(read.Token).GetAsyncEnumerator(read.Token);
                Task<bool> next = events.MoveNextAsync().AsTask();
                Task<bool> tick = timer.WaitForNextTickAsync(read.Token).AsTask();
                bool drained = false;
                try
                {
                    while (!read.IsCancellationRequested)
                    {
                        Task completed = await Task.WhenAny(next, tick).ConfigureAwait(false);
                        if (completed == next)
                        {
                            if (!await next.ConfigureAwait(false))
                            {
                                drained = true;
                                break;
                            }
                            attempt.Receive(events.Current);
                            next = events.MoveNextAsync().AsTask();
                        }
                        else
                        {
                            await tick.ConfigureAwait(false);
                            reason = attempt.Tick(run.BytesWritten);
                            if (reason != SteamStopReason.None)
                                break;
                            tick = timer.WaitForNextTickAsync(read.Token).AsTask();
                        }
                        updates.Report(attempt.State);
                    }
                    if (stop.IsCancellationRequested)
                        reason = SteamStopReason.User;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    reason = SteamStopReason.User;
                }
                finally
                {
                    read.Cancel();
                    if (!drained)
                        await run.StopAsync().ConfigureAwait(false);
                    try { await next.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (read.IsCancellationRequested) { }
                }

                cancellationToken.ThrowIfCancellationRequested();
                status = await run.Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (attempt.Exited(status, reason))
            {
                updates.Report(attempt.State);
                var check = await files.CheckAsync(folder, language, cancellationToken).ConfigureAwait(false);
                if (check is { ValidatedRoot.Length: > 0, Archives.IsComplete: true, HasLocalizedArchive: true })
                {
                    await files.CompleteAsync(folder, cancellationToken).ConfigureAwait(false);
                    await storage.DeleteAsync(NoteFile, cancellationToken).ConfigureAwait(false);
                }
                attempt.Checked(check);
            }
            updates.Report(attempt.State);
            return attempt.State;
        }
        finally { Volatile.Write(ref running, 0); }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamDownloadNote))]
internal sealed partial class SteamJson : JsonSerializerContext;
