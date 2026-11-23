using System.Collections.Immutable;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void MatchLibraryCases()
    {
        var first = new MatchSummary
        {
            SchemaVersion = 1, Map = "mp_toujane", Mode = "tdm", DurationSeconds = 90,
            EndedAt = "2026-11-10T21:14:00Z", EndReason = "", ServerAddress = "",
            RuleHints = ImmutableDictionary<string, string>.Empty, FrameCount = 0, CapSampleFrames = 0, CapFPS = 0,
            PingSamples = 1, SnapshotsReceived = 1, PingAverageMS = 38, PingMaximumMS = 38,
            PacketsReceived = 0, PacketsLost = 0
        };
        var second = first with { Map = "mp_carentan", PingAverageMS = 73, PingMaximumMS = 73 };

        Check("match library keeps exact records in recent-first order", () =>
        {
            var history = MatchLibrary.Load(true, () => [first, second]);
            Equal(MatchLibraryState.Ready, history.State);
            Equal(true, ReferenceEquals(second, history.Matches[0]));
            Equal(true, ReferenceEquals(first, history.Matches[1]));
            Equal("Delete all 2 match summaries?", history.DeleteTitle);
            Equal(true, history.CanDelete(gameRunning: false));
            Equal(false, history.CanDelete(gameRunning: true));
        });

        Check("turning off match summaries keeps existing history and its deletion action", () =>
        {
            var history = MatchLibrary.Load(false, () => [first]);
            Equal(MatchLibraryState.OffWithHistory, history.State);
            Sequence(new[] { first }, history.Matches);
            Equal("Delete your match summary?", history.DeleteTitle);
            Equal(true, history.CanDelete(gameRunning: false));
        });

        Check("match library distinguishes first use, disabled recording, and an unreadable history", () =>
        {
            var empty = MatchLibrary.Load(true, () => []);
            Equal(MatchLibraryState.Empty, empty.State);
            Equal(false, empty.CanDelete(gameRunning: false));
            var off = MatchLibrary.Load(false, () => []);
            Equal(MatchLibraryState.Off, off.State);
            Equal(false, off.CanDelete(gameRunning: false));
            foreach (bool enabled in new[] { true, false })
            {
                var failed = MatchLibrary.Load(enabled, () => throw new UnauthorizedAccessException());
                Equal(MatchLibraryState.Unavailable, failed.State);
                Equal(false, failed.CanDelete(gameRunning: false));
            }
        });

        Check("demo rename keeps the extension and enforces the recording name rules", () =>
        {
            var file = new MediaFile("first.DM_1", null, null);
            Equal("last-round.DM_1", MediaLibrary.Rename(file, " last-round ").FileName);
            Equal("Choose a name that doesn’t start with a dot, so the recording stays visible.",
                MediaLibrary.Rename(file, ".hidden").Problem);
            Equal("Enter a name without slashes or colons.", MediaLibrary.Rename(file, "path/elsewhere").Problem);
            Equal("Use only letters, numbers, dots, underscores or hyphens, so the game can play it.",
                MediaLibrary.Rename(file, "final round").Problem);
            Equal<string?>(null, MediaLibrary.Rename(file, "NUL").FileName);
        });
    }
}
