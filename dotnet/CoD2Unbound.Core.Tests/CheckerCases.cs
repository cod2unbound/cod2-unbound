using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void CheckerCases()
    {
        using var fixture = Fixtures("checker");
        var catalog = ConfigCatalog.LoadBundled();
        var rules = ConfigRules.LoadBundled();
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            Check("Swift checker " + row.GetProperty("id").GetString(), () =>
            {
                var report = ConfigChecker.CheckAsync([.. row.GetProperty("input").GetBytesFromBase64()],
                    row.GetProperty("cod2x").GetBoolean() ? ConfigDialect.Cod2x : ConfigDialect.Stock,
                    row.GetProperty("policy").GetString(), catalog, rules).AsTask().GetAwaiter().GetResult();
                Equal(row.GetProperty("commands").GetInt32(), report.Commands);
                Sequence(row.GetProperty("lines").EnumerateArray().Select(item => item.GetString()), report.Lines.Select(item => (string?)item.Text));
                var expected = row.GetProperty("findings").EnumerateArray().Select(item => (
                        item.GetProperty("id").GetInt32(), item.GetProperty("end").GetInt32(),
                        item.GetProperty("line").GetInt32(), item.GetProperty("kind").GetInt32(),
                        item.GetProperty("title").GetString()!, item.GetProperty("detail").GetString()!,
                        item.GetProperty("note").GetString()!, item.GetProperty("fixLabel").GetString())).ToArray();
                var actual = report.Findings.Select(item => (item.Id, item.Range.End, item.Line, (int)item.Kind, item.Title,
                    item.Detail, item.Note, item.Fix?.Label)).ToArray();
                Equal(expected.Length, actual.Length);
                foreach (var (want, got) in expected.Zip(actual))
                    Equal(want, got);
                var ids = report.Fixable.Select(item => item.Id).ToHashSet();
                Sequence(row.GetProperty("fixed").GetBytesFromBase64(), report.Apply(ids));
                Sequence(row.GetProperty("shown").EnumerateArray().Select(item => (
                        item.GetProperty("text").GetString()!, item.GetProperty("note").GetString()!)),
                    report.Show(ids).Select(item => (item.Text, item.Note)));
            });
    }
}
