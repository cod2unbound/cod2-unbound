using System.Diagnostics;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class LibraryPage : Page
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly WindowsMediaFiles files;
    private readonly MediaLibrary library;
    private bool ready;

    internal LibraryPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        this.session = session;
        files = new(session.HomePath);
        library = new(files);
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
        Files.ItemsSource = items.Select(file => new MediaRow(kind, file)).ToArray();
        Files.Visibility = IsVisible(!items.IsEmpty);
        Empty.Visibility = IsVisible(items.IsEmpty);
        Empty.Text = string.IsNullOrWhiteSpace(Search.Text) ? kind switch
        {
            MediaKind.Screenshot => "No screenshots yet.",
            MediaKind.Demo => "No demos yet.",
            _ => "No config files yet."
        } : "No matching files.";
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

        if (session.ClientIsRunning)
            throw new IOException("Close the game before playing a demo.");
        string name = Path.GetFileNameWithoutExtension(path);
        if (!MediaName.Playable(name))
            name = await library.PrepareDemoAsync(new Uri(path), row.File.Name);
        GameStartState result = await session.LaunchAsync(demo: name);
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
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "Move this file to the Recycle Bin?", Content = Caption(row.File.Name),
            PrimaryButtonText = "Move to Recycle Bin", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            files.Recycle(row.Kind, row.File.Name, WinRT.Interop.WindowNative.GetWindowHandle(shell));
            Render();
        }
    }
}

internal sealed record MediaRow(MediaKind Kind, MediaFile File)
{
    public string Name => File.Name;
    public string MenuName => $"More options for {Name}";
    public override string ToString() => Name;

    public string Detail
    {
        get
        {
            var facts = MediaFacts.From(File, TimeZoneInfo.Local);
            return string.Join(" · ", new[] { facts.Map is { } map ? MapCatalog.DisplayName(map) : "",
                facts.Stamp, facts.Size }.Where(value => value.Length > 0));
        }
    }
}
