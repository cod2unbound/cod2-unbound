using System.Collections.Immutable;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void ReadyCases()
    {
        using var fixture = Fixtures("ready");
        var clock = new FixedClock();
        var check = new ConfigReadyCheck(ConfigCatalog.LoadBundled(), ConfigRules.LoadBundled(), clock);
        Check("CoD2x status 120 keeps its protocol when getinfo advertises stock discovery protocol", () =>
        {
            var status = WireParser.Status([.. WireParser.Marker, .. "statusResponse\n\\protocol\\120\\shortversion\\1.4.6.8\\mod\\0\\g_competitive\\0\n"u8],
                "127.0.0.1:28960", 1, clock.GetUtcNow());
            var info = WireParser.Status([.. WireParser.Marker, .. "infoResponse\n\\protocol\\118\\mod\\0\\clients\\1"u8],
                status.Address, 1, clock.GetUtcNow());
            var combined = status.MergeInfo(info);
            Equal("120", combined.Fields["protocol"]);
            Equal(true, combined.QueryConflicts.IsDefaultOrEmpty);
            Equal("1", combined.Fields["clients"]);
            var contradictory = status with { Fields = status.Fields.Remove("shortversion") };
            Equal(true, contradictory.MergeInfo(info).QueryConflicts.Contains("protocol"));
        });

        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            Check("Swift Ready Check " + row.GetProperty("name").GetString(), () =>
            {
                var mode = row.GetProperty("mode").GetString() == "cod2x" ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock;
                ImmutableArray<byte> bytes = [.. row.GetProperty("input").GetBytesFromBase64()];
                var document = new ConfigDocument(bytes.AsSpan(), mode == ConfigCatalogMode.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock);
                var metadata = new ConfigStudioMetadata("Fixture")
                {
                    Locks = row.GetProperty("locks").EnumerateArray().Select(item => item.GetString()!).ToImmutableHashSet()
                };
                var profile = new ConfigProfileRead(new("Fixture", "Fixture", true), document, metadata, new(bytes, null));
                var age = row.GetProperty("age");
                var server = new GameServer("localhost", Fields(row.GetProperty("fields")), 12, [],
                    age.ValueKind == JsonValueKind.Number ? clock.GetUtcNow().AddSeconds(-age.GetInt32()) : null,
                    [.. row.GetProperty("conflicts").EnumerateArray().Select(item => item.GetString()!)]);
                var result = check.Evaluate(server, profile, mode, requireFresh: row.GetProperty("requireFresh").GetBoolean());
                Equal(row.GetProperty("status").GetString(), Camel(result.Status.ToString()));
                Equal(row.GetProperty("summary").GetString(), result.Summary);
                Sequence(row.GetProperty("ids").EnumerateArray().Select(item => item.GetString()), result.RulesetIds.Select(item => (string?)item));
                Sequence(Fields(row.GetProperty("fixes")).OrderBy(item => item.Key), result.Fixes.OrderBy(item => item.Key));
                Sequence(Fields(row.GetProperty("facts")).OrderBy(item => item.Key), result.Facts.OrderBy(item => item.Key));
                var details = row.GetProperty("details").EnumerateArray().ToArray();
                Equal(details.Length, result.Details.Length);
                foreach (var (expected, actual) in details.Zip(result.Details))
                {
                    Equal(expected.GetProperty("setting").GetString(), actual.Setting);
                    Equal(expected.GetProperty("status").GetString(), Camel(actual.Status.ToString()));
                    Equal(expected.GetProperty("reason").GetString(), actual.Reason);
                }
                Sequence(row.GetProperty("limitations").EnumerateArray().Select(item => item.GetString()),
                    result.Limitations.Select(item => (string?)item));
                if (result.Fixes.IsEmpty)
                    return;
                var connection = new ConfigServerConnection();
                connection.Select("LOCALHOST");
                connection.Fix(result);
                Equal("localhost:28960", connection.Address);
                Equal<ConfigConnectionLaunch?>(null, connection.Launch(LaunchLink.Direct("localhost:28961"), profile, result));
                var launch = connection.Launch(LaunchLink.Direct("localhost"), profile, result);
                Equal(result.Fixes, launch!.Values);
                var changed = profile with { Version = profile.Version with { Config = [1] } };
                try
                {
                    _ = connection.Launch(LaunchLink.Direct("localhost"), changed, result);
                    throw new InvalidDataException("Changed loadout was accepted.");
                }
                catch (InvalidOperationException error)
                {
                    Equal("The server rules or loadout changed. Review this connection again.", error.Message);
                }
            });
    }

    private static string Camel(string value) => char.ToLowerInvariant(value[0]) + value[1..];
    private static ImmutableDictionary<string, string> Fields(JsonElement value) =>
        value.EnumerateObject().ToImmutableDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal);
}
