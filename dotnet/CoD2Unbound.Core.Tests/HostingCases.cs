using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void HostingCases()
    {
        using var fixture = Fixtures("hosting");
        var root = fixture.RootElement;
        var listing = root.Bytes("listing");
        var release = ZpamRelease.Newest(ZpamLine.Three, listing)!;
        foreach (var row in root.GetProperty("configs").EnumerateArray())
        {
            Check("hosted config, saved server, export and portable port selection", () =>
            {
                byte[] saved = Encoding.UTF8.GetBytes("[" + row.GetProperty("server").GetRawText() + "]");
                var server = HostedServerJson.Read(saved).Single();
                var savedAgain = HostedServerJson.Read(HostedServerJson.Write([server]).AsSpan()).Single();
                Equal(server.Id, savedAgain.Id);
                Sequence(server.Maps, savedAgain.Maps);
                Equal(server.AdminPassword, savedAgain.AdminPassword);
                var template = row.String("template");
                Equal(row.String("fileName"), HostingConfig.FileName(server));
                Equal(row.Int("otherPort"), HostingConfig.OtherPort(server, port => port != 28961));
                Equal(row.String("config"), HostingConfig.Render(server, template));
                HostingExportEqual(row.GetProperty("export"), HostingConfig.Export(server, template, release, passwords: false, matchSettings: true));
                HostingExportEqual(row.GetProperty("publicExport"), HostingConfig.Export(server, template, release, passwords: true, matchSettings: false));
            });
        }

        foreach (var row in root.GetProperty("status").EnumerateArray())
        {
            Check("hosted status and zPAM match line", () =>
            {
                var result = HostedStatus.Parse(row.String("input")!);
                var expected = row.GetProperty("status");
                if (expected.ValueKind == JsonValueKind.Null) { Equal(null, result); return; }
                Equal(expected.String("map"), result!.Map);
                Equal(expected.String("gameType"), result.GameType);
                Equal(expected.Int("maxPlayers"), result.MaxPlayers);
                Equal(expected.String("matchInfo"), result.MatchInfo);
                Sequence(expected.GetProperty("players").EnumerateArray().Select(player =>
                    new HostedStatusPlayer(player.Int("id"), player.String("name")!, player.Int("score"), player.Int("ping"))), result.Players);
            });
        }

        foreach (var row in root.GetProperty("releases").EnumerateArray())
        {
            Check("zPAM release source, size and digest", () =>
            {
                var line = row.String("line") == "three" ? ZpamLine.Three : ZpamLine.Four;
                var result = ZpamRelease.Newest(line, listing)!;
                Equal(row.String("asset"), result.Asset);
                Equal(row.String("version"), result.Version);
                Equal(row.String("url"), result.Url.AbsoluteUri);
                Equal((long)row.Int("bytes"), result.Bytes);
                Equal(row.String("sha256"), result.Sha256);
                Equal(null, ZpamRelease.Newest(line, "not json"u8));
            });
        }

        Check("server export protects quoted options and duplicate managed template values", () =>
        {
            var server = HostedServer.New("sample") with
            {
                Name = "Ends with slash\\", AdminPassword = "safe\\", Rules = HostedRules.Zpam
            };
            string template = "set rcon_password \"sample-first\"\nseta RCON_PASSWORD \"sample-second\"\nmap mp_toujane\n";
            var config = HostingConfig.Render(server, template, passwords: false);
            Equal(false, config.Contains("sample-first", StringComparison.Ordinal));
            Equal(false, config.Contains("sample-second", StringComparison.Ordinal));
            var parsed = new ConfigDocument(Encoding.UTF8.GetBytes(config), ConfigDialect.Cod2x);
            Equal("CHANGE_ME", parsed.EffectiveSetting("rcon_password"));
            Equal("Ends with slash", parsed.EffectiveSetting("sv_hostname"));
        });

        Check("hosting admin queue uses only the owned loopback ports and preserves order", () =>
        {
            var fake = new HostingUdp();
            var clock = new AdvancingClock();
            var queue = new HostedAdminQueue(fake, clock);
            var server = HostedServer.New("sample-host") with { AdminPassword = "safe pass" };
            var first = queue.NextMapAsync(server, () => true);
            var second = queue.KickAsync(server, new(1, "^2Test\";name", 5, 11), () => true);
            var declined = queue.NextMapAsync(server, () => false);
            Task.WhenAll(first, second, declined).GetAwaiter().GetResult();
            Equal(true, first.Result);
            Equal(true, second.Result);
            Equal(false, declined.Result);
            Equal(TimeSpan.FromMilliseconds(1100), clock.GetElapsedTime(0));
            Sequence(new[] { "rcon safepass map_rotate", "rcon safepass kick \"Testname\"" }, fake.Commands);
        });

        Check("hosting dashboard distinguishes a full server from a starting server", () =>
        {
            var server = HostedServer.New() with { Players = 2 };
            var status = new HostedStatus("mp_toujane_fix", "sd", 2, [new(0, "^2Rifle", 0, 12), new(1, "Scout", 0, 15)], "Round 1");
            var running = HostedDashboard.From(server, new(HostedPhase.Running, status));
            Equal("Toujane", running.Map);
            Equal("Search & Destroy · Round 1", running.GameType);
            Equal(true, running.IsFull);
            Equal(false, running.CanJoin);
            Equal("2 / 2", running.PlayerCount);
            var starting = HostedDashboard.From(server, new(HostedPhase.Starting));
            Equal(false, starting.IsFull);
            Equal(false, starting.CanJoin);
        });

        Check("hosting addresses expose only reachable interfaces and omit invite passwords", () =>
        {
            var server = HostedServer.New(edition: GameEdition.Original) with { Port = 28965, JoinPassword = "private" };
            var local = HostedJoinAddresses.From(server, ["127.0.0.1", "192.168.1.20", "192.168.1.20", "::1"], loopbackOnly: false);
            Sequence(new[] { "192.168.1.20:28965" }, local.Network);
            Equal(false, local.InLocalList);
            Equal("cod2://connect%20192.168.1.20%3A28965", local.Invite);
            var isolated = HostedJoinAddresses.From(server, ["192.168.1.20"], loopbackOnly: true);
            Equal("127.0.0.1:28965", isolated.ThisPc);
            Equal(0, isolated.Network.Length);
            Equal(null, isolated.Invite);
        });

        Check("kick requires a unique player still present in the roster", () =>
        {
            var player = new HostedStatusPlayer(0, "^2Rifle", 0, 12);
            Equal(true, HostedDashboard.CanKick([player with { Ping = 15 }], player));
            Equal(false, HostedDashboard.CanKick([player with { Name = "New player" }], player));
            Equal(false, HostedDashboard.CanKick([player, new(1, "^3rifle", 0, 14)], player));
        });
    }

    private static void HostingExportEqual(JsonElement expected, IReadOnlyDictionary<string, string> actual)
    {
        Equal(expected.EnumerateObject().Count(), actual.Count);
        foreach (var file in expected.EnumerateObject())
            Equal(file.Value.GetString(), actual[file.Name]);
    }

    private sealed class HostingUdp : IUdpTransport
    {
        public List<string> Commands { get; } = [];
        public Task<UdpReplies> ExchangeAsync(UdpExchange exchange, CancellationToken cancellationToken)
        {
            Equal("127.0.0.1", exchange.Endpoint.Host);
            Equal(28960, exchange.Endpoint.Port);
            Sequence(new byte[] { 255, 255, 255, 255 }, exchange.Request.AsSpan()[..4].ToArray());
            Commands.Add(Encoding.Latin1.GetString(exchange.Request.AsSpan()[4..]));
            return Task.FromResult(new UdpReplies([], null));
        }
    }
}
