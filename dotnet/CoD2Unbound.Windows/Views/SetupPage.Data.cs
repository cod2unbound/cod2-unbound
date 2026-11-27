using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

/// <summary>Game data (the Mac's dataPanel; boards WinSetup 2): nothing found, found by itself, two copies,
/// a wrong folder, an unfinished Steam download, waiting for Steam and Steam missing.</summary>
public sealed partial class SetupPage
{
    private CancellationTokenSource? steamWatch;
    private GameInstallation? steamCopy;
    private bool steamUnavailable;
    private bool showCopyChoices = true;
    private string? dropProblem;
    private bool dropTargeted;

    private void RenderData()
    {
        var data = Setup.Data;
        var copies = session.SetupCopies;
        if (steamCopy is { } installed && !copies.Any(copy => WindowsLauncherSession.SamePath(copy.DataPath, installed.DataPath)))
            copies = copies.Insert(0, installed);
        bool found = data.State == SetupDataState.Found;
        Grid panel;
        dropZone = null;
        if (data.State is SetupDataState.Damaged or SetupDataState.UnfinishedSteam && data.Path.Length > 0)
        {
            bool unfinished = data.State == SetupDataState.UnfinishedSteam;
            var choose = FolderButton("Choose Folder…");
            var items = new List<UIElement>
            {
                Group(Row("folder", data.Path, unfinished ? "An unfinished Steam download" : $"{data.Survey.Found} of 16 game archives found",
                    trailing: choose, mono: true, negative: true)),
                Banner(SetupBannerTone.Error, "warning", (data.Problem ?? SetupSession.SteamUnfinishedMessage).Replace("main/", @"main\", StringComparison.Ordinal))
            };
            if (unfinished)
                items.Add(Cornered(Action("Finish in Steam", async (_, _) =>
                    await shell.ShowProblemAsync(() => OpenAsync("steam://install/2630")))));
            panel = GlassPanel(6, 12, [.. items]);
            focusTarget = choose;
        }
        else if (found && copies.Length > 1 && showCopyChoices &&
            copies.Any(copy => WindowsLauncherSession.SamePath(copy.DataPath, data.Path)))
        {
            panel = GlassPanel(6, 12, CopyChoices(copies, data.Path), Cornered(FolderButton("Choose a Different Folder…")));
            focusTarget = SetupNext;
        }
        else if (found)
        {
            var source = copies.FirstOrDefault(copy => WindowsLauncherSession.SamePath(copy.DataPath, data.Path))?.Source ?? GameInstallSource.Chosen;
            var used = Note("Used where it is. Nothing is copied or changed. Configs, demos and screenshots get their own folder.");
            used.Padding = new Thickness(16, 12, 16, 12);
            used.MinHeight = 44;
            panel = GlassPanel(6, 12,
                Group(16, Row("folder", data.Path, FoundDetail(source), mono: true, status: Status(SetupStatus.Ready, "Game data found")), used),
                Cornered(FolderButton("Choose a Different Folder…")));
            focusTarget = SetupNext;
        }
        else
        {
            var items = new List<UIElement> { DropZone() };
            if (dropProblem is { } dropError)
                items.Add(Banner(SetupBannerTone.Error, "warning", dropError));
            if (steamWatch is not null)
                items.Add(Group(Row("sync", "Waiting for Steam to install Call of Duty 2",
                    "Steam shows the download. Setup moves on by itself when all 16 game archives are there.",
                    trailing: Action("Stop Waiting", async (_, _) => await shell.RunAsync(async () =>
                    {
                        StopSteamWatch();
                        await RefreshAsync();
                    })))));
            else
            {
                items.Add(Group(16,
                    SourceRow("Steam", "Right-click the game, then Manage › Browse Local Files."),
                    SourceRow("Disc or other store", @"Usually C:\Program Files (x86)\Activision\Call of Duty 2."),
                    SourceRow("Mac 1.3", "Copy the Call of Duty 2 folder from the Mac to this PC.")));
                if (steamUnavailable)
                    items.Add(Banner(SetupBannerTone.Error, "warning",
                        "Steam isn’t installed on this PC. Install Steam, sign in, then choose Install with Steam again.",
                        Action("Get Steam", async (_, _) => await shell.ShowProblemAsync(() => OpenAsync("https://store.steampowered.com/about/")))));
                else
                    items.Add(Group(Row("download", "Own it on Steam, but not installed anywhere?",
                        "Steam installs it, about 3.7 GB. CoD2 Unbound finds the files when Steam finishes.",
                        trailing: Action("Install with Steam", OnInstallWithSteam))));
            }
            panel = GlassPanel(6, 12, [.. items]);
        }
        AcceptFolders(panel);
        StepContent.Children.Add(panel);
    }

    /// <summary>A button in the panel's corner sits two in from the group's edge (the Mac's cornered()).</summary>
    private static Border Cornered(FrameworkElement content)
    {
        content.HorizontalAlignment = HorizontalAlignment.Left;
        return new Border { Padding = new Thickness(2), Child = content };
    }

    private Grid SourceRow(string label, string detail)
    {
        var row = Columns(12, new GridLength(150), new(1, GridUnitType.Star));
        row.Padding = new Thickness(16, 0, 16, 0);
        row.MinHeight = 44;
        var title = new TextBlock
        {
            Text = label, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Token("text"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var description = new TextBlock
        {
            Text = detail, FontSize = 13, Foreground = Token("secondary"), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6)
        };
        Put(row, 0, title);
        Put(row, 1, description);
        AutomationProperties.SetName(row, $"{label}: {detail}");
        return row;
    }

    private static string FoundDetail(GameInstallSource source) => (source switch
    {
        GameInstallSource.Steam => "From Steam · ",
        GameInstallSource.Disc => "From a disc install · ",
        _ => ""
    }) + "All 16 game archives · Version 1.3";

    /// <summary>Two (or more) complete copies: a radio list in a group; the chosen one says Found.</summary>
    private StackPanel CopyChoices(System.Collections.Immutable.ImmutableArray<GameInstallation> copies, string chosen)
    {
        string count = copies.Length switch { 2 => "Two", 3 => "Three", 4 => "Four", _ => copies.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var intro = new TextBlock { Text = $"{count} copies found. Choose the one to use.", Style = StyleFor("SetupDetail"), Margin = new Thickness(10, 6, 0, 0) };
        // The stock RadioButtons control: arrow keys, roving focus and selection stay the system's.
        // The board's rows are 57 high with no gap between them; the stock control's own row spacing is a theme
        // resource of its template, set here.
        var list = new RadioButtons { Margin = new Thickness(16, 0, 16, 0) };
        AutomationProperties.SetName(list, "Game data folder");
        // Every row spans the list and takes the tallest row's height (57 on the board).
        ShareCells(list, 1, 0, floor: 57);
        foreach (var copy in copies)
        {
            bool selected = WindowsLauncherSession.SamePath(copy.DataPath, chosen);
            var text = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = copy.DataPath, Style = StyleFor("SetupMonoTitle") },
                    new TextBlock { Text = FoundDetail(copy.Source), Style = StyleFor("SetupDetail") }
                }
            };
            var layout = Columns(12, new(1, GridUnitType.Star), GridLength.Auto);
            Put(layout, 0, text);
            if (selected)
                Put(layout, 1, Status(SetupStatus.Ready, "Found"));
            var radio = new RadioButton
            {
                Content = layout, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 0, 0, 0), MinWidth = 0
            };
            AutomationProperties.SetName(radio, copy.DataPath);
            AutomationProperties.SetHelpText(radio, FoundDetail(copy.Source));
            list.Items.Add(radio);
            if (selected)
                list.SelectedIndex = list.Items.Count - 1;
        }
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedIndex < 0 || shell.IsBusy)
                return;
            string path = copies[list.SelectedIndex].DataPath;
            if (WindowsLauncherSession.SamePath(Setup.Data.Path, path))
                return;
            Setup.ChooseData(path);
            await shell.RunAsync(RefreshAsync);
        };
        var group = new Border { Style = StyleFor("SetupGroup"), Child = list };
        return new StackPanel { Spacing = 10, Children = { intro, group } };
    }

    private Button FolderButton(string label)
    {
        var button = Action(label, async (_, _) => await shell.RunAsync(async () =>
        {
            if (await shell.PickFolderAsync() is { } path)
                await ChooseDataAsync(path);
        }));
        return button;
    }

    private (TextBlock Title, Border Fill, Rectangle Edge, Button Choose)? dropZone;

    /// <summary>The well with a dashed edge; dragging a folder over the card turns the edge to the accent.</summary>
    private Grid DropZone()
    {
        var icon = Symbol("folder", 34);
        icon.Foreground = Token("accent");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var title = new TextBlock
        {
            FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Token("text"),
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap
        };
        var hint = new TextBlock
        {
            Text = "It’s the folder that contains “main”. Steam, disc or Mac 1.3 data all work.", Style = StyleFor("SetupDetail"),
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
        var choose = FolderButton("Choose Folder…");
        choose.HorizontalAlignment = HorizontalAlignment.Center;
        choose.Margin = new Thickness(0, 6, 0, 0);
        focusTarget = choose;
        var content = new StackPanel { Spacing = 10, Padding = new Thickness(20, 28, 20, 28), Children = { icon, title, hint, choose } };
        var fill = new Border();
        var edge = new Rectangle { RadiusX = 8, RadiusY = 8, IsHitTestVisible = false };
        var zone = new Grid { Children = { fill, edge, content } };
        AutomationProperties.SetName(zone, "Drop zone for your Call of Duty 2 folder");
        dropZone = (title, fill, edge, choose);
        ShowDropTarget(dropTargeted);
        return zone;
    }

    /// <summary>Draws the drop zone as idle or as a folder is held over the card; nothing is rebuilt, so the
    /// drag keeps its target.</summary>
    private void ShowDropTarget(bool targeted)
    {
        dropTargeted = targeted;
        if (dropZone is not { } zone)
            return;
        zone.Title.Text = targeted ? "Drop to use this folder" : "Drag your Call of Duty 2 folder here";
        zone.Fill.Background = Token(targeted ? "selection" : "well");
        zone.Edge.Stroke = Token(targeted ? "accent" : "wellStroke");
        zone.Edge.StrokeThickness = targeted ? 2 : 1;
        zone.Edge.StrokeDashArray = targeted ? null : new DoubleCollection { 6, 4 };
        zone.Choose.Visibility = IsVisible(!targeted);
    }

    /// <summary>A folder dropped anywhere on the card is used, as on the Mac (dropZone()).</summary>
    private void AcceptFolders(FrameworkElement card)
    {
        card.AllowDrop = true;
        card.DragOver += (_, args) =>
        {
            bool accepts = args.DataView.Contains(StandardDataFormats.StorageItems) && !shell.IsBusy;
            args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
            if (accepts)
            {
                args.DragUIOverride.Caption = "Drop to use this folder";
                ShowDropTarget(true);
            }
            args.Handled = true;
        };
        card.DragLeave += (_, _) => ShowDropTarget(false);
        card.Drop += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Handled = true;
                ShowDropTarget(false);
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
    }

    private async Task ChooseDataAsync(string path)
    {
        StopSteamWatch();
        showCopyChoices = false;
        dropProblem = null;
        steamUnavailable = false;
        if (Setup.ChooseData(path, picked: true).State == SetupDataState.Damaged)
            session.ShowError(UsageProblem.GameData);
        focusPending = true;
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
