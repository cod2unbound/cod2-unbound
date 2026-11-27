using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
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
    private readonly ObservableCollection<StudioHistoryRow> studioHistoryRows = [];

    private void AddStudioSnapshotCommand(Panel actions, ConfigProfileRead profile)
    {
        var snapshot = Action("Save a Snapshot Now", async () =>
        {
            if (!CanEdit || model.State.Current is not { } current || current.Profile.Id != profile.Profile.Id)
                return;
            model.Studio.SaveSnapshot(current);
            await RefreshStudioAsync();
            historySnapshotButton?.Focus(FocusState.Programmatic);
            Announce("Saved a snapshot.");
        });
        snapshot.Content = IconLabel("History", "Save a Snapshot Now", 18);
        AutomationProperties.SetName(snapshot, "Save a snapshot now");
        AutomationProperties.SetAutomationId(snapshot, "ConfigsSaveSnapshot");
        snapshot.HorizontalAlignment = HorizontalAlignment.Right;
        historySnapshotButton = snapshot;
        var target = new Border { Child = snapshot, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right };
        historySnapshotTarget = target;
        actions.Children.Add(target);
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
        if (studioHeaderExtra is not null)
            studioHeaderExtra.Content = Buttons(filterLabel, filters);
        var timeline = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 594,
            ItemsSource = studioHistoryRows, ItemTemplate = (DataTemplate)Resources["StudioHistoryRow"],
            ItemContainerStyle = StyleFor("StudioColumnItem"), TabNavigation = KeyboardNavigationMode.Once };
        AutomationProperties.SetName(timeline, "Snapshots");
        var empty = Caption("");
        int keep = ConfigProfileStore.HistoryRetention;
        var retention = Caption($"CoD2 Unbound keeps the last {keep} snapshots of each loadout. This one has {state.History.Length} of {keep}. Older ones are removed on their own.");
        var timelineArea = new StackPanel { Spacing = 10, Children = { timeline, empty, retention } };
        var compare = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false
        };
        var inspector = CheckCard(compare);
        inspector.MinHeight = 320;
        const double sideBySide = 760;
        var layout = CheckPair(timelineArea, inspector, sideBySide, rightWidth: 372);
        studioBody.Children.Add(layout);
        layout.SizeChanged += FitInspector;
        layout.Loaded += (_, _) =>
        {
            DetailScroll.SizeChanged += FitInspector;
            FitInspector(layout, null!);
        };
        layout.Unloaded += (_, _) => DetailScroll.SizeChanged -= FitInspector;
        bool drawing = false;
        filters.SelectionChanged += (_, _) =>
        {
            if (filters.SelectedIndex < 0)
                return;
            historyFilter = (ConfigHistoryFilter)filters.SelectedIndex;
            Draw();
        };
        timeline.SelectionChanged += (_, _) => { if (!drawing) Compare(); };
        Draw();

        void Draw()
        {
            drawing = true;
            var entries = state.History.Where(entry => historyFilter.Includes(entry.Source)).ToArray();
            var now = new StudioHistoryRow(null, "Now", $"How {profile.Profile.DisplayName} is set up now",
                profile.Profile.IsActive ? "Everything the next game will use." : "Everything the game uses when you choose this loadout.",
                "Now", state.History.FirstOrDefault()?.Date);
            var rows = entries.Select(entry =>
            {
                var changes = HistoryChanges(entry.Before, entry.After);
                string detail = entry.Summary == "Saved a snapshot" ? "A point to come back to." : ConfigDiff.Sentence(changes);
                return new StudioHistoryRow(entry, entry.Id, HistoryTitle(entry), detail, HistorySource(entry.Source), entry.Date);
            }).Prepend(now).ToArray();
            studioHistoryRows.Reconcile(rows, row => row.Id);
            timeline.SelectedItem = studioHistoryRows.FirstOrDefault(row => row.Entry?.Id == historySelection) ?? studioHistoryRows[0];
            empty.Visibility = IsVisible(entries.Length == 0);
            empty.Text = historyFilter == ConfigHistoryFilter.All ? "No snapshots yet. Every change you save here adds one." :
                $"No snapshots from {historyFilters[(int)historyFilter].ToLowerInvariant()} yet.";
            drawing = false;
            Compare();
        }

        void Compare()
        {
            var entry = (timeline.SelectedItem as StudioHistoryRow)?.Entry;
            historySelection = entry?.Id;
            compare.Content = entry is null
                ? EmptyState("History", "This is now", "Pick an earlier snapshot to see what has changed since then, or to go back to it.")
                : HistoryComparison(state, profile, entry);
        }

        void FitInspector(object sender, SizeChangedEventArgs args)
        {
            if (!layout.IsLoaded)
                return;
            if (layout.ActualWidth < sideBySide)
            {
                inspector.Height = double.NaN;
                return;
            }
            double top = layout.TransformToVisual(DetailColumn).TransformPoint(new Point(0, 0)).Y;
            inspector.Height = Math.Max(inspector.MinHeight, DetailScroll.ActualHeight - top - DetailColumn.Padding.Bottom);
        }
    }

    private sealed record StudioHistoryRow(ConfigHistoryEntry? Entry, string Id, string Title, string Detail, string Source, DateTimeOffset? Date)
    {
        public string Stamp => Date is { } time ? $"{Source} · {HistoryDate(time)}" : Source;
        public string Initial => Source[..1];
        public string AccessibleName => $"{Title}. {Detail} {Stamp}";
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
        var restore = Action("Restore This Snapshot", () => RestoreSnapshotAsync(profile, entry, ConfigSnapshotRevision.After));
        restore.Style = StyleFor("AccentButtonStyle");
        restore.IsEnabled = CanEdit && !entry.After.Matches(profile.Version);
        var more = new Button { Style = StyleFor("IconAction"), Content = Fluent("More", 16), IsEnabled = CanEdit };
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
        var footer = new StackPanel { Spacing = 12 };
        if (historyRestored == entry.Id)
            footer.Children.Add(CheckNotice(InfoBarSeverity.Success, "Restored", "Your previous state is saved as a snapshot."));
        footer.Children.Add(Caption("Restoring saves how things are now as a new snapshot first, so you can always come back."));
        footer.Children.Add(actions);

        var layout = new Grid { RowSpacing = 12 };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new ScrollViewer { Content = compare, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        layout.Children.Add(footer);
        Grid.SetRow(footer, 1);
        return layout;
    }

    private async Task RestoreSnapshotAsync(ConfigProfileRead profile, ConfigHistoryEntry entry, ConfigSnapshotRevision revision)
    {
        if (!CanEdit)
            return;
        var dialog = Dialog("Restore this loadout?", Body($"Restore {(revision == ConfigSnapshotRevision.Before ? "the state before" : "the state saved in")} “{HistoryTitle(entry)}”?"),
            "Restore");
        dialog.DefaultButton = ContentDialogButton.Close;
        TrackGameState(dialog, () => dialog.IsPrimaryButtonEnabled = CanEdit);
        if (await dialog.ShowSheetAsync() != ContentDialogResult.Primary || !CanEdit)
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

    private static string HistorySource(ConfigHistorySource source) => source switch
    {
        ConfigHistorySource.GameSession => "Game session",
        ConfigHistorySource.Checkup => "Check-up",
        ConfigHistorySource.Import => "Import",
        ConfigHistorySource.Migration => "Migration",
        ConfigHistorySource.Restore => "You · restored",
        _ => "You"
    };
}
