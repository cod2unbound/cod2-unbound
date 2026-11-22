using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void SetupDiscoveryCases()
    {
        using var fixture = Fixtures("directory");
        var root = fixture.RootElement;
        var index = new Uri(root.String("indexURL")!);
        var signature = new Uri(root.String("signatureURL")!);
        var figures = new Uri(index, "popularity.json");
        var figuresSignature = new Uri(index, "popularity.sig");
        var key = new Ed25519PrivateKeyParameters(new byte[32]);
        var trust = new ConfigDirectoryConfiguration(index, signature, [.. key.GeneratePublicKey().GetEncoded()]);
        var source = new DirectoryFetcher();
        ConfigDirectory Directory(DirectoryStorage? storage = null) => new(trust, storage ?? new(), source);
        var published = """{"schemaVersion":1,"periodDays":30,"generatedDate":"2026-11-22","setups":{"player-1":{"imports":64,"views":4},"player-2":{"imports":0,"views":188},"player-3":{"views":5},"player-4":{"imports":4}}}""";

        void Offer(string json, bool badSignature = false, Uri? finalUrl = null, Uri? finalSignature = null)
        {
            source.HttpStatus = null;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            source.Responses[figures] = new([.. bytes], finalUrl ?? figures, null);
            byte[] signed = new byte[64];
            if (!badSignature)
                key.Sign(Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Algorithm.Ed25519, null, bytes, signed);
            source.Responses[figuresSignature] = new([.. signed], finalSignature ?? figuresSignature, null);
        }

        Check("signed setup figures suppress small counts independently", () =>
        {
            Offer(published);
            var result = Sync(Directory().LoadPopularityAsync())!;
            Equal(30, result.PeriodDays);
            Equal("2026-11-22", result.GeneratedDate);
            Equal(64L, result.Setups["player-1"].Imports);
            Equal(null, result.Setups["player-1"].Views);
            Equal(null, result.Setups["player-2"].Imports);
            Equal(188L, result.Setups["player-2"].Views);
            Equal(5L, result.Setups["player-3"].Views);
            Equal(false, result.Setups.ContainsKey("player-4"));
        });

        Check("missing setup figures expose only featured order", () =>
        {
            source.HttpStatus = 404;
            var result = Sync(Directory().LoadPopularityAsync());
            Equal(null, result);
            Sequence(new[] { ConfigSetupOrder.FeaturedFirst }, ConfigDiscover.AvailableOrders(result));
        });

        Check("bad setup figure signature gives no figures", () =>
        {
            Offer(published, badSignature: true);
            Equal(null, Sync(Directory().LoadPopularityAsync()));
        });

        Check("setup figure contents and signature require their own addresses", () =>
        {
            Offer(published, finalUrl: index);
            Equal(null, Sync(Directory().LoadPopularityAsync()));
            Offer(published, finalSignature: signature);
            Equal(null, Sync(Directory().LoadPopularityAsync()));
        });

        Check("setup figures fall back to a verified cache offline", () =>
        {
            Offer(published);
            var storage = new DirectoryStorage();
            Sync(Directory(storage).LoadPopularityAsync());
            source.HttpStatus = 404;
            Equal(64L, Sync(Directory(storage).LoadPopularityAsync())!.Setups["player-1"].Imports);
            storage.Files[ConfigDirectoryFile.Popularity] = [1, 2, 3];
            Equal(null, Sync(Directory(storage).LoadPopularityAsync()));
        });

        Check("older setup figures cannot replace the cache and same-day corrections can", () =>
        {
            var storage = new DirectoryStorage();
            Offer(published);
            Sync(Directory(storage).LoadPopularityAsync());
            Offer(published.Replace("2026-11-22", "2026-11-21").Replace("64", "90"));
            Equal(64L, Sync(Directory(storage).LoadPopularityAsync())!.Setups["player-1"].Imports);
            Offer(published.Replace("64", "92"));
            Equal(92L, Sync(Directory(storage).LoadPopularityAsync())!.Setups["player-1"].Imports);
        });

        Check("setup figure schema, date, period and IDs are bounded", () =>
        {
            foreach (string bad in new[] { published.Replace("64", "9007199254740992"), published.Replace("2026-11-22", "2026-02-29"),
                published.Replace("30", "367"), published.Replace("player-1", "%2F"), published.Replace("Version\":1", "Version\":2") })
            {
                Offer(bad);
                Equal(null, Sync(Directory().LoadPopularityAsync()));
            }
        });

        Check("setup ordering keeps signed order for ties and unranked entries", () =>
        {
            var row = root.GetProperty("cases").EnumerateArray().First(row => row.String("id") == "signed");
            var indexTrust = new ConfigDirectoryConfiguration(index, signature, [.. root.Bytes("publicKey")]);
            source.Responses[index] = new([.. row.Bytes("index")], index, null);
            source.Responses[signature] = new([.. row.Bytes("signature")], signature, null);
            source.HttpStatus = null;
            var template = Load(new ConfigDirectory(indexTrust, new DirectoryStorage(), source)).Snapshot!.Index.Entries[0];
            var entries = new[] { template with { Id = "player-4", Player = "Eagle", Featured = false },
                template with { Id = "player-3", Player = "Alpha", Featured = true },
                template with { Id = "player-2", Player = "Bravo", Featured = true },
                template with { Id = "player-1", Player = "Charlie", Featured = false } }.ToImmutableArray();
            Offer("""{"schemaVersion":1,"periodDays":1,"generatedDate":"2026-11-22","setups":{"player-2":{"views":5},"player-3":{"views":5},"player-1":{"views":18}}}""");
            var result = Sync(Directory().LoadPopularityAsync())!;
            Sequence(new[] { ConfigSetupOrder.FeaturedFirst, ConfigSetupOrder.MostViewed }, ConfigDiscover.AvailableOrders(result));
            Sequence(new[] { "player-1", "player-3", "player-2", "player-4" },
                ConfigDiscover.OrderSetups(entries, ConfigSetupOrder.MostViewed, result, CultureInfo.InvariantCulture).Select(e => e.Id));
            Sequence(new[] { "player-3", "player-2", "player-1", "player-4" },
                ConfigDiscover.OrderSetups(entries, ConfigSetupOrder.MostImported, result, CultureInfo.InvariantCulture).Select(e => e.Id));
        });
    }
}
