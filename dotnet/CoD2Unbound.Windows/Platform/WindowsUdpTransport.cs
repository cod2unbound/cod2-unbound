using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsUdpTransport : IUdpTransport
{
    private readonly bool loopbackOnly;

    internal WindowsUdpTransport(bool loopbackOnly = false) => this.loopbackOnly = loopbackOnly;

    public async Task<UdpReplies> ExchangeAsync(UdpExchange exchange, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setup.CancelAfter(exchange.SetupTimeout);
        var packets = ImmutableArray.CreateBuilder<ImmutableArray<byte>>();
        TimeSpan? roundTrip = null;
        try
        {
            string host = exchange.Endpoint.Host;
            var addresses = IPAddress.TryParse(host, out var parsed) ? [parsed] : loopbackOnly ? [] :
                await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, setup.Token).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                (!loopbackOnly || IPAddress.IsLoopback(address)));
            if (address is null)
                return new([]);
            socket.Connect(address, exchange.Endpoint.Port);
            await socket.SendAsync(exchange.Request.AsMemory(), setup.Token).ConfigureAwait(false);

            long sent = Stopwatch.GetTimestamp();
            using var response = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            response.CancelAfter(exchange.ResponseTimeout);
            while (packets.Count < exchange.ResponseLimit)
            {
                var result = await socket.ReceiveAsync(response.Token).ConfigureAwait(false);
                if (result.Buffer.Length > exchange.MaxPacketBytes)
                    continue;
                roundTrip ??= Stopwatch.GetElapsedTime(sent);
                packets.Add([.. result.Buffer]);
            }
        }
        catch (SocketException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return new(packets.ToImmutable(), roundTrip);
    }
}
