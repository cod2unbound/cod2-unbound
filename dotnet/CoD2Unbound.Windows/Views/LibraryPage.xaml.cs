using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class LibraryPage : Page
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly WindowsMediaFiles files;
    private readonly MediaLibrary library;
    private readonly MapPreviews previews;
    private readonly ObservableCollection<MediaRow> screenshots = [];
    private readonly ObservableCollection<MediaRow> filesInView = [];
    private bool ready;

    internal LibraryPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        this.session = session;
        files = new(session.HomePath);
        library = new(files);
        previews = new(map => Task.Run(() => session.ReadMapArtwork(map)));
        InitializeComponent();
        Screenshots.ItemsSource = screenshots;
        Files.ItemsSource = filesInView;
        Matches.ItemsSource = matchesInView;
        Sections.SelectedItem = MatchesSection;
        Retention.Text = $"Kept on this PC · the last {MatchSummaries.RecordLimit} matches";
        ready = true;
        Render();
    }

    private MediaKind CurrentKind => ReferenceEquals(Sections.SelectedItem, DemosSection) ? MediaKind.Demo
        : ReferenceEquals(Sections.SelectedItem, ConfigFilesSection) ? MediaKind.Config : MediaKind.Screenshot;

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ready)
            Notice.IsOpen = false;
        TryRender();
    }

    internal bool FocusShellSearch() => !ShowingMatches && Search.Focus(FocusState.Keyboard);

    private void OnSearch(object sender, TextChangedEventArgs args) => TryRender();

    private async void TryRender()
    {
        if (ready)
            await shell.ShowProblemAsync(() => { Render(); return Task.CompletedTask; });
    }

    internal void UpdateGameState(bool running, bool ended)
    {
        if (ended || (running && !ShowingMatches && CurrentKind == MediaKind.Demo))
            Render();
        else if (running)
            DeleteSummaries.IsEnabled = false;
    }

    internal void Render()
    {
        bool matches = ShowingMatches;
        var kind = CurrentKind;
        bool canPlay = kind != MediaKind.Demo || !session.Game.State.Running;
        ShowFolder.Visibility = IsVisible(!matches);
        Search.Visibility = IsVisible(!matches);
        RecycleNotice.Visibility = IsVisible(!matches && recycled?.Kind == kind);
        SectionTitle.Text = matches ? "Matches" : kind switch
        {
            MediaKind.Demo => "Demos",
            MediaKind.Screenshot => "Screenshots",
            _ => "Library"
        };
        SectionDetail.Text = matches ? "How your recent matches ran: ping and connection." : kind switch
        {
            MediaKind.Demo => canPlay ? "Recorded matches play back in the game, from any point." : MediaRow.PlayBlocked,
            MediaKind.Screenshot => "Screenshots you take in a match, kept in your CoD2 Unbound folder.",
            _ => ""
        };
        SectionDetail.Visibility = IsVisible(SectionDetail.Text.Length != 0);
        ShowFolder.Content = kind == MediaKind.Config ? "Show folder" : "Show in File Explorer";
        if (matches)
        {
            Screenshots.Visibility = FileTable.Visibility = ScreenshotHelp.Visibility = Visibility.Collapsed;
            RenderMatches();
            return;
        }

        MatchTable.Visibility = Retention.Visibility = DeleteSummaries.Visibility = Visibility.Collapsed;
        SummariesOff.IsOpen = false;
        ListViewBase list = kind == MediaKind.Screenshot ? Screenshots : Files;
        var view = kind == MediaKind.Screenshot ? screenshots : filesInView;
        string? selected = (list.SelectedItem as MediaRow) is { } row && row.Kind == kind ? row.Name : null;
        MediaRow[] rows;
        try
        {
            rows = library.List(kind, Search.Text)
                .Select(file => new MediaRow(kind, file, MediaFacts.From(file, TimeZoneInfo.Local), canPlay)).ToArray();
        }
        catch (Exception error) when (kind != MediaKind.Config && error is (IOException or UnauthorizedAccessException))
        {
            Screenshots.Visibility = FileTable.Visibility = ScreenshotHelp.Visibility = Visibility.Collapsed;
            Empty.Visibility = Visibility.Visible;
            EmptyBody.Content = LibraryEmpty("Folder", "Library unavailable",
                "These files couldn’t be read. Check that your CoD2 Unbound folder is readable, then try again.",
                LibraryAction("Try again", () => { Render(); return Task.CompletedTask; }));
            return;
        }
        view.Reconcile(rows, item => (item.Kind, item.Name), (row, incoming) => row.Reconcile(incoming));
        Files.IsItemClickEnabled = kind == MediaKind.Config;
        Screenshots.Visibility = ScreenshotHelp.Visibility = IsVisible(rows.Length > 0 && kind == MediaKind.Screenshot);
        FileTable.Visibility = IsVisible(rows.Length > 0 && kind != MediaKind.Screenshot);
        NameHeader.Text = kind == MediaKind.Demo ? "Map" : "Name";
        MapHeader.Text = "Recording";
        FirstFileColumn.Width = kind == MediaKind.Demo ? new GridLength(84) : new GridLength(2, GridUnitType.Star);
        DateHeader.Text = kind == MediaKind.Demo ? "Recorded" : "Date";
        FileActionsColumn.Width = new GridLength(kind == MediaKind.Demo ? 96 : 32);
        MapHeader.Visibility = IsVisible(kind == MediaKind.Demo);
        Empty.Visibility = IsVisible(rows.Length == 0);
        if (selected is not null)
        {
            var target = view.FirstOrDefault(item => item.Name == selected);
            if (!ReferenceEquals(list.SelectedItem, target))
                list.SelectedItem = target;
        }
        if (rows.Length > 0)
            return;

        if (!string.IsNullOrWhiteSpace(Search.Text))
            EmptyBody.Content = kind == MediaKind.Config ? EmptyState("Document", "No matching files", "Try another search.")
                : LibraryEmpty("Search", "No matching files", "Try another search.");
        else if (kind == MediaKind.Demo)
            EmptyBody.Content = RecordingEmpty();
        else if (kind == MediaKind.Screenshot)
            EmptyBody.Content = LibraryEmpty("Image", "No screenshots",
                "Take one in a match with F12. It appears here when you come back.");
        else
            EmptyBody.Content = EmptyState("Document", "No config files yet", "Config files saved in the game folder appear here.");
    }

    private void OnScreenshotLoaded(object sender, RoutedEventArgs args) => _ = ShowScreenshotAsync((Border)sender);
    private void OnScreenshotChanged(FrameworkElement sender, DataContextChangedEventArgs args) => _ = ShowScreenshotAsync((Border)sender);
    private void OnDemoArtLoaded(object sender, RoutedEventArgs args) => ShowDemoArt((Image)sender);
    private void OnDemoArtChanged(FrameworkElement sender, DataContextChangedEventArgs args) => ShowDemoArt((Image)sender);

    private void ShowDemoArt(Image image)
    {
        if (image.DataContext is MediaRow { Kind: MediaKind.Demo, Facts.Map: { } map })
            _ = previews.FillAsync(image, map);
        else
        {
            image.Tag = null;
            image.Source = null;
        }
    }

    private async Task ShowScreenshotAsync(Border tile)
    {
        if (tile.DataContext is not MediaRow row || ReferenceEquals(tile.Tag, row))
            return;
        tile.Tag = row;
        tile.ClearValue(Border.BackgroundProperty);
        var placeholder = (FontIcon)tile.Child;
        placeholder.Visibility = Visibility.Visible;
        try
        {
            ImageSource thumbnail;
            if (Path.GetExtension(row.Name).Equals(".tga", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = await Task.Run(() =>
                {
                    using var input = files.OpenScreenshot(row.Name);
                    return TgaThumbnail.Read(input, 400);
                });
                if (!ReferenceEquals(tile.Tag, row))
                    return;
                thumbnail = BitmapFromBgra(decoded.Bgra, decoded.Width, decoded.Height);
            }
            else
            {
                using var input = files.OpenScreenshot(row.Name);
                using var source = input.AsRandomAccessStream();
                var bitmap = new BitmapImage { DecodePixelWidth = 400 };
                await bitmap.SetSourceAsync(source);
                thumbnail = bitmap;
            }
            if (ReferenceEquals(tile.Tag, row))
            {
                tile.Background = new ImageBrush { ImageSource = thumbnail, Stretch = Stretch.UniformToFill };
                placeholder.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // A file can disappear during preview or be unsupported by the Windows decoder.
        }
    }

    private async void OnShowFolder(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => OpenAsync(files.Folder(CurrentKind)));

    private async void OnOpenFile(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is MediaRow row)
            await shell.RunAsync(() => OpenFileAsync(row));
    }

    private async Task OpenFileAsync(MediaRow row)
    {
        string path = files.Existing(row.Kind, row.File.Name);
        if (row.Kind == MediaKind.Screenshot)
        {
            await OpenAsync(path);
            return;
        }
        if (row.Kind == MediaKind.Config)
        {
            var editor = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            editor.ArgumentList.Add(path);
            try { using var app = Process.Start(editor); }
            catch (System.ComponentModel.Win32Exception error) { throw new IOException(error.Message, error); }
            return;
        }

        await PlayDemoAsync(path, confirm: false);
    }

    internal Task OfferDemoAsync(string path)
    {
        Sections.SelectedItem = DemosSection;
        return PlayDemoAsync(path, confirm: true);
    }

    private async Task PlayDemoAsync(string path, bool confirm)
    {
        if (session.ClientIsRunning)
            throw new IOException("Close the game before playing a demo.");
        string fileName = Path.GetFileName(path);
        if (!MediaLibrary.Accepts(fileName, MediaKind.Demo))
            throw new IOException("Choose a .dm_1 demo recording.");
        string folder = Path.GetDirectoryName(path)!;
        using var parent = new WindowsFiles.DirectoryHandles(folder);
        // Keep the reviewed file stable until it has been staged for the engine.
        using var source = WindowsFiles.Open(folder, fileName, FileShare.Read);
        if (await Task.Run(() => DemoRecording.ReadEdition(source)) is not { } edition)
            throw new IOException("This file isn’t a complete Original 1.3 or CoD2x demo.");

        if (confirm)
        {
            var details = Body($"{fileName}\n{ConnectDialog.EditionName(edition)}");
            details.TextWrapping = TextWrapping.Wrap;
            var dialog = new ContentDialog
            {
                XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme, Title = "Play this demo?", Content = details,
                PrimaryButtonText = "Play demo", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        string name = Path.GetFileNameWithoutExtension(fileName);
        if (confirm || !MediaName.Playable(name))
            name = await library.PrepareDemoAsync(new Uri(path), fileName);
        var result = await shell.RunGameStartAsync(edition, () => session.LaunchAsync(edition: edition, demo: name));
        if (result is GameStartState.AlreadyRunning or GameStartState.Attached)
            throw new IOException("Close the game before playing a demo.");
    }

}

internal sealed class MediaRow(MediaKind kind, MediaFile file, MediaFacts facts, bool canPlay) : INotifyPropertyChanged
{
    internal const string PlayBlocked = "Close the game before playing a demo.";
    internal MediaKind Kind { get; } = kind;
    internal MediaFile File { get; } = file;
    internal MediaFacts Facts { get; } = facts;
    public bool CanPlay { get; private set; } = canPlay;
    public string Name => File.Name;
    public string ScreenshotTitle => Facts.Map is { } map ? MapCatalog.DisplayName(map) : Name;
    public string PlayName => $"Play {Name}";
    public string PlaybackHint => CanPlay ? "" : PlayBlocked;
    public string MenuName => $"More actions for {Name}";
    public string DemoDetail => Facts.Map is { } map ? $"{MapCatalog.DisplayName(map)} · Recorded match" : "Recorded match";
    public GridLength FirstColumnWidth => Kind == MediaKind.Demo ? new GridLength(84) : new GridLength(2, GridUnitType.Star);
    public string Stamp => Facts.Stamp;
    public string Size => Facts.Size;
    public Visibility Demo => IsVisible(Kind == MediaKind.Demo);
    public Visibility Config => IsVisible(Kind == MediaKind.Config);
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal MediaRow Reconcile(MediaRow incoming)
    {
        if (File != incoming.File || Facts != incoming.Facts)
            return incoming;
        if (CanPlay != incoming.CanPlay)
        {
            CanPlay = incoming.CanPlay;
            PropertyChanged?.Invoke(this, new(nameof(CanPlay)));
            PropertyChanged?.Invoke(this, new(nameof(PlaybackHint)));
        }
        return this;
    }
}
