using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public readonly record struct GameExit(bool Unexpected, string? Report)
{
    public static GameExit Classify(int? code, string? sessionLog, string? crashReport) => code switch
    {
        0 or 1 => new(false, null),
        null => new(crashReport is not null, crashReport),
        _ => new(true, sessionLog ?? crashReport)
    };
}

public sealed record CrashFile(string Name, string Path, DateTimeOffset Modified);

public static class CrashFiles
{
    // The adapter lists only regular files immediately under the verified game home.
    public static ImmutableArray<CrashFile> Since(IEnumerable<CrashFile> files, DateTimeOffset started) =>
        [.. files.Where(file => file.Modified >= started && IsReportName(file.Name))
            .OrderBy(file => file.Modified).ThenBy(file => file.Name, StringComparer.Ordinal)];

    public static bool IsReportName(string name) =>
        !name.AsSpan().ContainsAny("/\\:\0") &&
        ((name.EndsWith(".txt", StringComparison.Ordinal) && HasPrefix(name, "cod2_crash_", "cod2_freeze_")) ||
         (name.EndsWith(".log", StringComparison.Ordinal) && HasPrefix(name, "cod2x_crash.", "cod2x_freeze.")));

    private static bool HasPrefix(string name, string first, string second) =>
        name.StartsWith(first, StringComparison.Ordinal) || name.StartsWith(second, StringComparison.Ordinal);
}

public enum GameQuitAnswer { LauncherOnly, Both }
public enum QuitPrompt { Game, StopGame, Servers, StopServers, Exit }

public sealed class LauncherQuit
{
    private GameQuitAnswer? game;
    private bool servers;

    public QuitPrompt Next(bool hasGame, bool hasHostedServers)
    {
        if (hasGame && game is null)
            return QuitPrompt.Game;
        if (hasHostedServers && !servers)
            return QuitPrompt.Servers;
        if (hasGame && game == GameQuitAnswer.Both)
            return QuitPrompt.StopGame;
        return hasHostedServers ? QuitPrompt.StopServers : QuitPrompt.Exit;
    }

    public void AnswerGame(GameQuitAnswer answer) => game = answer;
    public void AnswerServers() => servers = true;
    public void Cancel() { game = null; servers = false; }
}

public sealed record HostedClosePrompt(string Title, string Message, string Action)
{
    public static HostedClosePrompt For(IReadOnlyList<string> serverNames) => serverNames.Count == 1
        ? new($"“{serverNames[0]}” is still running",
            "Closing CoD2 Unbound stops it. Players on it are disconnected.", "Stop server and close")
        : new($"{serverNames.Count} servers are still running",
            "Closing CoD2 Unbound stops them. Players on them are disconnected.", "Stop servers and close");
}
