using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void ChainCases()
    {
        using var fixture = Fixtures("chains");
        foreach (var row in fixture.RootElement.GetProperty("traces").EnumerateArray())
            Check("Swift chain trace " + row.GetProperty("id").GetString(), () =>
            {
                var machine = new ConfigChainMachine(row.GetProperty("cod2x").GetBoolean() ? ConfigDialect.Cod2x : ConfigDialect.Stock);
                var actual = machine.Execute([.. row.GetProperty("input").GetBytesFromBase64()]);
                var trace = row.GetProperty("trace");
                Equal(trace.GetProperty("complete").GetBoolean(), actual.Complete);
                Sequence(Texts(trace.GetProperty("issues")), actual.Issues.Select(issue => Camel(issue.Problem.ToString())));
                var expected = trace.GetProperty("commands").EnumerateArray().ToArray();
                Equal(expected.Length, actual.Commands.Length);
                foreach (var (want, got) in expected.Zip(actual.Commands))
                    Sequence(Texts(want), got.Select(token => token.Text));
            });
        Check("Swift managed bindings, conflicts and portable export", () =>
        {
            var compiled = fixture.RootElement.GetProperty("compiled");
            var original = new ConfigDocument(compiled.GetProperty("input").GetBytesFromBase64(), ConfigDialect.Cod2x);
            var store = new ConfigProfileStore(new FixtureProfileFiles(), ConfigDialect.Cod2x, timeProvider: new FixedClock());
            var profile = store.Create("Chains", "Chains", original);
            ImmutableArray<ConfigChain> chains =
            [
                new("HUD", "h", new ConfigChainBehavior.CommandCycle([["set cg_drawFPS Off"], ["set cg_drawFPS Simple"]]), "HUD cycle"),
                new("sens", "k", new ConfigChainBehavior.SettingCycle("sensitivity", ["1.5", "2"])),
                new("regular", "l", new ConfigChainBehavior.Commands(["+attack", "wait", "-attack"]))
            ];
            var edit = ConfigChains.Prepare(chains, profile);
            Sequence(compiled.GetProperty("config").GetBytesFromBase64(), edit.Document.Serialized);
            Sequence(compiled.GetProperty("script").GetBytesFromBase64(), edit.Script);
            using var expected = JsonDocument.Parse(compiled.GetProperty("definitions").GetBytesFromBase64());
            using var actual = JsonDocument.Parse(edit.Metadata.ChainDefinitions!.Value.AsMemory());
            Equal(true, JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
            Sequence(Texts(compiled.GetProperty("vars")), edit.RuntimeVariables);
            Sequence(Texts(compiled.GetProperty("conflicts")), edit.ConflictingKeys);
            try
            {
                _ = ConfigChains.Apply(edit, store);
                throw new InvalidDataException("Unconfirmed conflict replaced.");
            }
            catch (ConfigChainException error) { Equal(ConfigChainFailure.ConflictNeedsReview, error.Failure); }
            _ = ConfigChains.Apply(edit, store, replaceConflicts: true);
            var saved = store.Read("Chains");
            Sequence(compiled.GetProperty("portable").GetBytesFromBase64(), ConfigChains.WindowsExport(saved));
            Equal("HUD cycle", ConfigChains.OwnedChains(saved)[0].Name);
            var unsafeRead = saved with { Version = saved.Version with { Chain = [.. "echo substituted"u8] } };
            try
            {
                _ = ConfigChains.OwnedChains(unsafeRead);
                throw new InvalidDataException("Substituted companion accepted.");
            }
            catch (ConfigChainException error) { Equal(ConfigChainFailure.OpaqueOwnedFile, error.Failure); }
        });
    }

    private static void CheckupCases()
    {
        using var fixture = Fixtures("checkup");
        var model = new ConfigCheckup(ConfigCatalog.LoadBundled(), ConfigRules.LoadBundled(), ConfigOptimizer.LoadBundled());
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            Check("Swift Check-up " + row.GetProperty("id").GetString(), () =>
            {
                var store = new ConfigProfileStore(new FixtureProfileFiles(), ConfigDialect.Cod2x, timeProvider: new FixedClock());
                var metadata = new ConfigStudioMetadata("Checkup") { Locks = Texts(row.GetProperty("locks")).ToImmutableHashSet() };
                var profile = store.Create("Profile", "Checkup", new(row.GetProperty("input").GetBytesFromBase64(), ConfigDialect.Cod2x), metadata);
                var ids = Texts(row.GetProperty("ids"));
                var tune = Enum.Parse<ConfigTuneFor>(row.GetProperty("tune").GetString()!, ignoreCase: true);
                var mode = Enum.Parse<ConfigCatalogMode>(row.GetProperty("mode").GetString()!, ignoreCase: true);
                var report = model.Evaluate(profile, ids, tune, mode, new(8, -1, 42, Source: "synthetic"),
                    Texts(row.GetProperty("dismissed")).ToImmutableHashSet());
                Equal(row.GetProperty("unresolved").GetBoolean(), report.UnresolvedScripts);
                Sequence(Texts(row.GetProperty("limitations")), report.Limitations);
                var policy = row.GetProperty("policy").EnumerateArray().ToArray();
                Equal(policy.Length, report.Policy.Length);
                foreach (var (expected, actual) in policy.Zip(report.Policy))
                    Equal((expected.GetProperty("name").GetString(), expected.GetProperty("status").GetString(), expected.GetProperty("reason").GetString()),
                        (actual.Setting, Camel(actual.Status.ToString()), actual.Reason));
                var suggestions = row.GetProperty("suggestions").EnumerateArray().ToArray();
                Equal(suggestions.Length, report.Suggestions.Length);
                foreach (var (expected, actual) in suggestions.Zip(report.Suggestions))
                {
                    Equal((expected.GetProperty("id").GetString(), expected.GetProperty("area").GetString(), expected.GetProperty("title").GetString(),
                        expected.GetProperty("why").GetString(), expected.GetProperty("notice").GetString()),
                        (actual.Id, Camel(actual.Area.ToString()), actual.Title, actual.Why, actual.Notice));
                    Sequence(expected.GetProperty("changes").EnumerateArray().Select(change => (
                        change.GetProperty("name").GetString()!, change.GetProperty("old").GetString(), change.GetProperty("new").GetString()!)),
                        actual.Changes.Select(change => (change.Setting, change.OldValue, change.NewValue)));
                }
                Sequence(row.GetProperty("dials").EnumerateArray().Select(dial => (
                    dial.GetProperty("area").GetString()!, dial.GetProperty("value").ValueKind == JsonValueKind.Number ? dial.GetProperty("value").GetDouble() : (double?)null,
                    dial.GetProperty("unit").GetString())),
                    report.Dials.Select(dial => (Camel(dial.Area.ToString()), dial.Value, dial.Unit)));
                if (row.GetProperty("applied").ValueKind != JsonValueKind.Null)
                {
                    var receipt = model.Apply(report, report.Suggestions.Select(item => item.Id).ToHashSet(), store);
                    Sequence(row.GetProperty("applied").GetBytesFromBase64(), store.Read("Profile").Version.Config);
                    Equal(ConfigHistorySource.Checkup, receipt.HistoryEntry.Source);
                    _ = model.Undo(receipt, ids, store, mode);
                    Sequence(row.GetProperty("undone").GetBytesFromBase64(), store.Read("Profile").Version.Config);
                }
            });
    }

    private static ImmutableArray<string> Texts(JsonElement array) =>
        [.. array.EnumerateArray().Select(item => item.GetString()!)];
}
