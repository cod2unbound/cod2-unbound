namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void SetupCases()
    {
        using var fixture = Fixtures("setup");
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            Check("setup archive acceptance", () =>
            {
                var files = new ArchiveFiles();
                var indices = row.GetProperty("indices").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                foreach (int index in indices)
                    files.Present.Add(("root", FormattableString.Invariant($"main/iw_{index:D2}.iwd")));
                var result = GameArchives.Survey(files, "root");
                Equal(row.GetProperty("accepted").GetBoolean(), result.IsComplete);
                Equal(indices.Length, result.Found);
            });
        }

        Check("data preflight checks all sixteen readable archives and preserves a working selection", () =>
        {
            var files = new ArchiveFiles();
            var saved = "d:/archive";
            var discovered = "c:/games";
            for (int i = 0; i < 16; i++)
                files.Present.Add((discovered, FormattableString.Invariant($"main/iw_{i:D2}.iwd")));
            files.Present.Add((saved, "main/iw_00.iwd"));
            var partial = GameArchives.Survey(files, saved);
            Equal(1, partial.Found);
            Equal("main/iw_01.iwd", partial.Missing[0]);
            Equal(15, partial.Missing.Length);
            Equal(discovered, GameArchives.RecoverSelection(files, saved, [discovered]));
            Equal(discovered, GameArchives.RecoverSelection(files, discovered, [saved]));
            Equal(null, GameArchives.RecoverSelection(files, saved, []));
            Equal(true, GameArchives.Survey(files, discovered).IsComplete);
        });
    }

    private sealed class ArchiveFiles : IGameFiles
    {
        public HashSet<(string Root, string Path)> Present { get; } = [];
        public bool IsRegularFile(string root, string relativePath) => Present.Contains((root, relativePath));
    }
}
