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
    private bool ready;

    internal LibraryPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        this.session = session;
        files = new(session.HomePath);
        library = new(files);
        previews = new(map => Task.Run(() => session.ReadMapArtwork(map)));
        InitializeComponent();
        Sections.SelectedItem = Sections.Items[0];
        ready = true;
        Render();
    }

    private MediaKind CurrentKind => Sections.Items.IndexOf(Sections.SelectedItem) switch
    {
        1 => MediaKind.Demo,
        2 => MediaKind.Config,
        _ => MediaKind.Screenshot
    };

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => TryRender();
    private void OnSearch(object sender, TextChangedEventArgs args) => TryRender();

    private async void TryRender()
    {
        if (ready)
            await shell.ShowProblemAsync(() => { Render(); return Task.CompletedTask; });
    }

    private void Render()
    {
        var kind = CurrentKind;
        var items = library.List(kind, Search.Text);
        var rows = items.Select(file => new MediaRow(kind, file, MediaFacts.From(file, TimeZoneInfo.Local))).ToArray();
        Screenshots.ItemsSource = kind == MediaKind.Screenshot ? rows : null;
        Files.ItemsSource = kind == MediaKind.Screenshot ? null : rows;
        Screenshots.Visibility = IsVisible(!items.IsEmpty && kind == MediaKind.Screenshot);
        FileTable.Visibility = IsVisible(!items.IsEmpty && kind != MediaKind.Screenshot);
        NameHeader.Text = kind == MediaKind.Demo ? "Recording" : "Name";
        MapHeader.Visibility = IsVisible(kind == MediaKind.Demo);
        Empty.Visibility = IsVisible(items.IsEmpty);
        if (!items.IsEmpty)
            return;
        var (icon, heading, detail) = kind switch
        {
            MediaKind.Screenshot => ("Image", "No screenshots yet", "Screenshots you take in the game appear here."),
            MediaKind.Demo => ("VideoClip", "No demos yet", "Demos you record in the game appear here."),
            _ => ("Document", "No config files yet", "Config files saved in the game folder appear here.")
        };
        if (!string.IsNullOrWhiteSpace(Search.Text))
            (heading, detail) = ("No matching files", "Try another search.");
        Empty.Content = EmptyState(icon, heading, detail);
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

    private void OnFileMenu(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || button.Tag is not MediaRow row)
            return;
        var menu = new MenuFlyout();
        Add(row.Kind == MediaKind.Demo ? "Play demo" : "Open", () => OpenFileAsync(row));
        Add("Show in File Explorer", () =>
        {
            files.Reveal(row.Kind, row.File.Name);
            return Task.CompletedTask;
        });
        Add("Rename…", () => RenameAsync(row));
        Add("Move to Recycle Bin…", () => RecycleAsync(row));
        menu.ShowAt(button);

        void Add(string title, Func<Task> call)
        {
            var item = new MenuFlyoutItem { Text = title };
            item.Click += async (_, _) => await shell.RunAsync(call);
            menu.Items.Add(item);
        }
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
        Sections.SelectedItem = Sections.Items[1];
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
        if (!session.HasClient(edition))
            throw new IOException($"Reinstall CoD2 Unbound to get {ConnectDialog.EditionName(edition)}.");

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
        GameStartState result = await session.LaunchAsync(edition: edition, demo: name);
        if (result != GameStartState.Started)
            throw new IOException(result is GameStartState.NeedsSetup or GameStartState.MissingData
                ? "Set up your game files to play demos." : "Close the game before playing a demo.");
    }

    private async Task RenameAsync(MediaRow row)
    {
        var name = new TextBox { Header = "Name", Text = Path.GetFileNameWithoutExtension(row.File.Name), MaxLength = 251 };
        var problem = Caption("");
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(name);
        content.Children.Add(problem);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "Rename file", Content = content, PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        name.TextChanged += (_, _) => Validate();
        dialog.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (MediaLibrary.Rename(row.File, name.Text).FileName is not { } filename)
            {
                args.Cancel = true;
                return;
            }
            try
            {
                if (filename != row.File.Name)
                    files.Rename(row.Kind, row.File, filename);
            }
            catch (IOException error)
            {
                problem.Text = error.Message;
                args.Cancel = true;
            }
        };
        Validate();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            Render();

        void Validate()
        {
            var (_, message) = MediaLibrary.Rename(row.File, name.Text);
            problem.Text = message ?? "";
            dialog.IsPrimaryButtonEnabled = message is null;
        }
    }

    private async Task RecycleAsync(MediaRow row)
    {
        var dialog = new SafeRemovalDialog("Move this file to the Recycle Bin?", row.File.Name, "Move to Recycle Bin")
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            files.Recycle(row.Kind, row.File.Name, WinRT.Interop.WindowNative.GetWindowHandle(shell));
            Render();
        }
    }
}

internal sealed record MediaRow(MediaKind Kind, MediaFile File, MediaFacts Facts)
{
    public string Name => File.Name;
    public string MenuName => $"More actions for {Name}";
    public string Map => Kind == MediaKind.Demo && Facts.Map is { } map ? MapCatalog.DisplayName(map) : "";
    public string Stamp => Facts.Stamp;
    public string Size => Facts.Size;
    public Visibility Demo => IsVisible(Kind == MediaKind.Demo);
    public override string ToString() => Name;
}
