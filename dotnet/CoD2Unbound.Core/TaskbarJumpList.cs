using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public sealed record TaskbarActivation(ShellCommand Command, string? Address = null);
public sealed record TaskbarEntry(string Title, string Arguments);

public static class TaskbarJumpList
{
    private const string LaunchLast = "--launch-last-server";
    private const string OpenMenu = "--open-game-menu";
    private const string Connect = "--connect-to-address";
    private const string JoinRecent = "--join-recent=";

    public static ImmutableArray<TaskbarEntry> Tasks { get; } =
    [
        new("Launch on last server", LaunchLast),
        new("Open game menu", OpenMenu),
        new("Connect to an address…", Connect)
    ];

    public static ImmutableArray<TaskbarEntry> Recent(IEnumerable<GameServer> servers, IEnumerable<string> removedArguments)
    {
        var seen = removedArguments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<TaskbarEntry>();
        foreach (var server in servers)
        {
            if (ServerBrowser.TryAddress(server.Address) is not { } address || !seen.Add(JoinRecent + address))
                continue;
            string title = QuakeColors.Plain(server.Name).Trim();
            result.Add(new(title.Length == 0 ? address : title, JoinRecent + address));
            if (result.Count == 5)
                break;
        }
        return result.ToImmutable();
    }

    public static TaskbarActivation? Parse(string argument) => argument switch
    {
        LaunchLast => new(ShellCommand.Launch),
        OpenMenu => new(ShellCommand.OpenGameMenu),
        Connect => new(ShellCommand.Connect),
        _ when argument.StartsWith(JoinRecent, StringComparison.Ordinal) && !argument.Any(char.IsWhiteSpace) &&
            ServerBrowser.TryAddress(argument[JoinRecent.Length..]) is { } address => new(ShellCommand.Launch, address),
        _ => null
    };
}
