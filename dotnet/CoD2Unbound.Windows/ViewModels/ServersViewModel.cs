using System.Collections.Immutable;
using System.ComponentModel;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed class ServersViewModel(WindowsLauncherSession session, ServerScope scope)
{
    internal ServerScope Scope { get; set; } = scope;
    internal ServerSort Sort { get; private set; } = ServerSort.Ping;
    internal bool Ascending { get; private set; } = true;
    internal string Search { get; set; } = "";
    internal ServerFilters Filters { get; private set; } = session.ServerPreferences.Filters;
    internal bool IsFavorite(string address) => session.Library.Favorites.Contains(address);
    internal DateTimeOffset? CachedAt => session.Library.CachedAt;
    internal bool GameRunning => session.Game.State.Running;

    internal ImmutableArray<GameServer> Visible(ImmutableArray<GameServer>? live = null) =>
        ServerBrowser.Visible(session.Library, live ?? session.Library.Cache, Scope, Filters, Search, Sort, Ascending);

    internal void SortBy(ServerSort sort)
    {
        Ascending = Sort == sort ? !Ascending : sort != ServerSort.Ping;
        Sort = sort;
    }

    internal Task RefreshAsync(IProgress<ServerRefreshState> progress, CancellationToken token) =>
        session.RefreshServersAsync(Scope, progress, token);

    internal Task SetFiltersAsync(ServerFilters filters)
    {
        Filters = filters;
        return session.SaveFiltersAsync(filters);
    }
    internal Task FavoriteAsync(GameServer server) => session.ToggleFavoriteAsync(server.Address);
}

internal sealed class ServerRow(GameServer server, bool favorite) : INotifyPropertyChanged
{
    public GameServer Server { get; private set; } = server;
    public bool Favorite { get; private set; } = favorite;
    public string Mod => Server.Mod;
    public string Name => QuakeColors.Plain(Server.Name);
    public string Map => MapCatalog.DisplayName(Server.Map);
    public string MapMode => Map + " · " + (GameModes.Short(Server.GameType) is "SD" ? "S&D" : GameModes.Short(Server.GameType));
    public string Edition => ServerFacts.From(Server).VersionLabel;
    public Microsoft.UI.Xaml.Visibility Lock => Views.PagePresentation.IsVisible(Server.PasswordRequired);
    public Microsoft.UI.Xaml.Visibility ModVisibility => Views.PagePresentation.IsVisible(Server.Mod.Length != 0);
    public string Players => Server.MaxPlayers > 0 ? $"{Server.PlayerCount}/{Server.MaxPlayers}" : "—";
    public string Ping => Server.Ping < 0 ? "—" : $"{Server.Ping} ms";
    public string Label => string.Join(", ", new[]
    {
        Name, Map, GameModes.Long(Server.GameType), Edition,
        Server.Mod.Length == 0 ? null : Server.Mod,
        Server.MaxPlayers > 0 ? $"{Server.PlayerCount} of {Server.MaxPlayers} players" : null,
        Server.Ping >= 0 ? $"{Server.Ping} milliseconds" : null,
        Server.PasswordRequired ? "needs a password" : null,
        Favorite ? "favorite" : null
    }.OfType<string>());

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool Update(ServerRow incoming)
    {
        if (Server == incoming.Server && Favorite == incoming.Favorite)
            return false;
        Server = incoming.Server;
        Favorite = incoming.Favorite;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        return true;
    }
}
