using System.Collections.Immutable;
using System.Text.Json;

namespace CoD2Unbound.Core;

public enum UpdatePhase { Idle, Checking, Offered, Downloading, Verifying, Ready, Installing, Installed }
public enum UpdateFailureStage { Check, Install }
public sealed record LauncherUpdateState(UpdatePhase Phase = UpdatePhase.Idle, bool WasChecked = false, string? Message = null,
    UpdateFailureStage? FailedStage = null, string? AvailableVersion = null, ReleaseOffer? Offer = null, Uri? ReleaseUrl = null,
    ImmutableArray<ReleaseNoteItem> Notes = default, int? ProgressPercent = null)
{
    public bool CanCancel => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.Ready;
    public bool IsBusy => CanCancel || Phase == UpdatePhase.Installing;
}
public sealed record LauncherUpdateCheck(LauncherUpdateState State, LauncherLibrary Library, string? NotifyVersion);
public sealed record LauncherActivity(bool GameRunning, bool ServersRunning);

public interface ILauncherReleaseSource
{
    // The caller supplies a whole-response deadline. Bound release bytes to 1 MB and news to 64 KiB.
    ValueTask<ImmutableArray<byte>> ReleasesAsync(CancellationToken cancellationToken);
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
    Func<string, string, bool>? isNewer = null)
{
    private static readonly Uri ReleasesPage = new("https://github.com/cod2unbound/cod2-unbound/releases");
    private LauncherUpdateState state = new(Notes: []);
    private int busy;
    public LauncherUpdateState State => Volatile.Read(ref state);
    public event Action<LauncherUpdateState>? Changed;

    public void RestoreInstallResult(bool installed) =>
        Publish(new(Phase: installed ? UpdatePhase.Installed : UpdatePhase.Idle, WasChecked: true,
            Message: installed ? $"Updated to version {currentVersion}." : "The update did not finish. Try again or open the update logs.",
            FailedStage: installed ? null : UpdateFailureStage.Install, ReleaseUrl: installed ? null : ReleasesPage, Notes: []));

    public static bool AutoCheckDue(DateTimeOffset now, DateTimeOffset? attempted, DateTimeOffset? checkedAt, bool gameRunning) =>
        !gameRunning && now >= NextDue(attempted, checkedAt);
    public static TimeSpan WaitForAutomaticCheck(DateTimeOffset now, DateTimeOffset? attempted, DateTimeOffset? checkedAt) =>
        TimeSpan.FromSeconds(Math.Clamp((NextDue(attempted, checkedAt) - now).TotalSeconds, 300, 3600));

    public async Task<LauncherUpdateCheck> CheckAsync(LauncherLibrary library, bool automatic = false,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref busy, 1) != 0)
            return new(State, library, null);
        var before = State;
        Publish(before with { Phase = UpdatePhase.Checking, WasChecked = before.WasChecked || !automatic,
            Message = "Checking GitHub Releases…", FailedStage = automatic ? before.FailedStage : null });
        try
        {
            var releaseBytes = await source.ReleasesAsync(cancellationToken).ConfigureAwait(false);
            var release = ReleaseNotes.Parse(releaseBytes.AsSpan(), timeZone: timeZone);
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
                    Publish(new(WasChecked: true, Message: "This release did not provide a valid Windows Setup. Open the download page for details.",
                        FailedStage: UpdateFailureStage.Check, ReleaseUrl: ReleasesPage, Notes: []));
                    return new(State, library, null);
                }
                Publish(new(Phase: UpdatePhase.Offered, WasChecked: true, Message: $"Version {version} is available.",
                    AvailableVersion: version, ReleaseUrl: ReleasesPage, Offer: offer, Notes: release.LatestNotes));
                return new(State, library, automatic && before.AvailableVersion != version ? version : null);
            }
            Publish(new(WasChecked: true, Message: $"You’re up to date · {currentVersion}", Notes: []));
            return new(State, library, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(before);
            return new(State, library, null);
        }
        catch (Exception error) when (IsFetchError(error, cancellationToken))
        {
            Publish(automatic ? before : State with { Phase = UpdatePhase.Idle, FailedStage = UpdateFailureStage.Check,
                Message = "The update server didn’t answer. Check your connection and try again. Your current version keeps working." });
            return new(State, library, null);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    public async Task<LauncherUpdateState> InstallAsync(IWindowsLauncherUpdate adapter, Func<LauncherActivity> observe,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref busy, 1) != 0)
            return State;
        try
        {
            if (State.Offer is not { } offer || !(isNewer ?? ReleaseNotes.IsNewer)(offer.Version, currentVersion))
                return State;
            var use = observe();
            if (ReleasePackage.Block(use.GameRunning, use.ServersRunning) is { } blocked)
                return Refuse(blocked);
            adapter.CheckInstallLocation();
            Publish(State with { Phase = UpdatePhase.Downloading, FailedStage = null, Message = $"Downloading version {offer.Version}…", ProgressPercent = 0 });
            await using var staged = await adapter.DownloadAsync(offer, bytes =>
            {
                int percent = (int)(bytes * 100 / offer.Archive.Size);
                if (percent != State.ProgressPercent)
                    Publish(State with { ProgressPercent = percent });
            }, cancellationToken).ConfigureAwait(false);
            Publish(State with { Phase = UpdatePhase.Verifying, Message = "Verifying the download…", ProgressPercent = null });
            await staged.VerifyAsync(cancellationToken).ConfigureAwait(false);
            Publish(State with { Phase = UpdatePhase.Ready, Message = $"Version {offer.Version} is ready to install." });
            await using var lease = await adapter.AcquireInstallLeaseAsync(cancellationToken).ConfigureAwait(false);
            if (ReleasePackage.Block(lease.Activity.GameRunning, lease.Activity.ServersRunning) is { } changed)
                return Refuse(changed);

            cancellationToken.ThrowIfCancellationRequested();
            Publish(State with { Phase = UpdatePhase.Installing, Message = $"Installing {offer.Version}…" });
            await staged.InstallAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(State with { Phase = UpdatePhase.Offered, Message = "Download cancelled. You can try again.", ProgressPercent = null });
        }
        catch (Exception error) when (IsFetchError(error, cancellationToken))
        {
            Publish(State with { Phase = UpdatePhase.Idle, FailedStage = UpdateFailureStage.Install, ProgressPercent = null,
                Message = error is LauncherUpdateException ? error.Message : "The update could not be completed. Try again." });
        }
        finally { Volatile.Write(ref busy, 0); }
        return State;
    }

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

    private LauncherUpdateState Refuse(UpdateBlock reason)
    {
        string problem = reason == UpdateBlock.GameRunning ? "Close the game to update." :
            "Stop your hosted servers to update.";
        Publish(State with { Phase = UpdatePhase.Idle, FailedStage = UpdateFailureStage.Install, Message = problem });
        return State;
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
