using System.Collections.ObjectModel;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class LibraryPage
{
    private readonly ObservableCollection<LibraryMatchRow> matchesInView = [];
    private bool ShowingMatches => ReferenceEquals(Sections.SelectedItem, MatchesSection);

    private MatchLibrary ReadMatchLibrary() =>
        MatchLibrary.Load(session.Settings.Working.MatchSummariesEnabled, session.ReadMatches);

    private void RenderMatches()
    {
        var history = ReadMatchLibrary();
        bool haveMatches = !history.Matches.IsEmpty;
        Retention.Visibility = IsVisible(haveMatches || history.State == MatchLibraryState.Empty);
        DeleteSummaries.Visibility = IsVisible(haveMatches);
        DeleteSummaries.IsEnabled = history.CanDelete(session.Game.State.Running);
        SummariesOff.IsOpen = history.State == MatchLibraryState.OffWithHistory;
        MatchTable.Visibility = IsVisible(haveMatches);
        Empty.Visibility = IsVisible(!haveMatches);
        var now = DateTimeOffset.UtcNow;
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var next = new LibraryMatchRow[history.Matches.Length];
        for (int index = next.Length - 1; index >= 0; index--)
        {
            var match = history.Matches[index];
            int ordinal = occurrences.GetValueOrDefault(match.EndedAt);
            occurrences[match.EndedAt] = ordinal + 1;
            next[index] = new(match, now, ordinal);
        }
        matchesInView.Reconcile(next, row => row.Key, (row, incoming) => row.Reconcile(incoming));

        EmptyBody.Content = history.State switch
        {
            MatchLibraryState.Off => LibraryEmpty("HeartPulse", "Match summaries are off",
                "Turn them on to see how each match ran. They stay on this PC.",
                LibraryAction("Turn on in Settings…", OpenMatchPrivacyAsync)),
            MatchLibraryState.Empty => LibraryEmpty("HeartPulse", "No matches yet",
                "After each match the game notes how it ran, so Check-up can advise you. Play one and it shows up here.",
                LibraryAction("Find a server", () => shell.NavigateAsync(LauncherPlace.Servers), accent: true)),
            MatchLibraryState.Unavailable => LibraryEmpty("HeartPulse", "Matches unavailable",
                "Your match summaries couldn’t be read. Check that your CoD2 Unbound folder is readable, then try again.",
                LibraryAction("Try again", () => { Render(); return Task.CompletedTask; })),
            _ => null
        };
    }

    private Task OpenMatchPrivacyAsync() => shell.OpenSettingsPaneAsync("Privacy");

    private async void OnMatchPrivacy(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(OpenMatchPrivacyAsync);

    private async void OnMatchCheckup(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: LibraryMatchRow row })
            await shell.OpenLibraryCheckupAsync(row.Summary);
    }

    private async void OnDeleteSummaries(object sender, RoutedEventArgs args)
    {
        await shell.RunAsync(async () =>
        {
            var history = ReadMatchLibrary();
            if (!history.CanDelete(session.ClientIsRunning))
            {
                Render();
                return;
            }
            var dialog = LibraryRemovalDialog(history.DeleteTitle,
                "Check-up loses its history and starts again from your next match. This can’t be undone.", "Delete summaries");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || session.ClientIsRunning)
                return;
            try
            {
                await session.DeleteMatchHistoryAsync();
                Render();
                ShowNotice("Match summaries deleted.", InfoBarSeverity.Success);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShowNotice("The match summaries couldn’t be deleted. Check that your CoD2 Unbound folder is writable.",
                    InfoBarSeverity.Error);
            }
        });
    }

    private void OnMatchArtLoaded(object sender, RoutedEventArgs args) => ShowMatchArt((Image)sender);

    private void OnMatchArtChanged(FrameworkElement sender, DataContextChangedEventArgs args) => ShowMatchArt((Image)sender);

    private void ShowMatchArt(Image image)
    {
        if (image.DataContext is LibraryMatchRow row)
            _ = previews.FillAsync(image, row.Summary.Map);
        else
        {
            image.Tag = null;
            image.Source = null;
        }
    }
}

internal sealed class LibraryMatchRow(MatchSummary match, DateTimeOffset now, int ordinal)
{
    private MatchConnection Connection => MatchFacts.Connection(Summary.PacketsReceived > 0 ? 1 : 0, Summary.PacketLossPercent);
    internal (string EndedAt, int Ordinal) Key { get; } = (match.EndedAt, ordinal);
    public MatchSummary Summary { get; private set; } = match;
    public string Title => $"{MapCatalog.DisplayName(Summary.Map)} · {GameModes.Long(Summary.Mode)}";
    public string Stamp
    {
        get
        {
            string duration = MatchFacts.Duration(Summary.DurationSeconds).ToUpperInvariant();
            return MatchFacts.Date(Summary.EndedAt) is { } date
                ? MatchFacts.Played(date, now, TimeZoneInfo.Local, includeTime: true) + " · " + duration
                : duration;
        }
    }
    public string Ping => Summary.PingAverageMS is { } average ? $"{average:F0} ms" : "—";
    public string PingName => Summary.PingAverageMS.HasValue ? $"Ping {Ping}" : "Ping not measured";
    public string ConnectionLabel => MatchFacts.ConnectionText(Connection);
    public string ConnectionGlyph => Connection switch
    {
        MatchConnection.Steady => "\uE73E",
        MatchConnection.Dips => "\uE7BA",
        _ => "\uE946"
    };
    public string CheckupName => $"Check-up for {Title}, {Stamp}";

    internal LibraryMatchRow Reconcile(LibraryMatchRow incoming)
    {
        if (Summary.Map != incoming.Summary.Map || Title != incoming.Title || Stamp != incoming.Stamp ||
            PingName != incoming.PingName || Connection != incoming.Connection)
            return incoming;
        Summary = incoming.Summary;
        return this;
    }
}
