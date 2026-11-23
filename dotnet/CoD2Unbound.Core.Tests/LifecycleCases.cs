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
            Equal(QuitPrompt.Servers, quit.Next(true, true));
            quit.Cancel();
            Equal(QuitPrompt.Game, quit.Next(true, true));
            quit.AnswerGame(GameQuitAnswer.Both);
            quit.AnswerServers();
            Equal(QuitPrompt.StopGame, quit.Next(true, true));
            Equal(QuitPrompt.StopServers, quit.Next(false, true));
            Equal(QuitPrompt.Exit, quit.Next(false, false));
        });

        Check("Hosted close prompt describes a named server or a group", () =>
        {
            var single = HostedClosePrompt.For(["Night Sessions"]);
            Equal("“Night Sessions” is still running", single.Title);
            Equal("Stop server and close", single.Action);
            var multiple = HostedClosePrompt.For(["Night Sessions", "Sunday Rifles"]);
            Equal("2 servers are still running", multiple.Title);
            Equal("Closing CoD2 Unbound stops them. Players on them are disconnected.", multiple.Message);
            Equal("Stop servers and close", multiple.Action);
        });

        Check("Playing time follows the active process and clamps a future creation time", () =>
        {
            var now = DateTimeOffset.UnixEpoch.AddHours(2);
            var attached = new GameProcessIdentity(1, now.AddHours(-2));
            var owned = new GameProcessIdentity(2, now.AddMinutes(-12));
            var state = new GameSessionState(now, [attached, owned], null,
                Activity: new(owned, "localhost:28960", null));
            Equal(owned, state.ActiveGame);
            Equal("Playing for 12 min", state.PlayingFor(now));
            Equal("Playing for 1 h 2 min", state.PlayingFor(now.AddMinutes(50)));
            Equal("Playing for less than a minute", state.PlayingFor(now.AddMinutes(-13)));
            Equal(null, (state with { Games = [], Activity = null }).PlayingFor(now));
        });
    }
}
