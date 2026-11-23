using System.Collections.Immutable;
using System.Globalization;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed class HomeViewModel(WindowsLauncherSession session)
{
    internal GameSettings Settings => session.Settings.Working;
    internal GameSessionState Game => session.Game.State;
    internal bool CanLaunch => LaunchProblem is null;
    internal string? LaunchProblem => Game.Running ? "Call of Duty 2 is already running." :
        session.Setup.DataPath.Length == 0 ? "Choose a complete game data folder before launching." : null;

    internal ImmutableArray<GameServer> Recent => [.. session.Library.Recent.Take(3).Select(session.ServerForAddress)];
    internal GameServer? LastServer => session.Library.Recent.FirstOrDefault() is { } address
        ? session.ServerForAddress(address) : null;
    internal string? Played(string address, bool includeTime) => session.Library.Played.TryGetValue(address, out var date)
        ? includeTime ? TimeInSentence(date) : MatchFacts.Played(date, DateTimeOffset.UtcNow, TimeZoneInfo.Local, includeTime: false)
        : null;
    internal string? NewsChecked => session.Library.DispatchesCheckedAt is { } date ? TimeInSentence(date) : null;
    internal ImmutableArray<NewsDispatch> News => NewsFeed.Home(session.Version, session.Library.Dispatches,
        session.Library.News, session.Library.NewsNext);
    internal ImmutableArray<NewsDispatch> AllNews => NewsFeed.Home(session.Version, session.Library.Dispatches,
        session.Library.News, session.Library.NewsNext, rows: 12);
    internal ImmutableArray<ConfigProfile> Loadouts { get; private set; } = [];
    internal ImmutableArray<MatchSummary> Matches { get; private set; } = [];
    internal bool UnreadableMatches { get; private set; }

    private static string TimeInSentence(DateTimeOffset date)
    {
        string day = MatchFacts.Played(date, DateTimeOffset.UtcNow, TimeZoneInfo.Local, includeTime: false);
        var local = date.ToLocalTime();
        string when = day switch
        {
            "TODAY" => "today",
            "YESTERDAY" => "yesterday",
            _ => "on " + local.ToString("d MMMM", CultureInfo.CurrentCulture)
        };
        return when + " at " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    internal async Task RefreshAsync()
    {
        session.Setup.ChooseData(session.Setup.DataPath);
        Loadouts = await session.ReadLoadoutChoicesAsync();
        await session.Game.ReconcileAsync();
        await RefreshMatchesAsync();
    }

    internal async Task RefreshMatchesAsync()
    {
        Matches = [];
        UnreadableMatches = false;
        if (Settings.MatchSummariesEnabled)
        {
            try { Matches = await Task.Run(session.ReadMatches); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { UnreadableMatches = true; }
        }
    }

    internal Task<GameStartState> LaunchAsync() => session.LaunchAsync();

    internal Task SetLoadoutAsync(string id) => session.SelectLoadoutAsync(id);

    internal Task SetDisplayAsync(string mode) => session.UpdateSettingsAsync(Settings with { Fullscreen = mode });

    internal Task<MapPixels?> ReadArtAsync(string map) => Task.Run(() => session.ReadMapArtwork(map));
}
