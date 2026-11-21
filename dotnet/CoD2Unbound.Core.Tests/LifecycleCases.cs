using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void LifecycleCases()
    {
        using var fixture = Fixtures("lifecycle");
        foreach (var row in fixture.RootElement.GetProperty("outcomes").EnumerateArray())
            Check("Swift game exit classification", () =>
            {
                int? code = row.GetProperty("code").ValueKind == JsonValueKind.Null ? null : row.GetProperty("code").GetInt32();
                var outcome = GameExit.Classify(code, row.GetProperty("hasLog").GetBoolean() ? "/session" : null,
                    row.GetProperty("hasCrash").GetBoolean() ? "/crash" : null);
                Equal(row.GetProperty("unexpected").GetBoolean(), outcome.Unexpected);
                Equal(row.GetProperty("report").GetString(), outcome.Report);
            });

        foreach (var row in fixture.RootElement.GetProperty("records").EnumerateArray())
            Check("Swift match summaries " + row.GetProperty("name").GetString(), () =>
            {
                using var stream = new MemoryStream(row.GetProperty("input").GetBytesFromBase64(), writable: false);
                var results = MatchSummaries.Read(stream);
                Sequence(row.GetProperty("matches").EnumerateArray().Select(item => (
                    item.GetProperty("id").GetString()!, item.GetProperty("exact").GetBoolean(),
                    item.GetProperty("summary").GetString(), item.GetProperty("times").GetString())),
                    results.Select(match => (match.Id, match.FrameQuantilesAreExact, match.FreezeSummary, match.FreezeTimes)));
            });

        Check("Game and hosting quit answers are scoped to this quit attempt", () =>
        {
            var quit = new LauncherQuit();
            Equal(QuitPrompt.Game, quit.Next(true, true));
            quit.AnswerGame(GameQuitAnswer.LauncherOnly);
            Equal(QuitPrompt.Servers, quit.Next(true, true));
            quit.Cancel();
            Equal(QuitPrompt.Game, quit.Next(true, true));
            quit.AnswerGame(GameQuitAnswer.Both);
            Equal(QuitPrompt.StopGame, quit.Next(true, true));
            Equal(QuitPrompt.Servers, quit.Next(false, true));
            quit.AnswerServers();
            Equal(QuitPrompt.StopServers, quit.Next(false, true));
            Equal(QuitPrompt.Exit, quit.Next(false, false));
        });
    }
}
