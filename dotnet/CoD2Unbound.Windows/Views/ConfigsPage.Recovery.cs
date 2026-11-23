using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private Action? updateDialogGameState;

    private void TrackGameState(ContentDialog dialog, Action refresh)
    {
        // The shell defers its normal poll while a dialog owns the command queue.
        // A game can still be started separately while the dialog is open.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        bool readOnly = false;
        timer.Tick += (_, _) =>
        {
            bool running = model.Studio.IsReadOnly;
            if (running == readOnly)
                return;
            readOnly = running;
            refresh();
        };
        dialog.Opened += (_, _) =>
        {
            updateDialogGameState = refresh;
            readOnly = model.Studio.IsReadOnly;
            refresh();
            timer.Start();
        };
        dialog.Closed += (_, _) =>
        {
            timer.Stop();
            updateDialogGameState = null;
        };
    }

    private void OnSettingSaveChanged()
    {
        RenderLoadouts();
        if (model.SettingSaveProblem is null)
            ShowStudio();
        else
        {
            UpdateSharingAvailability();
            RenderRecovery();
        }
    }

    private void RenderRecovery()
    {
        RecoveryStatus.IsOpen = false;
        RecoveryStatus.Content = null;
        if (DiscoverPlaces.SelectedItem is not null)
            return;
        if (model.SettingSaveProblem is { } save)
        {
            var retry = Action("Try again", () => { model.RetrySetting(); return Task.CompletedTask; });
            var discard = Action("Discard change", () => { model.DiscardSetting(); return Task.CompletedTask; });
            retry.IsEnabled = discard.IsEnabled = !model.Studio.IsReadOnly;
            SetRecovery($"Couldn't save: {save.Title}", save.Reason, retry, discard);
        }
        else if (model.State.ReadProblem is { } read)
        {
            var retry = Action("Try again", ReopenLoadoutAsync);
            if (read.ProfileId is { } id)
            {
                var review = Action("Review…", () => ReviewRecoveryAsync(id));
                review.IsEnabled = !model.Studio.IsReadOnly;
                SetRecovery("Couldn't read this loadout", read.Message, retry, review);
            }
            else
                SetRecovery("Couldn't read this loadout", read.Message, retry);
        }
    }

    private void SetRecovery(string title, string reason, params Button[] actions)
    {
        RecoveryStatus.Title = title;
        RecoveryStatus.Message = reason;
        RecoveryStatus.Content = Buttons(actions);
        RecoveryStatus.IsOpen = true;
    }

    private async Task ReopenLoadoutAsync()
    {
        model.Studio.Refresh();
        await RefreshStudioAsync();
    }

    private async Task ReviewRecoveryAsync(string id)
    {
        try { model.Studio.InspectRecovery(id); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, "Nothing to recover", error.Message);
            return;
        }

        var dialog = Dialog("Keep the version on disk?", Body(
            "A change to this loadout was interrupted, and the file changed on disk since. Keeping the disk version saves the interrupted change in the loadout’s recovery folder instead of applying it."),
            "Keep version on disk");
        TrackGameState(dialog, () => dialog.IsPrimaryButtonEnabled = !model.Studio.IsReadOnly);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            model.Studio.KeepDiskVersion();
            await RefreshStudioAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, "Couldn't keep the version on disk", error.Message);
        }
    }
}
