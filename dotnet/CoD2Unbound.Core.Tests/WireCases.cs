using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static readonly DateTimeOffset Observation = DateTimeOffset.UnixEpoch;

    private static void WireCases()
    {
        using var fixture = Fixtures("wire");
        var root = fixture.RootElement;
        foreach (var row in root.GetProperty("statuses").EnumerateArray())
        {
            Check(row.String("id")!, () =>
            {
                if (row.OptionalString("error") is { } expected)
                {
                    string? error = null;
                    try { WireParser.Status(row.Bytes("packet"), row.String("address")!, row.Int("ping"), Observation); }
                    catch (WireResponseException exception) { error = exception.Message; }
                    Equal(expected, error);
                    return;
                }
                var server = WireParser.Status(row.Bytes("packet"), row.String("address")!, row.Int("ping"), Observation);
                Equal(row.String("address"), server.Address);
                Equal(row.Int("ping"), server.Ping);
                ServerEqual(row, server);
            });
        }

        foreach (var row in root.GetProperty("masters").EnumerateArray())
            Check(row.String("id")!, () => Sequence(row.Strings("addresses"), WireParser.Master(row.Bytes("packet"))));

        foreach (var row in root.GetProperty("colors").EnumerateArray())
        {
            Check("name colour", () =>
            {
                string name = row.String("input")!;
                Equal(row.String("plain"), QuakeColors.Plain(name));
                Sequence(row.GetProperty("runs").EnumerateArray().Select(run => new ColorRun(run.String("text")!, run.Int("code"))),
                    QuakeColors.Runs(name));
            });
        }

        foreach (var row in root.GetProperty("links").EnumerateArray())
        {
            string input = row.String("input")!;
            Check($"link {input}", () =>
            {
                var expected = row.OptionalString("address");
                Equal(expected is not null, LaunchLink.TryParse(input, out var parsed));
                if (parsed is null)
                    return;
                Equal(expected, parsed.Address);
                Equal(row.String("password"), parsed.Password);
                Equal(row.String("canonical"), LaunchLink.CanonicalAddress(parsed.Address));
            });
        }

        Check("UDP status, info and conflicts", () => LoopbackQueries(root.GetProperty("merged")).GetAwaiter().GetResult());
        Check("UDP info-only and invalid address", () => InfoOnly().GetAwaiter().GetResult());
        Check("UDP master collection", () => LoopbackMaster().GetAwaiter().GetResult());
    }

    private static void ServerEqual(JsonElement expected, GameServer server)
    {
        Sequence(expected.GetProperty("fields").EnumerateObject().Select(field => KeyValuePair.Create(field.Name, field.Value.GetString()!))
                .OrderBy(field => field.Key, StringComparer.Ordinal), server.Fields.OrderBy(field => field.Key, StringComparer.Ordinal));
        Equal(expected.String("name"), server.Name);
        Equal(expected.String("map"), server.Map);
        Equal(expected.String("gametype"), server.GameType);
        Equal(expected.GetProperty("maxPlayers").GetInt64(), server.MaxPlayers);
        Equal(expected.GetProperty("playerCount").GetInt64(), server.PlayerCount);
        Equal(expected.String("version"), server.Version);
        Equal(expected.String("mod"), server.Mod);
        Equal(expected.GetProperty("password").GetBoolean(), server.PasswordRequired);
        Equal(expected.GetProperty("statusObservation").GetBoolean() ? Observation : (DateTimeOffset?)null, server.ObservedAt);
        Sequence(expected.GetProperty("players").EnumerateArray().Select(player =>
            new ServerPlayer(player.Int("id"), player.GetProperty("score").GetInt64(), player.GetProperty("ping").GetInt64(), player.String("name")!)), server.Players);
        if (expected.GetProperty("conflicts").ValueKind == JsonValueKind.Null)
            Equal(true, server.QueryConflicts.IsDefaultOrEmpty);
        else
            Sequence(expected.Strings("conflicts"), server.QueryConflicts);
    }

    private static async Task LoopbackQueries(JsonElement expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var standIn = new LocalStandIn();
        var query = new ServerQueryClient(standIn.Transport, new FixedClock(), () => Guid.Parse("7badcafe-5555-1234-1234-123456789abc"));
        const string challenge = "7BADCAFE-555";
        var serverTask = standIn.ServeAsync(2, request => request switch
        {
            "getstatus " + challenge => [Packet("statusResponse\n\\sv_hostname\\Original\\Case\\first\\protocol\\120\n1 29 \"Local\"\n")],
            "getinfo " + challenge => [Packet("infoResponse\n\\hostname\\Information\\case\\second\\protocol\\118\\clients\\2\n")],
            _ => throw new InvalidDataException($"Unexpected datagram {request}")
        }, deadline.Token);
        var server = await query.ServerAsync($"127.0.0.1:{standIn.Port}", deadline.Token);
        await serverTask;
        ServerEqual(expected, server!);
        var sent = standIn.Transport.Exchanges.ToArray();
        Sequence(new[] { 650d, 350d }, sent.Select(exchange => exchange.ResponseTimeout.TotalMilliseconds));
        Equal(true, sent.All(exchange => exchange is { ResponseLimit: 1, MaxPacketBytes: 65535 } &&
            exchange.SetupTimeout == TimeSpan.FromSeconds(2)));
    }

    private static async Task InfoOnly()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var standIn = new LocalStandIn();
        var query = new ServerQueryClient(standIn.Transport);
        var serverTask = standIn.ServeAsync(2, request => request.StartsWith("getstatus ", StringComparison.Ordinal)
            ? [] : [Packet("infoResponse\n\\hostname\\Info only\n")], deadline.Token);
        string address = $"127.0.0.1:{standIn.Port}";
        var server = await query.ServerAsync(address, deadline.Token);
        await serverTask;
        Equal("Info only", server!.Name);
        Equal(null, server.ObservedAt);
        Equal(null, await query.ServerAsync("127.0.0.1:0", deadline.Token));
        Equal(2, standIn.Transport.Exchanges.Count);
    }

    private static async Task LoopbackMaster()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var standIn = new LocalStandIn();
        var query = new ServerQueryClient(standIn.Transport);
        byte[] PacketFor(byte last) =>
            [.. Packet("getserversResponse"), 92, 127, 0, 0, last, 0x71, 0x20, .. "\\EOT\0\0\0"u8];
        var serverTask = standIn.ServeAsync(2, request => request switch
        {
            "getservers 118 full empty" => [PacketFor(1), PacketFor(2)],
            "getservers 120 full empty" => [PacketFor(2), PacketFor(3)],
            _ => throw new InvalidDataException(request)
        }, deadline.Token);
        var addresses = await query.MasterAddressesAsync([new("127.0.0.1", standIn.Port)], deadline.Token);
        await serverTask;
        Sequence(new[] { "127.0.0.1:28960", "127.0.0.2:28960", "127.0.0.3:28960" }, addresses);
        Equal(2, standIn.Transport.Exchanges.Count);
        Equal(true, standIn.Transport.Exchanges.All(exchange => exchange.ResponseLimit == 64 && exchange.ResponseTimeout.TotalMilliseconds == 1500));
    }

    private static byte[] Packet(string payload) => [.. WireParser.Marker, .. Encoding.UTF8.GetBytes(payload)];

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Observation;
    }

    private sealed class LocalStandIn : IDisposable
    {
        private readonly UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        public int Port => ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        public LoopbackTransport Transport { get; } = new();

        public async Task ServeAsync(int requests, Func<string, byte[][]> replies, CancellationToken cancellationToken)
        {
            for (int index = 0; index < requests; index++)
            {
                var input = await server.ReceiveAsync(cancellationToken);
                Equal(true, input.Buffer.AsSpan().StartsWith(WireParser.Marker));
                foreach (var reply in replies(Encoding.UTF8.GetString(input.Buffer, 4, input.Buffer.Length - 4)))
                    await server.SendAsync(reply, input.RemoteEndPoint, cancellationToken);
            }
        }

        public void Dispose() => server.Dispose();
    }

    private sealed class LoopbackTransport : IUdpTransport
    {
        public ConcurrentQueue<UdpExchange> Exchanges { get; } = new();

        public async Task<UdpReplies> ExchangeAsync(UdpExchange exchange, CancellationToken cancellationToken)
        {
            Equal("127.0.0.1", exchange.Endpoint.Host);
            Exchanges.Enqueue(exchange);
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Connect(IPAddress.Loopback, exchange.Endpoint.Port);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(exchange.ResponseTimeout);
            long timestamp = Stopwatch.GetTimestamp();
            await socket.SendAsync(exchange.Request.AsMemory(), timeout.Token);
            var packets = ImmutableArray.CreateBuilder<ImmutableArray<byte>>();
            TimeSpan? roundTrip = null;
            try
            {
                while (packets.Count < exchange.ResponseLimit)
                {
                    var response = await socket.ReceiveAsync(timeout.Token);
                    if (response.Buffer.Length > exchange.MaxPacketBytes)
                        continue;
                    roundTrip ??= Stopwatch.GetElapsedTime(timestamp);
                    packets.Add([.. response.Buffer]);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            return new(packets.ToImmutable(), roundTrip);
        }
    }
}
