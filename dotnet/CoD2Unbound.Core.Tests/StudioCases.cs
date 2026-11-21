using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void StudioCases()
    {
        using var fixture = Fixtures("studio");
        var catalog = ConfigCatalog.LoadBundled();
        foreach (var row in fixture.RootElement.GetProperty("bindings").EnumerateArray())
            Check("Swift binding groups and effective bindings " + row.GetProperty("mode").GetString(), () =>
            {
                bool cod2x = row.GetProperty("mode").GetString() == "cod2x";
                var document = new ConfigDocument(row.GetProperty("input").GetBytesFromBase64(),
                    cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock);
                var binds = new ConfigBindings(document, catalog, cod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock);
                Sequence(row.GetProperty("entries").EnumerateArray().Select(entry => (
                        entry.GetProperty("key").GetString()!, entry.GetProperty("binding").GetString()!,
                        entry.GetProperty("action").GetString(), entry.GetProperty("hasReleaseTail").GetBoolean())),
                    binds.Entries.Select(entry => (entry.Key.Spelling, entry.Text, entry.Action, entry.HasReleaseTail)));
                Sequence(row.GetProperty("actions").EnumerateArray().Select(action => (
                        action.GetProperty("name").GetString()!, action.GetProperty("title").GetString()!,
                        action.GetProperty("group").GetString()!)),
                    binds.Actions.Select(action => (action.Name, action.Title,
                        action.Group == ConfigBindingGroup.GameUI ? "gameUI" : action.Group.ToString().ToLowerInvariant())));
            });
        foreach (var row in fixture.RootElement.GetProperty("keyboard").EnumerateArray())
            Check("Swift SDL engine key " + row.GetProperty("sdl").GetInt32(), () =>
            {
                var expected = row.GetProperty("number");
                Equal(expected.ValueKind == JsonValueKind.Null ? (int?)null : expected.GetInt32(),
                    EngineInputMapping.FromSdlKey(row.GetProperty("sdl").GetInt32()));
            });
        var rules = ConfigRules.LoadBundled();
        foreach (var row in fixture.RootElement.GetProperty("rules").EnumerateArray())
            Check("Swift ruleset " + row.GetProperty("ruleset").GetString() + " " + row.GetProperty("setting").GetString(), () =>
            {
                var mode = row.GetProperty("mode").GetString() == "cod2x" ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock;
                var result = rules.Evaluate(row.GetProperty("setting").GetString()!, row.GetProperty("value").GetString(),
                    [row.GetProperty("ruleset").GetString()!], mode);
                Equal(row.GetProperty("status").GetString(), result.Status.ToString().ToLowerInvariant());
                Equal(row.GetProperty("reason").GetString(), result.Reason);
                Equal(row.GetProperty("owned").GetBoolean(), result.ServerOwned);
                Sequence(row.GetProperty("sources").EnumerateArray().Select(item => item.GetString()!), result.Sources.Select(item => item.SourceURL));
            });
    }
}
