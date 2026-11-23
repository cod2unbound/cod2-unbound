namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void ConfigChecksCases()
    {
        var catalog = ConfigCatalog.LoadBundled();
        var rules = ConfigRules.LoadBundled();
        Check("Checker stages individual and bulk edits on a copy, then undoes byte for byte", () =>
        {
            var input = ImportBytes("seta cl_maxpackets 999\r\nseta com_maxfps 333\r\n");
            var rule = rules.Rulesets.First(item => item.Id == "public-cod2x").Id;
            var report = Sync(ConfigChecker.CheckAsync(input, ConfigDialect.Cod2x, rule, catalog, rules));
            var copy = new ConfigCheckCopy(report);
            Equal(true, copy.Fixable.Length > 0);
            var fix = copy.Fixable[0];
            copy.Fix(fix.Id);
            Equal(false, copy.Findings.Contains(fix));
            Equal(true, copy.Lines(markedOnly: true).Any(item => item.Id == fix.Line));
            copy.FixAll();
            Equal(true, copy.HasEdits);
            Equal(0, copy.Fixable.Length);
            Sequence(input, report.Bytes);
            copy.Undo();
            Sequence(input, copy.Bytes);
        });
        Check("Checker refuses a stream beyond the engine limit before parsing it", () =>
        {
            using var atLimit = new MemoryStream(new byte[65_535]);
            Equal(65_535, Sync(ConfigCheckCopy.ReadAsync(atLimit)).Length);
            using var overLimit = new MemoryStream(new byte[65_536]);
            try { Sync(ConfigCheckCopy.ReadAsync(overLimit)); }
            catch (ConfigImportException error) when (error.Failure == ConfigImportFailure.TooLarge) { return; }
            throw new InvalidDataException("Oversized input was accepted.");
        });
        Check("Checker export writes exact bytes and preserves an occupied destination", () =>
        {
            var folder = Directory.CreateTempSubdirectory("cod2-check-export-");
            string path = Path.Combine(folder.FullName, "checked.cfg");
            var bytes = ImportBytes("seta sensitivity 2\r\n");
            try
            {
                ConfigCheckCopy.ExportAsync(path, bytes).GetAwaiter().GetResult();
                Sequence(bytes, File.ReadAllBytes(path));
                bool refused = false;
                try { ConfigCheckCopy.ExportAsync(path, ImportBytes("seta sensitivity 3")).GetAwaiter().GetResult(); }
                catch (IOException) { refused = true; }
                Equal(true, refused);
                Sequence(bytes, File.ReadAllBytes(path));
                Equal(1, folder.GetFiles().Length);
            }
            finally
            {
                File.Delete(path);
                folder.Delete();
            }
        });
        Check("History records a manual snapshot without rewriting the config or companion", () =>
        {
            var files = new FixtureProfileFiles();
            bool running = false;
            var studio = new ConfigStudioSession(files, ConfigDialect.Cod2x, () => running, TimeProvider.System);
            studio.Create("Main", "Main", new ConfigDocument(ImportBytes("seta sensitivity 2\r\n").AsSpan(), ConfigDialect.Cod2x));
            studio.Focus("Main");
            var current = studio.State.Current!;
            files.FailPath = ConfigPath;
            var snapshot = studio.SaveSnapshot(current);
            files.FailPath = null;
            Equal("Saved a snapshot", snapshot.Summary);
            Equal(ConfigHistorySource.You, snapshot.Source);
            Equal(true, snapshot.Before.Matches(current.Version));
            Equal(true, snapshot.After.Matches(current.Version));
            Equal(2, studio.State.History.Length);
            Equal(true, ConfigHistoryFilter.You.Includes(ConfigHistorySource.Restore));
            Equal(false, ConfigHistoryFilter.Game.Includes(ConfigHistorySource.You));
            Equal(true, ConfigHistoryFilter.Imports.Includes(ConfigHistorySource.Import));
            Equal(false, ConfigHistoryFilter.Imports.Includes(ConfigHistorySource.Migration));
            studio.Mutate("Main", store => store.Update("Main", current.Document.SetSetting("sensitivity", "3"), current.Version,
                null, "Changed mouse speed"));
            ProfileRejects(ConfigProfileFailure.Conflict, () => studio.SaveSnapshot(current));
            Equal(1, ConfigDiff.Changes(snapshot.After, studio.State.Current!.Version, studio.Dialect, catalog).Length);
            Equal(0, ConfigDiff.Changes(snapshot.After, current.Version, studio.Dialect, catalog).Length);
            running = true;
            ProfileRejects(ConfigProfileFailure.GameRunning, () => studio.SaveSnapshot(current));
        });
        Check("Check-up selects a saved match and falls back to the latest after removal", () =>
        {
            var older = new MatchSummary
            {
                SchemaVersion = 1, EndedAt = "2026-11-21T12:00:00Z", EndReason = "disconnect",
                Map = "mp_toujane", Mode = "sd", DurationSeconds = 300, ServerAddress = "127.0.0.1:28960",
                RuleHints = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
                FrameCount = 100, FrameTimeP50MS = 4, FrameTimeP99MS = 12, FrameTimeMaxMS = 190,
                FrameTimeHistogramLimitMS = 100, CapSampleFrames = 100, FramesUnderCap = 96, CapFPS = 250,
                PingSamples = 5, PingAverageMS = 25, PingMaximumMS = 31, PacketsReceived = 49, PacketsLost = 1,
                PacketLossPercent = 2, SnapshotsReceived = 5
            };
            var latest = older with { EndedAt = "2026-11-21T13:00:00Z", Map = "mp_carentan" };
            var selected = ConfigCheckupMatch.Select([older, latest], older.Id);
            Equal(older, selected.Shown);
            Equal(latest, selected.Latest);
            Equal(false, selected.RequestedMissing);
            Equal(25d, selected.Measurements.PingMS);
            Equal("Watch", selected.Connection().Status);
            Equal("Long pauses", selected.Movement().Status);
            var removed = ConfigCheckupMatch.Select([latest], older.Id);
            Equal(latest, removed.Shown);
            Equal(true, removed.RequestedMissing);
            Equal(null, ConfigCheckupMatch.Select([], null).Measurements.PingMS);
        });
    }
}
