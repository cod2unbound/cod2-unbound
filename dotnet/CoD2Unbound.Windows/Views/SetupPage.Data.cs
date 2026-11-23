using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SetupPage
{
    private CancellationTokenSource? steamWatch;
    private GameInstallation? steamCopy;
    private bool steamUnavailable;
    private bool showCopyChoices = true;
    private string? dropProblem;

    private void RenderData()
    {
        var data = Setup.Data;
        var copies = session.SetupCopies;
        if (steamCopy is { } installed && !copies.Any(copy => WindowsLauncherSession.SamePath(copy.DataPath, installed.DataPath)))
            copies = copies.Insert(0, installed);
        bool found = data.State == SetupDataState.Found;
        if (found || data.Path.Length > 0)
        {
            if (found && copies.Length > 1 && showCopyChoices &&
                copies.Any(copy => WindowsLauncherSession.SamePath(copy.DataPath, data.Path)))
            {
                var options = new RadioButtons { Header = $"{copies.Length} copies found. Choose the one to use." };
                AutomationProperties.SetName(options, "Game data folder");
                foreach (var copy in copies)
                    options.Items.Add(RadioChoice(copy.DataPath, FoundDetail(copy.Source)));
                options.SelectedIndex = Array.FindIndex(copies.ToArray(), copy => WindowsLauncherSession.SamePath(copy.DataPath, data.Path));
                options.SelectionChanged += (_, _) =>
                {
                    if (options.SelectedIndex < 0 || shell.IsBusy)
                        return;
                    Setup.ChooseData(copies[options.SelectedIndex].DataPath);
                    UpdateContinue();
                    if (Setup.Data.State != SetupDataState.Found)
                        _ = shell.RunAsync(RefreshAsync);
                };
                StepContent.Children.Add(DataCard(options, FolderButton("Choose a different folder…")));
            }
            else
            {
                var source = copies.FirstOrDefault(copy => WindowsLauncherSession.SamePath(copy.DataPath, data.Path))?.Source ?? GameInstallSource.Chosen;
                StepContent.Children.Add(DataCard(
                    ChoiceContent(found ? "Game data found" : data.Path,
                        found ? data.Path + "\n" + FoundDetail(source) : $"{data.Survey.Found} of 16 game archives found"),
                    FolderButton("Choose a different folder…")));
            }
            if (data.Problem is { } problem)
                StepContent.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = problem });
            if (found)
                StepContent.Children.Add(Caption("Configs, demos and screenshots go in your CoD2 Unbound folder."));
            else if (copies.Length > 0)
            {
                var useFound = new Button { Content = "Use a found copy" };
                useFound.Click += async (_, _) => await shell.RunAsync(async () =>
                {
                    showCopyChoices = true;
                    Setup.ChooseData(copies[0].DataPath);
                    await RefreshAsync();
                });
                StepContent.Children.Add(useFound);
            }
            return;
        }

        if (steamUnavailable)
        {
            var getSteam = new Button { Content = "Get Steam" };
            getSteam.Click += async (_, _) => await shell.ShowProblemAsync(() => OpenAsync("https://store.steampowered.com/about/"));
            StepContent.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, ActionButton = getSteam,
                Message = "Steam isn’t installed on this PC. Install Steam, sign in, then choose Install with Steam again."
            });
        }
        StepContent.Children.Add(DropZone());
        if (dropProblem is { } dropError)
            StepContent.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = dropError });
        if (steamWatch is null)
        {
            StepContent.Children.Add(SettingRow("Steam", "In Steam, right-click the game, then Manage › Browse local files."));
            StepContent.Children.Add(SettingRow("Disc or other store", @"Usually C:\Program Files (x86)\Activision\Call of Duty 2."));
            StepContent.Children.Add(SettingRow("Mac 1.3", "Copy the Call of Duty 2 folder from the Mac to this PC."));
            var install = new Button { Content = "Install with Steam" };
            install.Click += OnInstallWithSteam;
            StepContent.Children.Add(DataCard(SettingRow("Own it on Steam, but not installed?",
                "Steam installs it. CoD2 Unbound finds the files when Steam finishes.", install)));
        }
        else
        {
            var stop = new Button { Content = "Stop waiting" };
            stop.Click += async (_, _) => await shell.RunAsync(async () =>
            {
                StopSteamWatch();
                await RefreshAsync();
            });
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(ring, "Waiting for Steam");
            var waiting = Columns(8, GridLength.Auto, new(1, GridUnitType.Star));
            Put(waiting, 0, ring);
            Put(waiting, 1, ChoiceContent("Waiting for Steam to install Call of Duty 2",
                "Steam shows the download. Setup continues when all 16 game archives are present."));
            StepContent.Children.Add(DataCard(waiting, stop));
        }
    }

    private static string FoundDetail(GameInstallSource source) => (source switch
    {
        GameInstallSource.Steam => "From Steam · ",
        GameInstallSource.Disc => "From a disc install · ",
        _ => ""
    }) + "Version 1.3 · 16 of 16 game archives";

    private static Border DataCard(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var child in children)
            panel.Children.Add(child);
        return new Border { Style = StyleFor("FieldCard"), Child = panel };
    }

    private Button FolderButton(string label)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await shell.RunAsync(async () =>
        {
            if (await shell.PickFolderAsync() is { } path)
                await ChooseDataAsync(path);
        });
        return button;
    }

    private Border DropZone()
    {
        const string prompt = "Drag your Call of Duty 2 folder here";
        var title = new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
        var choose = FolderButton("Choose a folder…");
        choose.HorizontalAlignment = HorizontalAlignment.Center;
        var zone = DataCard(title, Caption("It’s the folder that contains “main”. Windows or Mac 1.3 data both work."), choose);
        zone.AllowDrop = true;
        var normal = ThemeBrush("PanelStrokeBrush", ActualTheme, shell.IsHighContrast);
        zone.DragOver += (_, args) =>
        {
            bool accepts = args.DataView.Contains(StandardDataFormats.StorageItems) && !shell.IsBusy;
            args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
            if (accepts)
            {
                zone.BorderBrush = ThemeBrush("AccentFillColorDefaultBrush", ActualTheme, shell.IsHighContrast);
                title.Text = "Drop to use this folder";
                args.DragUIOverride.Caption = title.Text;
            }
            args.Handled = true;
        };
        zone.DragLeave += (_, _) => Reset();
        zone.Drop += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Handled = true;
                Reset();
                await shell.RunAsync(async () =>
                {
                    if (args.DataView.Contains(StandardDataFormats.StorageItems) &&
                        await args.DataView.GetStorageItemsAsync() is { Count: 1 } items &&
                        items[0] is StorageFolder { Path.Length: > 0 } folder)
                    {
                        await ChooseDataAsync(folder.Path);
                        return;
                    }
                    dropProblem = "Drop one Call of Duty 2 folder here. To find it, choose the folder that contains “main”.";
                    await RefreshAsync();
                });
            }
            finally { deferral.Complete(); }
        };
        return zone;

        void Reset()
        {
            zone.BorderBrush = normal;
            title.Text = prompt;
        }
    }

    private async Task ChooseDataAsync(string path)
    {
        StopSteamWatch();
        showCopyChoices = false;
        dropProblem = null;
        steamUnavailable = false;
        if (Setup.ChooseData(path, picked: true).State == SetupDataState.Damaged)
            session.ShowError(UsageProblem.GameData);
        await RefreshAsync();
    }

    private async void OnInstallWithSteam(object sender, RoutedEventArgs args)
    {
        CancellationTokenSource? watch = null;
        await shell.RunAsync(async () =>
        {
            steamUnavailable = !await session.CanOpenSteamAsync();
            if (!steamUnavailable)
            {
                try { await OpenAsync("steam://install/2630"); }
                catch (IOException) { steamUnavailable = true; }
                if (!steamUnavailable)
                    steamWatch = watch = new CancellationTokenSource();
            }
            await RefreshAsync();
        });
        if (watch is null)
            return;
        using (watch)
        {
            try
            {
                var installed = await session.WaitForSteamAsync(watch.Token);
                // A folder picker may be open. Wait until it closes, or cancel if the player chose a folder.
                while (shell.IsBusy)
                    await Task.Delay(100, watch.Token);
                if (watch.IsCancellationRequested || !ReferenceEquals(steamWatch, watch))
                    return;
                await shell.RunAsync(async () =>
                {
                    steamWatch = null;
                    steamCopy = installed;
                    Setup.ChooseData(installed.DataPath);
                    await Setup.ContinueAsync();
                    await RefreshAsync();
                });
            }
            catch (OperationCanceledException) when (watch.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (ReferenceEquals(steamWatch, watch))
                {
                    steamWatch = null;
                    await shell.ShowProblemAsync(async () =>
                    {
                        await RefreshAsync();
                        throw new IOException("Couldn’t check your Steam libraries. Choose the game folder or try again.", error);
                    });
                }
            }
        }
    }

    private void StopSteamWatch()
    {
        var watch = steamWatch;
        steamWatch = null;
        watch?.Cancel();
    }
}
