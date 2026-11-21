using System.Collections.Immutable;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed class HomeViewModel(WindowsLauncherSession session)
{
    internal GameSettings Settings => session.Settings.Working;
    internal GameSessionState Game => session.Game.State;
    internal bool CanLaunch => LaunchProblem is null;
    internal string? LaunchProblem => Game.Running ? "Call of Duty 2 is already running." :
        session.Setup.Data.State != SetupDataState.Found ? "Choose a complete game data folder before launching." :
        !session.HasClient(Settings.Engine) ? "The game executable is missing." : null;

    internal ImmutableArray<GameServer> Recent => [.. session.Library.Recent.Take(3).Select(session.ServerForAddress)];
    internal GameServer? LastServer => session.Library.Recent.FirstOrDefault() is { } address
        ? session.ServerForAddress(address) : null;
    internal string? LastPlayed => LastServer is { } server && session.Library.Played.TryGetValue(server.Address, out var date)
        ? "Played " + MatchFacts.Played(date, TimeProvider.System.GetUtcNow(), TimeZoneInfo.Local, includeTime: true).ToLowerInvariant()
        : null;
    internal ImmutableArray<NewsDispatch> News => NewsFeed.Home(session.Version, session.Library.Dispatches,
        session.Library.News, session.Library.NewsNext);
    internal ImmutableArray<ConfigProfile> Loadouts { get; private set; } = [];
    internal MatchSummary? LastMatch { get; private set; }
    internal bool UnreadableMatches { get; private set; }

    internal async Task RefreshAsync()
    {
        session.Setup.ChooseData(session.Setup.DataPath);
        Loadouts = await session.ReadLoadoutChoicesAsync();
        await session.Game.ReconcileAsync();
        if (Settings.MatchSummariesEnabled)
        {
            try { LastMatch = (await Task.Run(session.ReadMatches)).LastOrDefault(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { UnreadableMatches = true; }
        }
    }

    internal Task LaunchAsync(string? address = null) => address is null
        ? session.LaunchAsync() : session.JoinAsync(new(address, null));

    internal Task SetLoadoutAsync(string id) => session.SelectLoadoutAsync(id);

    internal Task SetDisplayAsync(string mode) => session.UpdateSettingsAsync(Settings with { Fullscreen = mode });

    internal Task<MapPixels?> ReadArtAsync(string map) => Task.Run(() => session.ReadMapArtwork(map));
}
