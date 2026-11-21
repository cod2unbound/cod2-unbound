using System.Collections.Immutable;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed class ServersViewModel(WindowsLauncherSession session, ServerScope scope)
{
    private readonly ServerAdmission admission = new();
    internal ServerScope Scope { get; set; } = scope;
    internal ServerSort Sort { get; private set; } = ServerSort.Players;
    internal bool Ascending { get; private set; }
    internal string Search { get; set; } = "";
    internal ServerFilters Filters => session.ServerPreferences.Filters;
    internal bool IsFavorite(string address) => session.Library.Favorites.Contains(address);
    internal bool GameRunning => session.Game.State.Running;

    internal ImmutableArray<GameServer> Visible(ImmutableArray<GameServer>? live = null) =>
        ServerBrowser.Visible(session.Library, live ?? session.Library.Cache, Scope, Filters, Search, Sort, Ascending);

    internal void SortBy(ServerSort sort)
    {
        Ascending = Sort == sort ? !Ascending : sort != ServerSort.Players;
        Sort = sort;
    }

    internal Task RefreshAsync(IProgress<ServerRefreshState> progress, CancellationToken token) =>
        session.RefreshServersAsync(progress, token);

    internal Task SetFiltersAsync(ServerFilters filters) => session.SaveFiltersAsync(filters);
    internal Task FavoriteAsync(GameServer server) => session.ToggleFavoriteAsync(server.Address);
    internal Task<GameStartState> JoinAsync(ServerAddress address, GameEdition edition) => session.JoinAsync(address, edition);

    internal async Task<ServerAdmissionResult> CheckAsync(ServerAddress address)
    {
        var link = LaunchLink.Direct(address.Address, address.Password ?? "");
        var edition = session.Settings.Working.Engine;
        var installed = Enum.GetValues<GameEdition>().Where(session.HasClient).ToHashSet();
        var saved = session.Library.Cache.FirstOrDefault(row => row.Address == address.Address);
        var result = admission.Evaluate(link, saved, edition, installed, alwaysUseServerVersion: false);
        if (result.Action != ServerAdmissionAction.LookUp)
            return result;
        var checkedServer = await session.ServerQuery.ServerAsync(address.Address);
        admission.LookUpFinished(address.Address, cancelled: false);
        return admission.Evaluate(link, checkedServer, edition, installed, alwaysUseServerVersion: false);
    }
}

internal sealed record ServerRow(GameServer Server)
{
    public string Name => QuakeColors.Plain(Server.Name);
    public string Map => MapCatalog.DisplayName(Server.Map);
    public string Detail => GameModes.Short(Server.GameType) + " · " + ServerFacts.From(Server).VersionLabel +
        (Server.Mod.Length == 0 ? "" : " · " + Server.Mod);
    public string Players => Server.MaxPlayers > 0 ? $"{Server.PlayerCount}/{Server.MaxPlayers}" : "—";
    public string Ping => Server.Ping < 0 ? "—" : $"{Server.Ping} ms";
    public string Label => $"{Name}, {Map}, {Players}, {Ping}";
}
