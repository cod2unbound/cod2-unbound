using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public enum MatchLibraryState { Ready, OffWithHistory, Off, Empty, Unavailable }

public sealed record MatchLibrary(MatchLibraryState State, ImmutableArray<MatchSummary> Matches)
{
    public static MatchLibrary Load(bool enabled, Func<ImmutableArray<MatchSummary>> read)
    {
        ImmutableArray<MatchSummary> matches;
        try { matches = read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(MatchLibraryState.Unavailable, []);
        }

        var state = (enabled, matches.IsEmpty) switch
        {
            (true, false) => MatchLibraryState.Ready,
            (false, false) => MatchLibraryState.OffWithHistory,
            (true, true) => MatchLibraryState.Empty,
            _ => MatchLibraryState.Off
        };
        return new(state, [.. matches.Reverse()]);
    }

    public bool CanDelete(bool gameRunning) => !gameRunning && !Matches.IsEmpty;

    public string DeleteTitle => Matches.Length switch
    {
        0 => "Delete match summaries?",
        1 => "Delete your match summary?",
        _ => $"Delete all {Matches.Length} match summaries?"
    };
}
