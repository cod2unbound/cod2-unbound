using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private MatchSummary? libraryMatch;

    internal void OpenCheckup(MatchSummary match)
    {
        libraryMatch = match;
        OpenCheckup();
    }
}
