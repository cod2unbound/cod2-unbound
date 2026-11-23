using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;

namespace CoD2Unbound.Core;

public sealed record HostedDashboard(string PlayerCount, string Map, string GameType,
    ImmutableArray<HostedStatusPlayer> Players, bool IsFull, bool CanJoin)
{
    public static HostedDashboard From(HostedServer server, HostedRunState state)
    {
        var status = state.Status;
        var players = status?.Players ?? [];
        int limit = status is { MaxPlayers: > 0 } ? status.MaxPlayers : server.Players;
        bool full = status is not null && players.Length >= limit;
        string gameType = HostingCatalog.GameTypeTitle(status?.GameType ?? HostingConfig.GameType(server));
        if (status?.MatchInfo is { } match)
            gameType += " · " + match;
        return new(status is null ? $"— / {limit}" : $"{players.Length} / {limit}",
            status is { Map.Length: > 0 } ? MapCatalog.DisplayName(HostingCatalog.StockMap(status.Map)) : "Waiting for status",
            gameType, players, full, state.Phase == HostedPhase.Running && status is not null && !full);
    }

    public static bool CanKick(ImmutableArray<HostedStatusPlayer> roster, HostedStatusPlayer player)
    {
        string name = HostedStatus.Plain(player.Name);
        return name.Length > 0 && name.All(ch => ch is >= ' ' and < '\u007f') && HostingConfig.Safe(name, 32) == name &&
            roster.Any(row => row.Id == player.Id && row.Name == player.Name) &&
            roster.Count(row => HostedStatus.Plain(row.Name).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                row.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 1;
    }
}

public sealed record HostedJoinAddresses(string ThisPc, ImmutableArray<string> Network, string? Invite, bool InLocalList)
{
    public static HostedJoinAddresses From(HostedServer server, IEnumerable<string> ipv4Addresses, bool loopbackOnly)
    {
        string Endpoint(string host) => FormattableString.Invariant($"{host}:{server.Port}");
        var network = loopbackOnly ? ImmutableArray<string>.Empty :
            [.. ipv4Addresses.Select(address => IPAddress.TryParse(address, out var ip) ? ip : null)
                .OfType<IPAddress>().Where(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast))
                .Distinct().Select(ip => Endpoint(ip.ToString()))];
        string? invite = network.IsEmpty ? null :
            (server.Engine == GameEdition.Original ? "cod2://" : "cod2x://") + Uri.EscapeDataString("connect " + network[0]);
        return new(Endpoint("127.0.0.1"), network, invite, server.InLocalList);
    }
}
