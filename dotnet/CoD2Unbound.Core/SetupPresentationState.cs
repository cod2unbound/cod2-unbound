namespace CoD2Unbound.Core;

public static class SetupPlayer
{
    public const string Unknown = "Unknown Soldier";

    public static string? NameProblem(string name) => GameSettings.SafeValue(name)
        ? null : "A name can’t contain quotes, semicolons, plus signs or backslashes, and must fit in the game’s name field.";

    public static string DisplayMode(string mode) => mode switch
    {
        "exclusive" => "Full screen",
        "windowed" => "Window",
        _ => "Borderless"
    };
}

public enum LaunchRecoveryKind { Folder, MissingGame, Other }

public sealed record LaunchRecovery(LaunchRecoveryKind Kind, string Message, string? Path)
{
    public static LaunchRecovery Classify(string path, bool dataComplete, bool gameInstalled, string message)
    {
        if (!dataComplete)
            return new(LaunchRecoveryKind.Folder, "The game data folder has moved or its drive isn’t connected.", path);
        if (!gameInstalled)
            return new(LaunchRecoveryKind.MissingGame,
                "This copy of CoD2 Unbound is missing the game itself. Download CoD2 Unbound again to put it back.", null);
        return new(LaunchRecoveryKind.Other, message, null);
    }
}
