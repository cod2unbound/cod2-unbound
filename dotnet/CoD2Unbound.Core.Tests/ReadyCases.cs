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

        Check("clearing recent servers and timestamps survives a source-generated round trip", () =>
        {
            const string address = "localhost:28960";
            var library = new LauncherLibrary { Favorites = [address] }.Joined(address, clock.GetUtcNow());
            var cleared = LauncherLibrary.Read(library.ClearRecent().Write().AsMemory());
            Equal(true, cleared.Recent.IsEmpty);
            Equal(true, cleared.Played.IsEmpty);
            Equal(true, cleared.Favorites.Contains(address));
        });

        Check("direct entry carries the observed result and an explicit link edition", () =>
        {
            var input = ServerConnectEntry.Parse("cod2://connect/LOCALHOST", "");
            Equal("localhost:28960", input.Target?.Address);
            Equal<GameEdition?>(GameEdition.Original, input.Edition);
            string copied = ServerAddress.JoinLink(input.Target!.Address, GameEdition.Original)!;
            Equal(input.Target.Address, ServerLink.Parse(copied)?.Target.Address);
            Equal<GameEdition?>(GameEdition.Original, ServerLink.Parse(copied)?.Edition);
            Equal(true, ServerConnectEntry.Parse("localhost:65536", "").InvalidAddress);
            Equal(false, ServerConnectEntry.Parse(" ", "").InvalidAddress);
            Equal(null, ServerConnectEntry.Parse("localhost", "bad;password").Target);
            Equal("one use", ServerConnectEntry.Parse("cod2x://connect%20localhost%20password%20%22one%20use%22", "").Target?.Password);
        });

        Check("remembered passwords are per canonical endpoint and clearing one leaves other secrets", () =>
        {
            var platform = new MemoryPlatform("C:/fixture");
            var store = new ServerPasswordStore(platform);
            var secrets = (ISecretStore)platform;
            secrets.WriteAsync(SetupSession.KeySecret, "fixture key", default).GetAwaiter().GetResult();
            store.RememberAsync("LOCALHOST", "first secret").GetAwaiter().GetResult();
            store.RememberAsync("localhost:28961", "second secret").GetAwaiter().GetResult();
            Equal("first secret", Sync(store.ReadAsync("localhost:28960")));
            store.RememberAsync("LOCALHOST:28960", null).GetAwaiter().GetResult();
            Equal(null, Sync(store.ReadAsync("localhost")));
            Equal("second secret", Sync(store.ReadAsync("localhost:28961")));
            Equal("fixture key", Sync(secrets.ReadAsync(SetupSession.KeySecret, default)));
        });

        Check("ready check switches the inspected loadout and uses a disposable, endpoint-bound fix", () =>
        {
            var rows = fixture.RootElement.GetProperty("cases").EnumerateArray().ToArray();
            var original = rows.Single(row => row.String("name") == "competitive");
            var compatible = rows.Single(row => row.String("name") == "compatible");
            var files = new FixtureProfileFiles();
            var store = new ConfigProfileStore(files, ConfigDialect.Cod2x);
            store.Create("Main", "Main", new(original.Bytes("input"), ConfigDialect.Cod2x));
            store.Create("Scrim", "Scrim", new(compatible.Bytes("input"), ConfigDialect.Cod2x));
            store.Select("Main", null);
            bool playing = false;
            var studio = new ConfigStudioSession(files, ConfigDialect.Cod2x, () => playing, clock);
            var ready = new ServerReadySession(check, new ServerQueryClient(new BrowserUdp(), clock), studio);
            var server = new GameServer("127.0.0.1:28960", Fields(original.GetProperty("fields")), 12, [], clock.GetUtcNow());
            var link = LaunchLink.Direct(server.Address);
            const ConfigCatalogMode mode = ConfigCatalogMode.Cod2x;
            Equal(ConfigReadyStatus.NeedsChanges, ready.Evaluate(server.Address, server, mode).Report?.Status);
            Equal("Scrim", ready.CompatibleAlternative(mode)?.Id);
            ready.UseLoadout("Scrim", mode);
            Equal(ConfigReadyStatus.MatchesKnownRules, ready.State.Report?.Status);
            Equal("Scrim", ready.Prepare(link, server, mode)?.ProfileId);
            Equal("Main", store.ActiveSelection().ProfileId);
            playing = true;
            ProfileRejects(ConfigProfileFailure.GameRunning, () => ready.Undo(mode));
            playing = false;
            ready.Undo(mode);
            ready.Fix();
            Equal(null, ready.Prepare(LaunchLink.Direct("127.0.0.1:28961"), server, mode));
            var choice = ready.Prepare(link, server, mode);
            Equal("250", choice?.Connection?.Values["com_maxfps"]);
            var prepared = studio.PrepareClientLaunch(new(GameEdition.Cod2x, new(), "data", "home") { Link = link, ServerLoadout = choice });
            Equal(true, prepared.Studio?.ConnectionIsolated);
            Equal(true, studio.IsTemporaryServerLoadout);
            Equal("250", prepared.Connection?.Values["com_maxfps"]);
            studio.CancelLaunch();
            Equal(false, studio.IsTemporaryServerLoadout);
            Equal("Main", store.ActiveSelection().ProfileId);
            Equal("333", store.Read("Main").Document.EffectiveSetting("com_maxfps"));
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
