using System.Collections.Immutable;
using System.Globalization;

namespace CoD2Unbound.Core;

public enum JumpGroup { Servers, Loadouts, Settings, Bindings, Actions, ConfigSettings }

public sealed record JumpTarget(JumpGroup Group, string Id, string Title, string Detail = "",
    string SearchName = "", string Keys = "", bool Enabled = true);

public static class JumpIndex
{
    private static readonly CompareInfo Comparison = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static ImmutableArray<JumpTarget> Search(IEnumerable<JumpTarget> entries, string query, bool settingsOnly = false)
    {
        string text = query.Trim();
        var terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (text.Length == 0)
            return [.. settingsOnly ? entries.Where(item => item.Group == JumpGroup.ConfigSettings).Take(8) :
                entries.Where(item => item.Group == JumpGroup.Actions)];

        return [.. entries.Where(item => (item.Group == JumpGroup.ConfigSettings) == settingsOnly
                    && terms.All(term => Contains(item.Title, term) || Contains(item.SearchName, term)))
            .GroupBy(item => item.Group).OrderBy(group => group.Key)
            .SelectMany(group => group.OrderBy(item => Rank(item, text))
                .ThenBy(item => item.Title, StringComparer.InvariantCultureIgnoreCase)
                .Take(settingsOnly ? 30 : 8))];
    }

    public static JumpTarget Server(GameServer server)
    {
        string map = MapCatalog.DisplayName(server.Map);
        string players = server.MaxPlayers > 0 ? $"{server.PlayerCount}/{server.MaxPlayers}" : $"{server.PlayerCount}";
        string ping = server.Ping >= 0 ? $"{server.Ping} ms" : "—";
        string name = QuakeColors.Plain(server.Name);
        return new(JumpGroup.Servers, server.Address, name.Length == 0 ? server.Address : name,
            $"{map} · {players} · {ping}", $"{map} {server.Map}");
    }

    public static JumpTarget Loadout(ConfigProfile profile) => new(JumpGroup.Loadouts, profile.Id, profile.DisplayName,
        profile.IsActive ? "Loadout · used for your next game" : "Loadout");

    public static ImmutableArray<JumpTarget> GameActions(bool running, bool closingGame) =>
    [
        new(JumpGroup.Actions, nameof(ShellCommand.ReturnToGame), "Return to game", Enabled: running),
        new(JumpGroup.Actions, nameof(ShellCommand.CloseGame), "Close game", Enabled: running && !closingGame)
    ];

    public static IEnumerable<JumpTarget> HostingActions(Func<ShellCommand, bool> canRun)
    {
        yield return Action(ShellCommand.ExportHostedServer, "Export for another machine…", "hosting dedicated server");
        yield return Action(ShellCommand.RevealHostedServer, "Show its folder in File Explorer", "hosting hosted server folder");
        yield return Action(ShellCommand.DeleteHostedServer, "Delete server…", "hosting hosted server");
        yield return Action(ShellCommand.RevealHostedExport, "Show exported server folder", "hosting export file explorer");
        yield return Action(ShellCommand.RetryHostedExport, "Try server export again", "hosting retry failed export");

        JumpTarget Action(ShellCommand command, string title, string searchName) =>
            new(JumpGroup.Actions, command.ToString(), title, SearchName: searchName,
                Keys: LauncherShortcuts.Keys(command), Enabled: canRun(command));
    }

    private static int Rank(JumpTarget item, string query)
    {
        if (Comparison.Compare(item.Title, query, Options) == 0 || Comparison.Compare(item.SearchName, query, Options) == 0)
            return 0;
        if (Comparison.IsPrefix(item.Title, query, Options))
            return 1;
        return Contains(item.Title, query) ? 2 : 3;
    }

    private static bool Contains(string value, string query) => Comparison.IndexOf(value, query, Options) >= 0;
}
