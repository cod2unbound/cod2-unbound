using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.Windows.Storage.Pickers;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private async Task PickInstalledProfileAsync(string? folder = null)
    {
        if (!CanEdit)
            return;
        bool dropped = folder is not null;
        (WindowsProfileDiscovery Found, string[] Searched)? results = null;
        while (CanEdit)
        {
            // A single dropped profile goes straight to review. A folder with several
            // profiles uses the same grouped chooser as an install.
            if (dropped)
            {
                results = await model.FindInstalledProfilesAsync(folder, default);
                if (results.Value.Found.Profiles is [var single])
                {
                    await ReviewProfileAsync(single);
                    return;
                }
                dropped = false;
            }

            using var cancel = new CancellationTokenSource();
            var panel = new StackPanel { Spacing = 12, MinWidth = 370, MaxWidth = 510 };
            panel.Children.Add(Body("CoD2 Unbound looks for Call of Duty 2 player profiles in the usual install folders on this PC's drives, and where Windows keeps a copy for games installed under Program Files. The game's own files are never changed."));
            var state = new StackPanel { Spacing = 8 };
            panel.Children.Add(state);
            var dialog = Dialog("Bring in your Call of Duty 2 profiles", panel, "Review");
            dialog.SecondaryButtonText = "Choose folder…";
            dialog.IsPrimaryButtonEnabled = false;
            dialog.Closed += (_, _) => cancel.Cancel();

            ListView? list = null;
            TrackGameState(dialog, Validate);
            Task? scan = null;
            if (results is { } saved)
                ShowResults(saved);
            else
            {
                state.Children.Add(Progress("Looking on this PC's drives…"));
                dialog.Opened += (_, _) => scan = LoadAsync();
            }
            var decision = await dialog.ShowSheetAsync();
            if (scan is not null)
                await scan;

            if (decision == ContentDialogResult.Primary && list?.SelectedItem is InstalledProfileRow selected)
            {
                await ReviewProfileAsync(selected.Candidate);
                return;
            }
            if (decision != ContentDialogResult.Secondary)
                return;
            var picker = new FolderPicker(shell.AppWindow.Id);
            if (await picker.PickSingleFolderAsync() is { } chosen)
            {
                folder = chosen.Path;
                results = null;
            }

            async Task LoadAsync()
            {
                try
                {
                    var found = await model.FindInstalledProfilesAsync(folder, cancel.Token);
                    if (!cancel.IsCancellationRequested)
                    {
                        results = found;
                        ShowResults(found);
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    state.Children.Clear();
                    state.Children.Add(Body(error.Message));
                }
            }

            void Validate() => dialog.IsPrimaryButtonEnabled = CanEdit && list?.SelectedItem is InstalledProfileRow;

            void ShowResults((WindowsProfileDiscovery Found, string[] Searched) result)
            {
                state.Children.Clear();
                if (result.Found.Profiles.IsEmpty)
                {
                    state.Children.Add(IconLabel("Folder", "No profiles found", 20));
                    state.Children.Add(Body("Choose the folder that holds your Call of Duty 2 player profile."));
                }
                else
                {
                    var groups = result.Found.Profiles.GroupBy(candidate => candidate.IsBackup).OrderBy(group => group.Key)
                        .Select(group => new InstalledProfileGroup(group.Key ? "Backups (optional)" : "Profiles",
                            group.Select(candidate => new InstalledProfileRow(candidate)).ToArray())).ToArray();
                    var view = new CollectionViewSource { IsSourceGrouped = true, ItemsPath = new PropertyPath("Items"), Source = groups };
                    list = new ListView
                    {
                        ItemsSource = view.View,
                        ItemTemplate = (DataTemplate)Resources["InstalledProfileRow"],
                        MaxHeight = Math.Max(100, Math.Min(290, XamlRoot.Size.Height - 350)),
                        SelectionMode = ListViewSelectionMode.Single
                    };
                    list.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)Resources["InstalledProfileHeader"] });
                    AutomationProperties.SetName(list, "Call of Duty 2 profiles");
                    list.SelectionChanged += (_, _) => Validate();
                    state.Children.Add(list);
                    list.SelectedIndex = 0;
                }
                if (result.Searched.Length > 0)
                    state.Children.Add(Caption($"Looked on: {string.Join(", ", result.Searched)}."));
                if (result.Found.UnreadableDirectories is var unreadable && unreadable > 0)
                    state.Children.Add(Caption($"{unreadable} {(unreadable == 1 ? "folder couldn't" : "folders couldn't")} be read. If your profiles are there, choose the folder yourself."));
                if (result.Found.Truncated)
                    state.Children.Add(Caption("The search stopped early. Choose the folder yourself if you don't see your profile."));
                Validate();
            }
        }
    }

    private Task ReviewProfileAsync(WindowsProfileCandidate profile)
    {
        string path = profile.ConfigPath.Replace('/', '\\');
        string filename = Path.GetFileName(path);
        return ReviewInputAsync(() => new ConfigImportInput.Folder(new Uri(Path.GetDirectoryName(path)!), filename),
            profile.Name, new("Windows profile", $"{profile.Name}/{filename}", "Desktop",
                Scope: "Selected profile folder"));
    }
}

internal sealed record InstalledProfileGroup(string Title, InstalledProfileRow[] Items);
internal sealed record InstalledProfileRow(WindowsProfileCandidate Candidate)
{
    public string Name => Candidate.Name;
    public string FileName => Path.GetFileName(Candidate.ConfigPath);
    public Visibility ActiveVisibility => IsVisible(Candidate.IsActive && !Candidate.IsBackup);
    public Visibility BackupVisibility => IsVisible(Candidate.IsBackup);
    public string AccessibleName => $"{Name}, {FileName}" + (Candidate.IsBackup ? ", backup copy" :
        Candidate.IsActive ? ", in use by the game" : "");
}
