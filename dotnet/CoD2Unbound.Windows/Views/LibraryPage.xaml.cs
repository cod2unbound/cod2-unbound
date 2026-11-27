using System.Collections.ObjectModel;
using System.ComponentModel;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal enum LibrarySection { Matches, Demos, Screenshots }

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
        foreach (var banner in new[] { SummariesOff, RecycleNotice, Notice })
        {
            banner.Visibility = Visibility.Collapsed;
            banner.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
                banner.Visibility = IsVisible(banner.IsOpen));
        }
        Screenshots.ItemsSource = screenshots;
        Files.ItemsSource = filesInView;
        Matches.ItemsSource = matchesInView;
        Sections.SelectedItem = shell.LastLibrarySection switch
        {
            LibrarySection.Matches => MatchesSection,
            LibrarySection.Screenshots => ScreenshotsSection,
            _ => DemosSection
        };
        RetentionText.Text = $"Kept on this PC · the last {MatchSummaries.RecordLimit} matches";
        AutomationProperties.SetName(Retention, $"Kept on this PC. The last {MatchSummaries.RecordLimit} matches");
        ready = true;
        Render();
    }

    private MediaKind CurrentKind => ReferenceEquals(Sections.SelectedItem, DemosSection)
        ? MediaKind.Demo : MediaKind.Screenshot;

    public void OpenMatches() => Sections.SelectedItem = MatchesSection;

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!ready)
            return;
        shell.LastLibrarySection = ShowingMatches ? LibrarySection.Matches
            : CurrentKind == MediaKind.Demo ? LibrarySection.Demos : LibrarySection.Screenshots;
        Notice.IsOpen = false;
        RecycleNotice.IsOpen = false;
        recycled = null;
        Files.SelectedItem = Screenshots.SelectedItem = null;
        if (!ClearSearch())
            TryRender();
    }

    internal bool FocusShellSearch() => !ShowingMatches && Search.Focus(FocusState.Keyboard);

    private bool ClearSearch()
    {
        if (Search.Text.Length == 0)
            return false;
        Search.Text = "";
        return true;
    }

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
        bool demos = kind == MediaKind.Demo;
        bool canPlay = !demos || !session.Game.State.Running;
        Search.Visibility = ShowFolder.Visibility = IsVisible(!matches);
        DeleteSummaries.Visibility = IsVisible(matches);
        SectionTitle.Text = matches ? "Matches" : demos ? "Demos" : "Screenshots";
        SectionDetail.Text = matches ? "How your recent matches ran: ping and connection."
            : demos ? "Recorded matches play back in the game, from any point."
            : "Screenshots you take in a match, kept in your CoD2 Unbound folder.";
        if (matches)
        {
            ScreenshotGallery.Visibility = FileTable.Visibility = Visibility.Collapsed;
            RenderMatches();
            return;
        }

        MatchTable.Visibility = Retention.Visibility = Visibility.Collapsed;
        SummariesOff.IsOpen = false;
        var list = MediaList;
        var view = demos ? filesInView : screenshots;
        string? selected = (list.SelectedItem as MediaRow)?.Name;
        MediaRow[] rows;
        try
        {
            rows = library.List(kind, Search.Text)
                .Select(file => new MediaRow(kind, file, MediaFacts.From(file, TimeZoneInfo.Local), canPlay)).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ScreenshotGallery.Visibility = FileTable.Visibility = Visibility.Collapsed;
            Empty.Visibility = Visibility.Visible;
            EmptyBody.Content = LibraryEmpty("Library unavailable",
                "These files couldn’t be read. Check that your CoD2 Unbound folder is readable, then try again.",
                LibraryAction("Try Again", () => { Render(); return Task.CompletedTask; }));
            return;
        }
        view.Reconcile(rows, item => item.Name, (row, incoming) => row.Reconcile(incoming));
        ScreenshotGallery.Visibility = IsVisible(rows.Length > 0 && !demos);
        FileTable.Visibility = IsVisible(rows.Length > 0 && demos);
        Empty.Visibility = IsVisible(rows.Length == 0);
        if (selected is not null)
        {
            var target = view.FirstOrDefault(item => item.Name == selected);
            if (!ReferenceEquals(list.SelectedItem, target))
                list.SelectedItem = target;
        }
        EmptyBody.Content = rows.Length > 0 ? null
            : !string.IsNullOrWhiteSpace(Search.Text) ? LibraryEmpty("No matching files", "Try another search.")
            : demos ? RecordingEmpty() : LibraryEmpty("No screenshots",
                "Take one in a match with F12. It appears here when you come back.",
                LibraryAction("Show Screenshots Folder", OpenCurrentFolderAsync));
    }

    private void OnScreenshotLoaded(object sender, RoutedEventArgs args) => _ = ShowScreenshotAsync((Image)sender);
    private void OnScreenshotChanged(FrameworkElement sender, DataContextChangedEventArgs args) => _ = ShowScreenshotAsync((Image)sender);
    private void OnDemoArtLoaded(object sender, RoutedEventArgs args) => ShowDemoArt((Image)sender);
    private void OnDemoArtChanged(FrameworkElement sender, DataContextChangedEventArgs args) => ShowDemoArt((Image)sender);
    private void OnDemoMenuLoaded(object sender, RoutedEventArgs args) => StyleDemoMenu((Button)sender);
    private void OnDemoMenuContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => StyleDemoMenu((Button)sender);

    private void OnDemoSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        foreach (var item in args.RemovedItems.Concat(args.AddedItems))
            if (Files.ContainerFromItem(item) is ListViewItem { ContentTemplateRoot: Grid row } &&
                row.FindName("DemoMenuButton") is Button menu)
                StyleDemoMenu(menu);
    }

    private void StyleDemoMenu(Button menu)
    {
        bool selected = menu.DataContext is MediaRow item && ReferenceEquals(item, Files.SelectedItem);
        menu.Style = (Style)Files.Resources[selected ? "SelectedDemoMenuAction" : "DemoMenuAction"];
    }

    private void ShowDemoArt(Image image)
    {
        if (image.DataContext is MediaRow row)
            _ = previews.FillAsync(image, row.Facts.Map ?? row.Name, squareAsFourByThree: true);
        else
        {
            image.Tag = null;
            image.Source = null;
        }
    }

    private async Task ShowScreenshotAsync(Image tile)
    {
        if (tile.DataContext is not MediaRow row)
        {
            tile.Tag = null;
            tile.Source = null;
            return;
        }
        if (ReferenceEquals(tile.Tag, row))
            return;

        tile.Tag = row;
        tile.Source = MapPreviews.Fallback(row.Facts.Map ?? row.Name);
        tile.Stretch = Stretch.UniformToFill;
        try
        {
            ImageSource thumbnail;
            if (Path.GetExtension(row.Name).Equals(".tga", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = await Task.Run(() =>
                {
                    using var input = files.OpenScreenshot(row.Name);
                    return TgaThumbnail.Read(input, 900);
                });
                if (!ReferenceEquals(tile.Tag, row))
                    return;
                thumbnail = BitmapFromBgra(decoded.Bgra, decoded.Width, decoded.Height);
            }
            else
            {
                using var input = files.OpenScreenshot(row.Name);
                using var source = input.AsRandomAccessStream();
                var bitmap = new BitmapImage { DecodePixelWidth = 900 };
                await bitmap.SetSourceAsync(source);
                thumbnail = bitmap;
            }
            if (ReferenceEquals(tile.Tag, row))
            {
                tile.Source = thumbnail;
                tile.Stretch = Stretch.Uniform;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // A file can disappear during preview or be unsupported by the Windows decoder.
        }
    }

    private Task OpenCurrentFolderAsync() => OpenAsync(files.Folder(CurrentKind));

    private async void OnShowFolder(object sender, RoutedEventArgs args) => await shell.RunAsync(OpenCurrentFolderAsync);

    private Task OpenFileAsync(MediaRow row)
    {
        string path = files.Existing(row.Kind, row.File.Name);
        return row.Kind == MediaKind.Screenshot ? OpenAsync(path) : PlayDemoAsync(path);
    }

    internal Task PlayOpenedDemoAsync(string path)
    {
        Sections.SelectedItem = DemosSection;
        return PlayDemoAsync(path);
    }

    private async Task PlayDemoAsync(string path)
    {
        if (session.ClientIsRunning)
            throw new IOException(MediaRow.PlayBlocked);
        string fileName = Path.GetFileName(path);
        if (!MediaLibrary.Accepts(fileName, MediaKind.Demo))
            throw new IOException("Choose a .dm_1 demo recording.");
        string folder = Path.GetDirectoryName(path)!;
        using var parent = new WindowsFiles.DirectoryHandles(folder);
        // Keep the reviewed file stable until it has been staged for the engine.
        using var source = WindowsFiles.Open(folder, fileName, FileShare.Read);
        if (await Task.Run(() => DemoRecording.ReadEdition(source)) is not { } edition)
            throw new IOException("This file isn’t a complete Original 1.3 or CoD2x demo.");

        string name = Path.GetFileNameWithoutExtension(fileName);
        if (!MediaName.Playable(name))
            name = await library.PrepareDemoAsync(new Uri(path), fileName);
        var result = await shell.RunGameStartAsync(edition, () => session.LaunchAsync(edition: edition, demo: name));
        if (result is GameStartState.AlreadyRunning or GameStartState.Attached)
            throw new IOException(MediaRow.PlayBlocked);
    }

}

internal sealed class MediaRow(MediaKind kind, MediaFile file, MediaFacts facts, bool canPlay) : INotifyPropertyChanged
{
    internal const string PlayBlocked = "Close the game before playing a recording.";
    internal MediaKind Kind { get; } = kind;
    internal MediaFile File { get; } = file;
    internal MediaFacts Facts { get; } = facts;
    public bool CanPlay { get; private set; } = canPlay;
    public string Name => File.Name;
    public string ScreenshotTitle => Facts.Map is { } map ? MapCatalog.DisplayName(map) : Path.GetFileNameWithoutExtension(Name);
    public string ScreenshotName => $"Screenshot, {ScreenshotTitle}, {Stamp}";
    public string DemoName => $"Demo {Name}, {DemoDetail}, {Stamp}, {Size}";
    public string PlayName => $"Play {Name}";
    public string PlaybackHint => CanPlay ? "" : PlayBlocked;
    public string MenuName => $"More actions for {Name}";
    public string DemoDetail => Facts.Map is { } map ? $"{MapCatalog.DisplayName(map)} · Recorded match" : "Recorded match";
    public string Stamp => Facts.Stamp;
    public string Size => Facts.Size;
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
