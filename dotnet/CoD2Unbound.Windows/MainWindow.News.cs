using CoD2Unbound.Core;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    /// Home's All Matches: Library, on Matches (the Mac's libraryTab = .matches).
    internal async Task OpenMatchesAsync()
    {
        await NavigateAsync(LauncherPlace.Library);
        if (ContentFrame.Content is LibraryPage library && library.FindName("MatchesSection") is SelectorBarItem matches)
            matches.IsSelected = true;
    }

    /// The loadout chip's Edit … in Configs.
    internal async Task OpenLoadoutInConfigsAsync(string id)
    {
        await NavigateAsync(LauncherPlace.Configs);
        if (ContentFrame.Content is ConfigsPage configs)
            configs.OpenJumpLoadout(id);
    }

    internal async Task ShowReleaseNotesAsync(string version)
    {
        var state = session.Updates.State;
        var offer = state.Offer?.Version == version ? state.Offer : null;
        var cached = session.Library.Releases.FirstOrDefault(item => item.Version == version);
        var notes = offer is not null && !state.Notes.IsDefaultOrEmpty ? state.Notes : cached?.Notes ?? [];
        if (notes.IsEmpty && session.Library.Dispatches.FirstOrDefault(item => item.Version == version) is { } dispatch)
            notes = [new("Changed", dispatch.Title, dispatch.Summary)];
        var link = (offer is not null ? state.ReleaseUrl : null) ?? ReleaseNotes.BrowserLink(cached?.Link)
            ?? (offer is null ? WindowsReleaseAssets.ReleasesPage : WindowsReleaseAssets.NotesPage(version));
        UpdateBlock? hold = null;
        if (offer is not null)
        {
            var activity = session.UpdateInstaller.Observe();
            hold = ReleasePackage.Block(activity.GameRunning, activity.ServersRunning);
        }
        bool canInstall = hold is null && session.CanCheckUpdates && updateRequest is null &&
            !state.IsBusy && state.Issue is not UpdateIssue.ProtectedLocation;
        var dialog = new ReleaseNotesDialog(this, version, session.Version, offer, notes, link, hold, canInstall);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && offer is not null &&
            session.Updates.State.Offer?.Version == version && updateRequest is null)
        {
            await NavigateAsync(LauncherPlace.UpdateSettings);
            await RunUpdateAsync(install: true);
        }
    }
}
