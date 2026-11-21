using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public enum ZpamDownloadPhase { Unknown, LookingUp, Missing, Getting, Ready, Failed }
public sealed record ZpamDownloadState(ZpamDownloadPhase Phase, ZpamRelease? Release = null,
    string? Version = null, long Received = 0, string? Problem = null);

public interface IZpamDownloadSource
{
    // List only a bounded response from the project feed. Download to a private, size-bounded,
    // seekable stream that cleans temporary data on disposal. Refuse untrusted redirects.
    ValueTask<ImmutableArray<byte>> ListAsync(CancellationToken cancellationToken);
    ValueTask<Stream> DownloadAsync(ZpamRelease release, Action<long> progress, CancellationToken cancellationToken);
    string? InstalledVersion(ZpamLine line);
}

public sealed class ZpamDownload(IZpamDownloadSource source, IZpamPackageStorage storage)
{
    private readonly Lock gate = new();
    private readonly Dictionary<ZpamLine, ZpamDownloadState> states = new();
    private readonly Dictionary<ZpamLine, CancellationTokenSource> attempts = new();

    public ZpamDownloadState State(ZpamLine line)
    {
        lock (gate)
            return states.GetValueOrDefault(line) ?? new(ZpamDownloadPhase.Unknown);
    }

    public async Task LookUpAsync(ZpamLine line, CancellationToken cancellationToken = default)
    {
        ZpamDownloadState previous;
        lock (gate)
        {
            previous = State(line);
            if (previous.Phase is ZpamDownloadPhase.LookingUp or ZpamDownloadPhase.Getting)
                return;
            states[line] = new(ZpamDownloadPhase.LookingUp);
        }
        ZpamDownloadState result;
        try
        {
            if (source.InstalledVersion(line) is { } version)
                result = new(ZpamDownloadPhase.Ready, Version: version);
            else
            {
                var listing = await source.ListAsync(cancellationToken).ConfigureAwait(false);
                var release = listing.Length <= 1_000_000 ? ZpamRelease.Newest(line, listing.AsSpan()) : null;
                result = release is null
                    ? new(ZpamDownloadPhase.Failed, Problem: "The zPAM project’s releases don’t list a download with a checksum right now.")
                    : new(ZpamDownloadPhase.Missing, release);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate)
                states[line] = previous;
            throw;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        {
            result = new(ZpamDownloadPhase.Failed,
                Problem: "The zPAM project’s releases on GitHub couldn’t be reached. Check your connection and try again.");
        }
        lock (gate)
            states[line] = result;
    }

    public async Task GetAsync(ZpamLine line, CancellationToken cancellationToken = default)
    {
        ZpamRelease release;
        CancellationTokenSource attempt;
        lock (gate)
        {
            if (State(line) is not { Phase: ZpamDownloadPhase.Missing, Release: { } chosen })
                return;
            release = chosen;
            attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempts[line] = attempt;
            states[line] = new(ZpamDownloadPhase.Getting, release);
        }
        using (attempt)
        {
            ZpamDownloadState result = new(ZpamDownloadPhase.Failed, Problem: "zPAM couldn’t be downloaded.");
            try
            {
                await using var archive = await source.DownloadAsync(release, Report, attempt.Token).ConfigureAwait(false);
                await ZpamPackage.InstallVerifiedAsync(release, archive, storage, attempt.Token).ConfigureAwait(false);
                result = new(ZpamDownloadPhase.Ready, Version: release.Version);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            {
                result = new(ZpamDownloadPhase.Missing, release);
            }
            catch (ZpamPackageException error)
            {
                result = new(ZpamDownloadPhase.Failed, Problem: error.Message);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
            {
                result = new(ZpamDownloadPhase.Failed, Problem: "zPAM couldn’t be downloaded.");
            }
            finally
            {
                lock (gate)
                {
                    attempts.Remove(line);
                    states[line] = result;
                }
            }
        }

        void Report(long received)
        {
            lock (gate)
            {
                if (ReferenceEquals(attempts.GetValueOrDefault(line), attempt))
                {
                    var current = states[line];
                    states[line] = current with { Received = Math.Clamp(received, current.Received, release.Bytes) };
                }
            }
        }
    }

    public void Cancel(ZpamLine line)
    {
        lock (gate)
        {
            if (attempts.TryGetValue(line, out var attempt))
                _ = attempt.CancelAsync();
        }
    }
}
