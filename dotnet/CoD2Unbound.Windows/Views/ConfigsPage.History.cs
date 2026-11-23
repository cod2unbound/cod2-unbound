using System.Collections.Immutable;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private static readonly string[] historyFilters = ["All", "You", "Imports", "Check-up", "Game"];
    private ConfigHistoryFilter historyFilter;
    private string? historyProfile;
    private string? historySelection;
    private string? historyRestored;
    private Button? historySnapshotButton;
    private Border? historySnapshotTarget;

    private void AddStudioTabs(SelectorBar tabs, ConfigProfileRead profile)
    {
        var snapshot = Action("Save a snapshot now", async () =>
        {
            if (!CanEdit || model.State.Current is not { } current || current.Profile.Id != profile.Profile.Id)
                return;
            model.Studio.SaveSnapshot(current);
            await RefreshStudioAsync();
            historySnapshotButton?.Focus(FocusState.Programmatic);
            Announce("Saved a snapshot.");
        });
        snapshot.Content = IconLabel("Bookmark", "Save a snapshot now", 16);
        AutomationProperties.SetName(snapshot, "Save a snapshot now");
        snapshot.HorizontalAlignment = HorizontalAlignment.Right;
        historySnapshotButton = snapshot;
        var target = new Border { Child = snapshot, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right };
        historySnapshotTarget = target;
        Detail.Children.Add(CheckPair(tabs, target, 565, rightAuto: true));
        UpdateHistorySnapshotButton();
    }

    private void UpdateHistorySnapshotButton()
    {
        if (historySnapshotButton is not { } button)
            return;
        historySnapshotTarget!.Visibility = IsVisible(studioTab == 3);
        button.IsEnabled = CanEdit;
        ToolTipService.SetToolTip(historySnapshotTarget, model.State.ReadOnly
            ? ConfigProfileException.ReadOnlyWhilePlaying
            : "Save how this loadout is set up now as a snapshot");
    }

    private void ShowHistory(ConfigStudioState state, ConfigProfileRead profile)
    {
        if (historyProfile != profile.Profile.Id)
        {
            historyProfile = profile.Profile.Id;
            historySelection = historyRestored = null;
        }
        var filters = new ComboBox
        {
            ItemsSource = historyFilters, SelectedIndex = (int)historyFilter, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(filters, "Show changes from");
        var filterLabel = CheckStrong("Show changes from");
        filterLabel.VerticalAlignment = VerticalAlignment.Center;
        studioBody.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { filterLabel, filters }
        });
        var area = new StackPanel();
        studioBody.Children.Add(area);
        filters.SelectionChanged += (_, _) =>
        {
            if (filters.SelectedIndex < 0)
                return;
            historyFilter = (ConfigHistoryFilter)filters.SelectedIndex;
            Draw();
        };
        Draw();

        void Draw()
        {
            area.Children.Clear();
            var entries = state.History.Where(entry => historyFilter.Includes(entry.Source)).ToArray();
            if (!entries.Any(entry => entry.Id == historySelection))
                historySelection = historyRestored = null;
            var timeline = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 540,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, TabNavigation = KeyboardNavigationMode.Once };
            AutomationProperties.SetName(timeline, "Snapshots");
            timeline.Items.Add(HistoryRow("Check", $"How {profile.Profile.DisplayName} is set up now",
                profile.Profile.IsActive ? "Everything the next game will use." : "Everything the game uses when you choose this loadout.",
                "Now", state.History.FirstOrDefault()?.Date));
            foreach (var entry in entries)
            {
                var changes = HistoryChanges(entry.Before, entry.After);
                string detail = entry.Summary == "Saved a snapshot" ? "A point to come back to." : ConfigDiff.Sentence(changes);
                var (icon, source) = HistorySource(entry.Source);
                timeline.Items.Add(HistoryRow(icon, HistoryTitle(entry), detail, source, entry.Date));
            }
            var timelineArea = new StackPanel { Spacing = 8, Children = { timeline } };
            if (entries.Length == 0)
                timelineArea.Children.Add(Caption(historyFilter == ConfigHistoryFilter.All
                    ? "No snapshots yet. Every change you save here adds one."
                    : $"No snapshots from {historyFilters[(int)historyFilter].ToLowerInvariant()} yet."));
            int keep = ConfigProfileStore.HistoryRetention;
            timelineArea.Children.Add(Caption($"CoD2 Unbound keeps the last {keep} snapshots of each loadout. This one has {state.History.Length} of {keep}. Older ones are removed on their own."));

            var compare = new StackPanel();
            area.Children.Add(CheckPair(timelineArea, CheckCard(compare), 755));
            timeline.SelectedIndex = Array.FindIndex(entries, entry => entry.Id == historySelection) + 1;
            timeline.SelectionChanged += (_, _) => Compare();
            Compare();

            void Compare()
            {
                int index = timeline.SelectedIndex - 1;
                var entry = index < 0 ? null : entries[index];
                historySelection = entry?.Id;
                compare.Children.Clear();
                compare.Children.Add(entry is null
                    ? new StackPanel { Spacing = 8, Children =
                    {
                        new TextBlock { Text = "This is now", Style = StyleFor("EmptyHeading") },
                        Caption("Pick an earlier snapshot to see what has changed since then, or to go back to it.")
                    } }
                    : HistoryComparison(state, profile, entry));
            }
        }
    }

    private static ListViewItem HistoryRow(string icon, string title, string detail, string source, DateTimeOffset? date)
    {
        var facts = new StackPanel { Spacing = 4, Children =
        {
            CheckStrong(title), Caption(detail), MonoCaption(date is { } value ? $"{source} · {HistoryDate(value)}" : source)
        } };
        var row = Columns(12, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        Put(row, 0, Fluent(icon, 18));
        Put(row, 1, facts);
        var item = new ListViewItem { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, $"{title}. {detail} {source}. {(date is { } time ? HistoryDate(time) : "")}");
        return item;
    }

    private FrameworkElement HistoryComparison(ConfigStudioState state, ConfigProfileRead profile, ConfigHistoryEntry entry)
    {
        var stamp = MonoCaption(HistoryDate(entry.Date));
        var compare = new StackPanel { Spacing = 12, Children =
        {
            Caption("Compare with now"), CheckStrong(HistoryTitle(entry)), stamp
        } };
        var changes = HistoryChanges(entry.After, profile.Version);
        compare.Children.Add(HistoryValues("Setting", "Then", "Now", header: true));
        if (changes.IsEmpty)
            compare.Children.Add(HistoryValues("No differences", "—", "—"));
        foreach (var change in changes)
            compare.Children.Add(HistoryValues(change.Label, change.Before.Length == 0 ? "—" : change.Before, change.After));
        if (historyRestored == entry.Id)
            compare.Children.Add(CheckNotice(InfoBarSeverity.Success, "Restored", "Your previous state is saved as a snapshot."));
        compare.Children.Add(Caption("Restoring saves how things are now as a new snapshot first, so you can always come back."));

        var restore = Action("Restore this snapshot", () => RestoreSnapshotAsync(profile, entry, ConfigSnapshotRevision.After));
        restore.Style = StyleFor("LauncherAccentButton");
        restore.IsEnabled = CanEdit && !entry.After.Matches(profile.Version);
        var more = new Button { Content = Fluent("More", 16), IsEnabled = CanEdit };
        AutomationProperties.SetName(more, "More actions for this snapshot");
        var menu = new MenuFlyout();
        bool earlier = ConfigHistoryFilters.HasEarlierRevision(entry, state.History);
        menu.Items.Add(MenuItem("Restore the state before this change",
            () => RestoreSnapshotAsync(profile, entry, ConfigSnapshotRevision.Before), earlier && !entry.Before.Matches(profile.Version)));
        more.Flyout = menu;

        var actions = Columns(8, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        restore.HorizontalAlignment = HorizontalAlignment.Right;
        Put(actions, 0, more);
        Put(actions, 1, restore);
        compare.Children.Add(actions);
        return compare;
    }

    private async Task RestoreSnapshotAsync(ConfigProfileRead profile, ConfigHistoryEntry entry, ConfigSnapshotRevision revision)
    {
        if (!CanEdit)
            return;
        var dialog = Dialog("Restore this loadout?", Body($"Restore {(revision == ConfigSnapshotRevision.Before ? "the state before" : "the state saved in")} “{HistoryTitle(entry)}”?"),
            "Restore");
        dialog.DefaultButton = ContentDialogButton.Close;
        TrackGameState(dialog, () => dialog.IsPrimaryButtonEnabled = CanEdit);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !CanEdit)
            return;
        model.RestoreHistory(profile, entry, revision);
        historyRestored = entry.Id;
        await RefreshStudioAsync();
        Announce("Restored. Your previous state is saved as a snapshot.");
    }

    private ImmutableArray<ConfigChange> HistoryChanges(ConfigProfileVersion before, ConfigProfileVersion after) =>
        [.. ConfigDiff.Changes(before, after, model.Dialect, model.Catalog).Select(item => item.Id switch
        {
            "com_maxfps" => item with { Label = "Game timing setting", Before = CheckTimingCopy(item.Before), After = CheckTimingCopy(item.After) },
            "cg_drawfps" => item with { Label = "Timing display" },
            _ => item
        })];

    private static Grid HistoryValues(string label, string before, string after, bool header = false)
    {
        var row = Columns(8, new GridLength(4, GridUnitType.Star), new GridLength(2, GridUnitType.Star),
            GridLength.Auto, new GridLength(2, GridUnitType.Star));
        Put(row, 0, header ? CheckStrong(label) : Body(label));
        Put(row, 1, Body(before));
        Put(row, 2, Body(header ? "" : "→"));
        Put(row, 3, header ? CheckStrong(after) : Body(after));
        return row;
    }

    private static string HistoryTitle(ConfigHistoryEntry entry) => entry.Source switch
    {
        ConfigHistorySource.GameSession when !entry.Before.Matches(entry.After) => "Changed while you played",
        ConfigHistorySource.Migration => "Brought over your launcher settings",
        ConfigHistorySource.Restore => "Restored a snapshot",
        _ => entry.Summary
    };

    private static (string Icon, string Source) HistorySource(ConfigHistorySource source) => source switch
    {
        ConfigHistorySource.GameSession => ("Play", "Game session"),
        ConfigHistorySource.Checkup => ("HeartPulse", "Check-up"),
        ConfigHistorySource.Import => ("ArrowDownload", "Import"),
        ConfigHistorySource.Migration => ("Folder", "Migration"),
        ConfigHistorySource.Restore => ("Refresh", "You · restored"),
        _ => ("Person", "You")
    };
}
