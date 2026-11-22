using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public readonly record struct UdpEndpoint(string Host, int Port);
public sealed record UdpExchange(UdpEndpoint Endpoint, ImmutableArray<byte> Request, TimeSpan SetupTimeout,
    TimeSpan ResponseTimeout, int ResponseLimit, int MaxPacketBytes);
public sealed record UdpReplies(ImmutableArray<ImmutableArray<byte>> Packets, TimeSpan? FirstRoundTrip = null);

public interface IUdpTransport
{
    // Use connected UDP, a separate setup deadline and a reply timer starting at send.
    // Timeout/socket failure returns packets already received. Cancellation may throw.
    Task<UdpReplies> ExchangeAsync(UdpExchange exchange, CancellationToken cancellationToken);
}

public sealed class ServerQueryClient(IUdpTransport transport, TimeProvider? time = null, Func<Guid>? newChallenge = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Func<Guid> challengeFactory = newChallenge ?? Guid.NewGuid;
    private static readonly ImmutableArray<UdpEndpoint> Masters =
        [new("cod2master.activision.com", 20710), new("master.cod2x.me", 20710)];

    public async Task<ImmutableArray<string>> MasterAddressesAsync(CancellationToken cancellationToken = default) =>
        await MasterAddressesAsync(Masters, cancellationToken).ConfigureAwait(false);

    public async Task<ImmutableArray<string>> MasterAddressesAsync(IEnumerable<UdpEndpoint> endpoints,
        CancellationToken cancellationToken = default)
    {
        int[] protocols = [118, 120];
        var requests = endpoints.SelectMany(endpoint => protocols.Select(protocol =>
            ExchangeAsync(endpoint, FormattableString.Invariant($"getservers {protocol} full empty"), 1500, 64, cancellationToken)));
        var replies = await Task.WhenAll(requests).ConfigureAwait(false);
        return [.. replies.SelectMany(reply => reply.Packets).SelectMany(packet => WireParser.Master(packet.AsSpan()))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(2048)];
    }

    public async Task<GameServer?> ServerAsync(string address, CancellationToken cancellationToken = default)
    {
        LaunchLink link;
        try { link = LaunchLink.Direct(address); }
        catch (FormatException) { return null; }

        int colon = link.Address.IndexOf(':');
        var endpoint = colon < 0
            ? new UdpEndpoint(link.Address, LaunchLink.DefaultPort)
            : new UdpEndpoint(link.Address[..colon], (int)(WireParser.Integer(link.Address[(colon + 1)..]) ?? LaunchLink.DefaultPort));
        string challenge = challengeFactory().ToString().ToUpperInvariant()[..12];
        var status = Parse(await ExchangeAsync(endpoint, $"getstatus {challenge}", 650, 1, cancellationToken).ConfigureAwait(false), address);
        var info = Parse(await ExchangeAsync(endpoint, $"getinfo {challenge}", 350, 1, cancellationToken).ConfigureAwait(false), address);
        if (info is null)
            return status;
        return status?.MergeInfo(info) ?? info with { ObservedAt = null };
    }

    private GameServer? Parse(UdpReplies replies, string address)
    {
        int ping = (int)(replies.FirstRoundTrip?.TotalMilliseconds ?? 0);
        foreach (var packet in replies.Packets)
        {
            try { return WireParser.Status(packet.AsSpan(), address, ping, clock.GetUtcNow()); }
            catch (WireResponseException) { }
        }
        return null;
    }

    private Task<UdpReplies> ExchangeAsync(UdpEndpoint endpoint, string command, int milliseconds, int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<byte> request = [.. WireParser.Marker, .. Encoding.UTF8.GetBytes(command)];
        return transport.ExchangeAsync(new(endpoint, request, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(milliseconds),
            limit, WireParser.MaxPacketBytes), cancellationToken);
    }
}
