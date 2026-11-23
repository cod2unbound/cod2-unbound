using System.Collections.Immutable;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed partial class ConfigsViewModel
{
    internal bool MatchSummariesEnabled => session.Settings.Working.MatchSummariesEnabled;
    internal ImmutableArray<MatchSummary> ReadCheckupMatches() => session.ReadMatches();

    internal ConfigCheckupReceipt ApplyCheckup(ConfigCheckup checkup, ConfigCheckupReport report, string suggestionId) =>
        Studio.Mutate(report.ProfileId, store => checkup.Apply(report, new HashSet<string> { suggestionId }, store));

    internal void UndoCheckup(ConfigCheckup checkup, ConfigCheckupReceipt receipt, ConfigCheckupReport report) =>
        Studio.Mutate(receipt.ProfileId, store => checkup.Undo(receipt, report.RulesetIds, store, report.Mode));
}
