using System.Collections.Immutable;
using System.Globalization;

namespace CoD2Unbound.Core;

public enum ServerScope { All, Favorites, Recent, Hosting }
public enum ServerSort { Players, Name, Ping }
public enum ServerEditionFilter { All, Cod2x, Stock }

public sealed record ServerFilters(ServerEditionFilter Version = ServerEditionFilter.All, string? Mode = null,
    int? MaxPing = null, bool HideEmpty = false, bool HideFull = false, bool HidePassworded = false)
{
    public static ImmutableArray<int> PingLimits { get; } = [50, 100, 150, 250];
    public int Count => new[] { Version != ServerEditionFilter.All, Mode is not null, MaxPing is not null,
        HideEmpty, HideFull, HidePassworded }.Count(value => value);

    public bool Admits(GameServer server) =>
        (Version == ServerEditionFilter.All || (Version == ServerEditionFilter.Cod2x) == server.IsCod2x) &&
        (Mode is null || server.GameType.ToLowerInvariant() == Mode) &&
        (MaxPing is null || server.Ping < MaxPing) && (!HideEmpty || server.PlayerCount != 0) &&
        (!HideFull || ServerFacts.From(server).Occupancy != ServerOccupancy.Full) && (!HidePassworded || !server.PasswordRequired);
}

public sealed record ServerRefreshState(ImmutableArray<GameServer> Servers, bool Refreshing, int Asked,
    int Finished, int Answered, bool MastersDidNotAnswer, string? Message = null);

public sealed class ServerBrowser(ServerQueryClient query, TimeProvider clock)
{
    private int active;
    public bool ShouldRefreshOnOpen(bool enabled, LauncherLibrary library) =>
        enabled && Volatile.Read(ref active) == 0 && (library.CachedAt is not { } time || clock.GetUtcNow() - time >= TimeSpan.FromMinutes(2));

    public async Task<LauncherLibrary> RefreshAsync(LauncherLibrary library, ImmutableArray<GameServer> current,
        IProgress<ServerRefreshState> progress, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref active, 1) != 0)
            return library;
        var earlier = current.IsEmpty ? library.Cache : current;
        bool masters = false;
        int asked = 0, finished = 0;
        var fresh = new List<GameServer>();
        try
        {
            progress.Report(new(earlier, true, 0, 0, 0, false));
            var found = await query.MasterAddressesAsync(cancellationToken).ConfigureAwait(false);
            masters = !found.IsEmpty;
            var addresses = found.Concat(library.Favorites).Concat(library.Recent)
                .Select(TryAddress).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            asked = addresses.Length;
            foreach (var batch in addresses.Chunk(6))
            {
                var pending = new List<Task<GameServer?>>();
                try
                {
                    foreach (string address in batch)
                    {
                        pending.Add(query.ServerAsync(address, cancellationToken));
                        await Task.Delay(TimeSpan.FromMilliseconds(40), clock, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                while (pending.Count > 0)
                {
                    var complete = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(complete);
                    try
                    {
                        if (await complete.ConfigureAwait(false) is { } result)
                            fresh.Add(result);
                        finished++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                }
                progress.Report(new(Result(), true, asked, finished, fresh.Count, !masters));
                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            if (!cancellationToken.IsCancellationRequested || fresh.Count > 0)
                library = library with { Cache = Result(), CachedAt = masters ? clock.GetUtcNow() : library.CachedAt };
            return library;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return library; }
        finally
        {
            Volatile.Write(ref active, 0);
            string count = FormattableString.Invariant($"{fresh.Count} {(fresh.Count == 1 ? "server" : "servers")}");
            string message = cancellationToken.IsCancellationRequested ? $"Refresh stopped. {count} answered." :
                masters ? $"{count} answered." : "The master servers didn’t answer.";
            progress.Report(new(cancellationToken.IsCancellationRequested && fresh.Count == 0 ? earlier : Result(),
                false, asked, finished, fresh.Count, !masters, message));
        }

        ImmutableArray<GameServer> Result()
        {
            var answered = fresh.Select(item => item.Address).ToHashSet(StringComparer.Ordinal);
            return masters ? [.. fresh] : [.. fresh.Concat(earlier.Where(item => !answered.Contains(item.Address)))];
        }
    }

    public static ImmutableArray<GameServer> Visible(LauncherLibrary library, ImmutableArray<GameServer> current,
        ServerScope scope, ServerFilters filters, string search, ServerSort sort, bool ascending, CultureInfo? culture = null)
    {
        var byAddress = current.Concat(library.Cache).ToLookup(item => item.Address, StringComparer.Ordinal);
        IEnumerable<GameServer> selected = scope switch
        {
            ServerScope.All => current,
            ServerScope.Favorites => library.Favorites.Order(StringComparer.Ordinal).Select(address => byAddress[address].FirstOrDefault()).OfType<GameServer>(),
            ServerScope.Recent => library.Recent.Take(30).Select(address => byAddress[address].FirstOrDefault()).OfType<GameServer>(),
            _ => []
        };
        string value = search.Trim();
        var compare = (culture ?? CultureInfo.CurrentCulture).CompareInfo;
        selected = selected.Where(server => filters.Admits(server) &&
            (value.Length == 0 || compare.IndexOf(string.Join(' ', QuakeColors.Plain(server.Name), server.Map,
                MapCatalog.DisplayName(server.Map), server.Mod, server.Address), value, CompareOptions.IgnoreCase) >= 0));
        return [.. selected.Order(Comparer<GameServer>.Create((left, right) => ascending ? Compare(left, right) : Compare(right, left)))];

        int Compare(GameServer left, GameServer right)
        {
            int order = sort switch
            {
                ServerSort.Players => left.PlayerCount.CompareTo(right.PlayerCount),
                ServerSort.Ping => left.Ping.CompareTo(right.Ping),
                _ => compare.Compare(QuakeColors.Plain(left.Name), QuakeColors.Plain(right.Name),
                    CompareOptions.IgnoreCase | CompareOptions.NumericOrdering)
            };
            return order == 0 ? StringComparer.Ordinal.Compare(left.Address, right.Address) : order;
        }
    }

    public static string? TryAddress(string address)
    {
        try { return LaunchLink.CanonicalAddress(address); }
        catch (FormatException) { return null; }
    }
}

public sealed record ServerAddress(string Address, string? Password)
{
    public override string ToString() => Address;

    public static ServerAddress? Parse(string input)
    {
        string text = input.Trim();
        foreach (string scheme in new[] { "cod2x://", "cod2://" })
        {
            if (!text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                continue;
            text = text[scheme.Length..];
            if (LaunchLink.TryParse("cod2x://" + text, out var link))
                return new(LaunchLink.CanonicalAddress(link.Address), link.Password.Length > 0 ? link.Password : null);
            break;
        }
        text = Uri.UnescapeDataString(text).Replace('+', ' ').TrimEnd('/').Trim();
        foreach (string prefix in new[] { "connect/", "connect " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                text = text[prefix.Length..].Trim();
        return !text.Contains(' ') && ServerBrowser.TryAddress(text) is { } address ? new(address, null) : null;
    }

    public static string? JoinLink(string address) =>
        ServerBrowser.TryAddress(address) is { } normalized ? "cod2x://%2Bconnect+" + normalized + "/" : null;

    public static string? PasswordProblem(string password) => password.Any(character => character is < ' ' or >= '\x7f' or '"' or ';' or '\\')
        ? "Passwords can’t contain quotation marks, semicolons, backslashes or letters outside plain English." : null;
}

public sealed class ServerPasswordStore(ISecretStore secrets)
{
    public ValueTask<string?> ReadAsync(string address, CancellationToken cancellationToken = default) =>
        secrets.ReadAsync(Slot(address), cancellationToken);

    public ValueTask RememberAsync(string address, string? password, CancellationToken cancellationToken = default)
    {
        if (password is { Length: > 0 } value)
            _ = LaunchLink.Direct(address, value);
        return secrets.WriteAsync(Slot(address), string.IsNullOrEmpty(password) ? null : password, cancellationToken);
    }

    private static string Slot(string address) => "server-password/" + LaunchLink.CanonicalAddress(address);
}

public enum ServerAdmissionAction { Launch, LookUp, CheckInProgress, ConfirmVersion, MissingCod2x }
public sealed record ServerAdmissionResult(ServerAdmissionAction Action, GameEdition Edition, string? Problem = null);

/// <summary>Join attempts share one endpoint-bound edition choice for the current game.</summary>
public sealed class ServerAdmission
{
    private readonly HashSet<string> checkedAddresses = new(StringComparer.Ordinal);
    private readonly HashSet<string> lookingUp = new(StringComparer.Ordinal);
    private string? matchAddress;
    private GameEdition? matchEdition;

    public GameEdition? MatchEdition => matchEdition;
    public void EndGame() { matchAddress = null; matchEdition = null; }

    public ServerAdmissionResult Evaluate(LaunchLink link, GameServer? server, GameEdition normal,
        IReadOnlySet<GameEdition> installed, bool alwaysUseServerVersion)
    {
        string address = LaunchLink.CanonicalAddress(link.Address);
        if (matchAddress == address && matchEdition is { } choice && installed.Contains(choice))
            return new(ServerAdmissionAction.Launch, choice);
        EndGame();
        if (server is null || ServerBrowser.TryAddress(server.Address) != address)
        {
            return normal == GameEdition.Original && !checkedAddresses.Contains(address)
                ? new(lookingUp.Add(address) ? ServerAdmissionAction.LookUp : ServerAdmissionAction.CheckInProgress, normal)
                : new(ServerAdmissionAction.Launch, normal);
        }
        var needed = server.IsCod2x ? GameEdition.Cod2x : GameEdition.Original;
        if (needed == normal)
            return new(ServerAdmissionAction.Launch, normal);
        if (!installed.Contains(needed))
            return needed == GameEdition.Cod2x
                ? new(ServerAdmissionAction.MissingCod2x, normal,
                    "This server runs CoD2x, which this copy of CoD2 Unbound doesn’t include. Reinstall the app to get it.")
                : new(ServerAdmissionAction.Launch, normal);
        if (!alwaysUseServerVersion)
            return new(ServerAdmissionAction.ConfirmVersion, needed);
        AcceptVersion(address, needed);
        return new(ServerAdmissionAction.Launch, needed);
    }

    public void AcceptVersion(string address, GameEdition edition)
    {
        matchAddress = LaunchLink.CanonicalAddress(address);
        matchEdition = edition;
    }

    public void LookUpFinished(string address, bool cancelled)
    {
        string canonical = LaunchLink.CanonicalAddress(address);
        lookingUp.Remove(canonical);
        if (!cancelled)
            checkedAddresses.Add(canonical);
    }
}
