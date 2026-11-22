using System.Collections.Immutable;
using System.Text.Json;

namespace CoD2Unbound.Core;

public enum UpdatePhase { Idle, Checking, Offered, Downloading, Verifying, Ready, Installing, Installed }
public enum UpdateFailureStage { Check, Install }
public enum UpdateIssue { None, Refused, Interrupted, ProtectedLocation, SetupFailed, Unexpected }
public sealed class UpdateHeldException(UpdateBlock reason) : IOException
{
    public UpdateBlock Reason { get; } = reason;
}
public sealed record LauncherUpdateState(UpdatePhase Phase = UpdatePhase.Idle, bool WasChecked = false, string? Message = null,
    UpdateFailureStage? FailedStage = null, string? AvailableVersion = null, ReleaseOffer? Offer = null, Uri? ReleaseUrl = null,
    ImmutableArray<ReleaseNoteItem> Notes = default, int? ProgressPercent = null,
    long DownloadedBytes = 0, DateTimeOffset? CheckedAt = null, UpdateIssue Issue = UpdateIssue.None,
    UpdateBlock? Hold = null)
{
    public bool CanCancel => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Verifying;
    public bool IsBusy => CanCancel || Phase == UpdatePhase.Installing;
}
public sealed record LauncherUpdateCheck(LauncherUpdateState State, LauncherLibrary Library, string? NotifyVersion);
public sealed record LauncherActivity(bool GameRunning, bool ServersRunning);

public interface ILauncherReleaseSource
{
    ValueTask<ReleaseFeed> ReleasesAsync(TimeZoneInfo timeZone, CancellationToken cancellationToken);
    ValueTask<ImmutableArray<byte>?> NewsAsync(CancellationToken cancellationToken);
    // News null means keep the prior copy. A missing project file is an explicit empty feed.
}

public interface IWindowsLauncherUpdate
{
    // Own private temporary files until disposal. Verify the expected checksum and the pinned
    // signing publisher before publication. Encapsulate staging, trust checks and installer handoff.
    void CheckInstallLocation();
    ValueTask<IStagedLauncherUpdate> DownloadAsync(ReleaseOffer offer, Action<long> progress, CancellationToken cancellationToken);
    // Takes the same cross-process interlock as game launch and Hosting start, then checks live processes.
    ValueTask<IUpdateInstallLease> AcquireInstallLeaseAsync(CancellationToken cancellationToken);
}
public interface IStagedLauncherUpdate : IAsyncDisposable
{
    ValueTask VerifyAsync(CancellationToken cancellationToken);
    ValueTask InstallAsync(CancellationToken cancellationToken);
}
public interface IUpdateInstallLease : IAsyncDisposable
{
    LauncherActivity Activity { get; }
}

public sealed class LauncherUpdates(ILauncherReleaseSource source, TimeProvider clock, string currentVersion,
    Func<string, IEnumerable<ReleaseAsset>, ReleaseOffer?> selectOffer, TimeZoneInfo timeZone,
    Func<string, string, bool>? isNewer = null, Action<string>? diagnostic = null,
    Func<string, IEnumerable<ReleaseAsset>, string>? describeRefusal = null)
{
    private static readonly Uri ReleasesPage = new("https://github.com/cod2unbound/cod2-unbound/releases");
    private LauncherUpdateState state = new(Notes: []);
    private int busy;
    private IStagedLauncherUpdate? verified;
    public LauncherUpdateState State => Volatile.Read(ref state);
    public event Action<LauncherUpdateState>? Changed;

    public void RestoreInstallResult(bool installed, DateTimeOffset? checkedAt = null) =>
        Publish(new(Phase: installed ? UpdatePhase.Installed : UpdatePhase.Idle, WasChecked: true,
            FailedStage: installed ? null : UpdateFailureStage.Install, ReleaseUrl: ReleasesPage, Notes: [],
            CheckedAt: checkedAt, Issue: installed ? UpdateIssue.None : UpdateIssue.SetupFailed));

    public void RefreshInstallBlock(LauncherActivity activity)
    {
        var snapshot = State;
        var hold = ReleasePackage.Block(activity.GameRunning, activity.ServersRunning);
        if (snapshot.Phase == UpdatePhase.Ready && hold != snapshot.Hold && Volatile.Read(ref busy) == 0)
            Publish(snapshot with { Hold = hold });
    }

    public async ValueTask DiscardVerifiedAsync()
    {
        var staged = Interlocked.Exchange(ref verified, null);
        if (staged is not null)
            await staged.DisposeAsync().ConfigureAwait(false);
    }

    public static bool AutoCheckDue(DateTimeOffset now, DateTimeOffset? attempted, DateTimeOffset? checkedAt, bool gameRunning) =>
        !gameRunning && now >= NextDue(attempted, checkedAt);
    public static TimeSpan WaitForAutomaticCheck(DateTimeOffset now, DateTimeOffset? attempted, DateTimeOffset? checkedAt) =>
        TimeSpan.FromSeconds(Math.Clamp((NextDue(attempted, checkedAt) - now).TotalSeconds, 300, 3600));

    public async Task<LauncherUpdateCheck> CheckAsync(LauncherLibrary library, bool automatic = false,
        CancellationToken cancellationToken = default)
    {
        if (State.Phase is UpdatePhase.Ready or UpdatePhase.Installing || Interlocked.Exchange(ref busy, 1) != 0)
            return new(State, library, null);
        var before = State;
        Publish(before with { Phase = UpdatePhase.Checking, WasChecked = before.WasChecked || !automatic,
            Message = null, Issue = UpdateIssue.None, FailedStage = null });
        try
        {
            var release = await source.ReleasesAsync(timeZone, cancellationToken).ConfigureAwait(false);
            NewsFeed? news = null;
            try
            {
                if (await source.NewsAsync(cancellationToken).ConfigureAwait(false) is { } bytes)
                    news = bytes.IsEmpty ? new([], null) : NewsFeed.Parse(bytes.AsMemory(), clock.GetUtcNow(), timeZone);
            }
            catch (Exception error) when (IsFetchError(error, cancellationToken)) { }
            cancellationToken.ThrowIfCancellationRequested();
            library = library with { Dispatches = release.Dispatches.IsEmpty ? library.Dispatches : release.Dispatches,
                News = news?.Items ?? library.News, NewsNext = news is null ? library.NewsNext : news.Next,
                DispatchesCheckedAt = clock.GetUtcNow() };
            if (release.LatestVersion is { } version && (isNewer ?? ReleaseNotes.IsNewer)(version, currentVersion))
            {
                var offer = selectOffer(version, release.LatestAssets);
                if (offer is null)
                {
                    diagnostic?.Invoke(describeRefusal?.Invoke(version, release.LatestAssets) ?? "The release did not provide a valid Windows Setup.");
                    Publish(new(WasChecked: true, AvailableVersion: version, Issue: UpdateIssue.Refused,
                        FailedStage: UpdateFailureStage.Check, ReleaseUrl: release.LatestUrl ?? ReleasesPage,
                        Notes: [], CheckedAt: library.DispatchesCheckedAt));
                    return new(State, library, null);
                }
                Publish(new(Phase: UpdatePhase.Offered, WasChecked: true, AvailableVersion: version,
                    ReleaseUrl: release.LatestUrl, Offer: offer with { ListedByGitHub = release.ListedByGitHub },
                    Notes: release.LatestNotes, CheckedAt: library.DispatchesCheckedAt));
                return new(State, library, automatic && before.AvailableVersion != version ? version : null);
            }
            Publish(new(WasChecked: true, Notes: [], CheckedAt: library.DispatchesCheckedAt));
            return new(State, library, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(before);
            return new(State, library, null);
        }
        catch (Exception error) when (IsFetchError(error, cancellationToken))
        {
            LogError(error);
            Publish(automatic ? before : State with { Phase = UpdatePhase.Idle, FailedStage = UpdateFailureStage.Check,
                Issue = error is LauncherUpdateException ? UpdateIssue.Refused : UpdateIssue.Unexpected,
                Message = "The update server didn’t answer. Check your connection and try again. Your current version keeps working." });
            return new(State, library, null);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    public async Task<LauncherUpdateState> InstallAsync(IWindowsLauncherUpdate adapter,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref busy, 1) != 0)
            return State;
        bool keepVerified = false;
        try
        {
            if (State.Offer is not { } offer || !(isNewer ?? ReleaseNotes.IsNewer)(offer.Version, currentVersion))
                return State;
            adapter.CheckInstallLocation();
            if (verified is null)
            {
                Publish(State with { Phase = UpdatePhase.Downloading, FailedStage = null, Message = null,
                    Issue = UpdateIssue.None, ProgressPercent = 0, DownloadedBytes = 0 });
                verified = await adapter.DownloadAsync(offer, bytes =>
                {
                    int percent = (int)(bytes * 100 / offer.Archive.Size);
                    if (percent != State.ProgressPercent)
                        Publish(State with { ProgressPercent = percent, DownloadedBytes = bytes });
                }, cancellationToken).ConfigureAwait(false);
                Publish(State with { Phase = UpdatePhase.Verifying, ProgressPercent = null, DownloadedBytes = offer.Archive.Size });
                await verified.VerifyAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var lease = await adapter.AcquireInstallLeaseAsync(cancellationToken).ConfigureAwait(false);
            if (ReleasePackage.Block(lease.Activity.GameRunning, lease.Activity.ServersRunning) is { } block)
                throw new UpdateHeldException(block);

            cancellationToken.ThrowIfCancellationRequested();
            Publish(State with { Phase = UpdatePhase.Installing, Hold = null });
            await verified.InstallAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (UpdateHeldException error)
        {
            keepVerified = true;
            diagnostic?.Invoke(error.Reason == UpdateBlock.GameRunning ? "Setup handoff held for the game." : "Setup handoff held for hosted servers.");
            Publish(State with { Phase = UpdatePhase.Ready, Hold = error.Reason });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(State with { Phase = UpdatePhase.Offered, Message = "Download cancelled.", ProgressPercent = null, Hold = null });
        }
        catch (Exception error)
        {
            LogError(error);
            Publish(State with { Phase = UpdatePhase.Idle, FailedStage = UpdateFailureStage.Install, ProgressPercent = null, Hold = null,
                Issue = error is LauncherUpdateException refusal ? refusal.Issue : UpdateIssue.Unexpected });
        }
        finally
        {
            try
            {
                if (!keepVerified)
                    await DiscardVerifiedAsync().ConfigureAwait(false);
            }
            finally { Volatile.Write(ref busy, 0); }
        }
        return State;
    }

    private void LogError(Exception error) => diagnostic?.Invoke(error is LauncherUpdateException known
        ? $"{known.Message} ({(known.InnerException ?? known).HResult:X8})"
        : $"Update operation failed ({error.GetType().Name}, {error.HResult:X8}).");

    public async Task AutoCheckAsync(Func<DateTimeOffset?> attempted, Func<LauncherLibrary> library, Action<DateTimeOffset> recordAttempt,
        Func<bool> gameRunning, Action<LauncherUpdateCheck> publish, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), clock, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var saved = library();
            var now = clock.GetUtcNow();
            var lastAttempt = attempted();
            var wait = WaitForAutomaticCheck(now, lastAttempt, saved.DispatchesCheckedAt);
            if (Volatile.Read(ref busy) == 0 && AutoCheckDue(now, lastAttempt, saved.DispatchesCheckedAt, gameRunning()))
            {
                recordAttempt(now);
                publish(await CheckAsync(saved, automatic: true, cancellationToken).ConfigureAwait(false));
            }
            await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Publish(LauncherUpdateState next)
    {
        Volatile.Write(ref state, next);
        Changed?.Invoke(next);
    }

    private static DateTimeOffset NextDue(DateTimeOffset? attempt, DateTimeOffset? check)
    {
        var last = attempt.GetValueOrDefault() > check.GetValueOrDefault() ? attempt.GetValueOrDefault() : check.GetValueOrDefault();
        return last > DateTimeOffset.MaxValue.AddDays(-1) ? DateTimeOffset.MaxValue : last.AddDays(1);
    }

    private static bool IsFetchError(Exception error, CancellationToken cancellationToken) =>
        error is IOException or HttpRequestException or JsonException or UnauthorizedAccessException ||
        (error is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}

public sealed class LauncherSettingsSession(GameSettings initial, GameSettingsStorage storage, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1);
    private GameSettings working = initial;
    private GameSettings saved = initial;
    private long edited;
    public GameSettings Working => Volatile.Read(ref working);
    public GameSettings Saved => Volatile.Read(ref saved);

    public void Replace(GameSettings value)
    {
        Volatile.Write(ref edited, clock.GetTimestamp());
        Volatile.Write(ref working, value);
    }

    public void Reset(GameSettings defaults) => Replace(Working.Reset(defaults));

    public async Task SaveAfterQuietAsync(CancellationToken cancellationToken = default)
    {
        var quiet = TimeSpan.FromMilliseconds(250);
        while (quiet - clock.GetElapsedTime(Volatile.Read(ref edited)) is { Ticks: > 0 } remaining)
            await Task.Delay(remaining, clock, cancellationToken).ConfigureAwait(false);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidate = Working;
            if (Saved == candidate)
                return;
            await storage.WriteAsync(candidate, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref saved, candidate);
        }
        finally { gate.Release(); }
    }
}

public sealed record AdvancedSettingsReview(GameSettings? Candidate, string? Problem, ImmutableArray<int> LoadoutOwnedLines);

public static class AdvancedSettings
{
    public static AdvancedSettingsReview Inspect(GameSettings current, string draft, bool usesLoadouts,
        Func<GameSettings, string?> validateLargestStart)
    {
        var ignored = ImmutableArray.CreateBuilder<int>();
        string[] lines = draft.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var pair = GameSettings.AdvancedPair(line);
            try { _ = (current with { Advanced = line }).Dvars(); }
            catch (LaunchPlanException)
            {
                string problem = pair?.Name.Equals("r_mode", StringComparison.OrdinalIgnoreCase) == true
                    ? usesLoadouts ? "The resolution belongs to each loadout. Change it in Configs." : "Choose the resolution in Settings › Display."
                    : $"Line {index + 1} can’t be used. Enter one setting and its value, without quotes or semicolons. Paths, passwords and your CD key are set by the app.";
                return new(null, problem, ignored.ToImmutable());
            }
            if (usesLoadouts && pair is { } value && !GameSettings.MachineDvarNames.Contains(value.Name))
                ignored.Add(index + 1);
        }
        var candidate = current with { Advanced = draft };
        try { _ = candidate.Dvars(); }
        catch (LaunchPlanException) { return new(null, "These options are too long. Remove some lines and try again.", ignored.ToImmutable()); }
        string? failure = validateLargestStart(candidate);
        return new(failure is null ? candidate : null, failure, ignored.ToImmutable());
    }
}
