using System.Runtime.InteropServices;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI.Core;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class LibraryPage
{
    private RecycledMedia? recycled;

    private nint LibraryOwner => WinRT.Interop.WindowNative.GetWindowHandle(shell);
    private ListViewBase MediaList => CurrentKind == MediaKind.Screenshot ? Screenshots : Files;

    private void OnScreenshotPanelLoaded(object sender, RoutedEventArgs args) =>
        SizeScreenshotTiles((ItemsWrapGrid)sender, Screenshots.ActualWidth);

    private void OnScreenshotGridSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (Screenshots.ItemsPanelRoot is ItemsWrapGrid grid)
            SizeScreenshotTiles(grid, args.NewSize.Width);
    }

    private static void SizeScreenshotTiles(ItemsWrapGrid grid, double width)
    {
        if (width <= 18)
            return;
        const double gap = 18, minimumTile = 240, caption = 36;
        int columns = Math.Max(1, (int)(width / (minimumTile + gap)));
        double itemWidth = width / columns;
        grid.ItemWidth = itemWidth;
        grid.ItemHeight = Math.Round((itemWidth - gap) * 0.75) + caption + gap;
    }

    private async void OnPlayDemo(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: MediaRow { CanPlay: true } row })
            await shell.RunAsync(() => OpenFileAsync(row));
    }

    private void OnFileMenu(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: MediaRow row } button)
            ShowFileMenu(row, button);
    }

    private void OnMediaContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: MediaRow row } || args.Handled)
            return;
        var anchor = MediaList.ContainerFromItem(row) as FrameworkElement ?? (FrameworkElement)sender;
        args.Handled = true;
        ShowFileMenu(row, anchor,
            args.TryGetPosition(anchor, out var position) ? new FlyoutShowOptions { Position = position } : null);
    }

    private async void OnMediaDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: MediaRow row } element ||
            element.FindName("DemoActions") is FrameworkElement actions && IsInside(args.GetPosition(actions), actions))
            return;
        args.Handled = true;
        if (row.CanPlay)
            await shell.RunAsync(() => OpenFileAsync(row));
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && ClearSearch())
            args.Handled = true;
    }

    private async void OnLibraryKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (ShowingMatches || XamlRoot is not { } root)
            return;
        var focus = FocusManager.GetFocusedElement(root);
        if (args.Key == VirtualKey.Escape && (ClearSearch() || MediaList.SelectedItem is not null))
        {
            args.Handled = true;
            MediaList.SelectedItem = null;
            return;
        }
        var row = focus switch
        {
            SelectorItem { Content: MediaRow media } => media,
            Button { Tag: MediaRow media } => media,
            FrameworkElement { DataContext: MediaRow media } => media,
            _ => null
        };
        if (row is null || MediaList.ContainerFromItem(row) is not SelectorItem container)
            return;

        bool fromButton = focus is Button;
        bool control = KeyPressed(VirtualKey.Control);
        bool shift = KeyPressed(VirtualKey.Shift);
        bool alt = KeyPressed(VirtualKey.Menu);
        if (!control && !alt && (args.Key == VirtualKey.Application || (shift && args.Key == VirtualKey.F10)))
        {
            args.Handled = true;
            ShowFileMenu(row, container);
            return;
        }
        Func<Task>? action = (args.Key, control, shift, alt) switch
        {
            (VirtualKey.Space, false, false, false) when !fromButton => () => PreviewMediaAsync(row),
            (VirtualKey.Enter, false, false, false) when !fromButton && row.CanPlay => () => OpenFileAsync(row),
            (VirtualKey.F2, false, false, false) when row.Kind == MediaKind.Demo => () => RenameAsync(row),
            (VirtualKey.Delete, false, false, false) => () => RecycleAsync(row),
            (VirtualKey.C, true, false, false) => () => CopyMediaAsync(row),
            _ => null
        };
        if (action is null)
            return;
        args.Handled = true;
        MediaList.SelectedItem = row;
        await shell.RunAsync(action);
    }

    private static bool KeyPressed(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void ShowFileMenu(MediaRow row, FrameworkElement anchor, FlyoutShowOptions? options = null)
    {
        MediaList.SelectedItem = row;
        bool demo = row.Kind == MediaKind.Demo;
        var menu = new MenuFlyout();
        if (anchor is Button)
            menu.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
        Add(demo ? "Play" : "Open", () => OpenFileAsync(row), "Enter", row.CanPlay);
        Add("Preview", () => PreviewMediaAsync(row), LauncherShortcuts.Keys(ShellCommand.PreviewMedia));
        Add("Show in File Explorer", () => RevealMediaAsync(row));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (demo)
            Add("Rename…", () => RenameAsync(row), "F2");
        Add(demo ? "Copy File" : "Copy Image", () => CopyMediaAsync(row), "Ctrl+C");
        if (!demo)
            Add("Share", () => files.ShareAsync(row.Kind, row.Name, shell.LibraryShare));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Move to Recycle Bin", () => RecycleAsync(row), "Delete");
        if (options is null)
            menu.ShowAt(anchor);
        else
            menu.ShowAt(anchor, options);

        void Add(string title, Func<Task> action, string? shortcut = null, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = title, IsEnabled = enabled };
            if (!enabled)
                AutomationProperties.SetHelpText(item, row.PlaybackHint);
            if (shortcut is not null)
                item.KeyboardAcceleratorTextOverride = shortcut;
            item.Click += async (_, _) => await shell.RunAsync(action);
            menu.Items.Add(item);
        }
    }

    private async Task PreviewMediaAsync(MediaRow row)
    {
        bool demo = row.Kind == MediaKind.Demo;
        var name = Body(row.Name);
        name.TextWrapping = TextWrapping.Wrap;
        var content = new StackPanel { Spacing = 12, Children = { name } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = demo ? "Demo Preview" : "Screenshot Preview",
            Content = content, CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close
        }.WithSymbol(demo ? "FluentFilmstrip" : "FluentImage");

        if (demo)
        {
            string map = row.Facts.Map is { } key ? MapCatalog.DisplayName(key) : "Unknown";
            string date = row.File.Modified?.ToLocalTime().ToString("g") ?? "Unknown";
            var details = Body($"Map: {map}\nLength: Reading…\nDate: {date}\nSize: {row.Size}");
            content.Children.Add(details);
            dialog.PrimaryButtonText = "Play";
            dialog.IsPrimaryButtonEnabled = row.CanPlay;
            if (!row.CanPlay)
                content.Children.Add(Body(row.PlaybackHint));
            using var reading = new CancellationTokenSource();
            var show = dialog.ShowSheetAsync();
            var duration = LoadDurationAsync();
            ContentDialogResult choice;
            try { choice = await show; }
            finally
            {
                await reading.CancelAsync();
                await duration;
            }
            if (choice == ContentDialogResult.Primary)
                await OpenFileAsync(row);
            return;

            async Task LoadDurationAsync()
            {
                TimeSpan? elapsed = null;
                try
                {
                    elapsed = await Task.Run(() =>
                    {
                        using var recording = files.OpenDemo(row.Name);
                        return DemoRecording.ReadDuration(recording, reading.Token);
                    }, reading.Token);
                }
                catch (OperationCanceledException) when (reading.IsCancellationRequested) { return; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                if (!reading.IsCancellationRequested)
                {
                    string length = elapsed is { } time ? MatchFacts.Duration(time.TotalSeconds) : "Unknown";
                    details.Text = $"Map: {map}\nLength: {length}\nDate: {date}\nSize: {row.Size}";
                }
            }
        }

        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        dialog.Width = 760;
        var image = new Image { MaxHeight = 360, Stretch = Stretch.Uniform };
        var status = Body("");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        content.Children.Add(new Grid { Children = { image, status } });

        MediaRow[] items = [.. screenshots];
        int index = Array.IndexOf(items, row);
        if (index < 0)
            return;
        var previous = new Button { Content = "Previous" };
        var next = new Button { Content = "Next" };
        if (items.Length > 1)
            content.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, Children = { previous, next }
            });

        int request = 0;
        previous.Click += async (_, _) => await ShowAtAsync(index - 1);
        next.Click += async (_, _) => await ShowAtAsync(index + 1);
        await ShowAtAsync(index);
        try { await dialog.ShowSheetAsync(); }
        finally { request++; }

        async Task ShowAtAsync(int position)
        {
            if (position < 0 || position >= items.Length)
                return;
            int current = ++request;
            index = position;
            string filename = items[position].Name;
            name.Text = filename;
            AutomationProperties.SetName(image, filename);
            previous.IsEnabled = position > 0;
            next.IsEnabled = position + 1 < items.Length;
            image.Source = null;
            status.Text = "Loading screenshot…";
            status.Visibility = Visibility.Visible;

            ImageSource? source = null;
            string failure = "This screenshot format can’t be previewed.";
            try { source = await files.ReadScreenshotPreviewAsync(filename); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                failure = "This screenshot is unavailable.";
            }
            catch (Exception error) when (error is IOException or COMException or ArgumentException or NotSupportedException)
            {
                // The Windows codec or TGA decoder could not read the file.
            }
            if (current != request)
                return;
            image.Source = source;
            status.Text = source is null ? failure : "";
            status.Visibility = IsVisible(source is null);
        }
    }

    private Task RevealMediaAsync(MediaRow row)
    {
        files.Reveal(row.Kind, row.Name);
        return Task.CompletedTask;
    }

    private async Task CopyMediaAsync(MediaRow row)
    {
        bool screenshot = row.Kind == MediaKind.Screenshot;
        try
        {
            await files.CopyAsync(row.Kind, row.Name);
            ShowNotice(screenshot ? "Copied the screenshot. Paste it into a message or an image editor."
                : $"Copied “{row.Name}”. Paste it in File Explorer to make a copy.", InfoBarSeverity.Success);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
            ShowNotice(screenshot ? "This screenshot couldn’t be copied. Show it in File Explorer and try again."
                : "This recording couldn’t be copied. Show it in File Explorer and try again.", InfoBarSeverity.Error);
        }
    }

    private async Task RenameAsync(MediaRow row)
    {
        var name = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(row.Name),
            MaxLength = 251,
            Style = StyleFor("InlineTextBox"),
            FontFamily = (FontFamily)Application.Current.Resources["CourierPrime"]
        };
        AutomationProperties.SetName(name, "Recording name");
        var problemLabel = Caption("");
        var problem = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed,
            Children = { new FontIcon
            {
                FontFamily = (FontFamily)Application.Current.Resources["SystemIcons"],
                Glyph = (string)Application.Current.Resources["SystemWarning"], FontSize = 14
            }, problemLabel }
        };
        GlyphAlign.SetIsEnabled(problem, true);
        AutomationProperties.SetLiveSetting(problemLabel, AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 8, Children = { name, problem } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "Rename recording", Content = content,
            PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        }.WithSymbol("SystemEdit", "Choose a name you’ll recognize in your library.");
        dialog.Resources["ContentDialogMaxWidth"] = 460d;
        dialog.Resources["ContentDialogMinWidth"] = 460d;
        string? renamed = null;
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
                if (filename != row.Name)
                    files.Rename(row.Kind, row.File, filename);
                renamed = filename;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShowProblem(error is MediaNameInUseException
                    ? "You already have a recording with this name. Choose another."
                    : "This recording couldn’t be renamed. Check that it’s still in the folder and try again.");
                args.Cancel = true;
            }
        };
        Validate();
        if (await dialog.ShowSheetAsync() == ContentDialogResult.Primary)
        {
            Render();
            if (renamed is not null)
            {
                SelectMedia(renamed);
                if (renamed != row.Name)
                    ShowNotice($"Renamed to “{renamed}”.", InfoBarSeverity.Success);
            }
        }

        void Validate()
        {
            bool blank = string.IsNullOrWhiteSpace(name.Text);
            var message = blank ? null : MediaLibrary.Rename(row.File, name.Text).Problem;
            ShowProblem(message);
            dialog.IsPrimaryButtonEnabled = !blank && message is null;
        }

        void ShowProblem(string? message)
        {
            problem.Visibility = IsVisible(message is not null);
            problemLabel.Text = message ?? "";
            bool contrast = new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            var ink = ThemeBrush("negative", name.ActualTheme, contrast);
            ((FontIcon)problem.Children[0]).Foreground = ink;
            problemLabel.Foreground = ink;
            if (message is null)
                name.ClearValue(Control.BorderBrushProperty);
            else
                name.BorderBrush = ink;
        }
    }

    private async Task RecycleAsync(MediaRow row)
    {
        var dialog = LibraryRemovalDialog($"Move “{row.Name}” to the Recycle Bin?",
            "You can put it back with Undo, or from the Recycle Bin until you empty it.", "Move to Recycle Bin");
        if (await dialog.ShowSheetAsync() != ContentDialogResult.Primary)
            return;

        string token;
        try { token = files.Recycle(row.Kind, row.Name, LibraryOwner); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
            ShowNotice("This file couldn’t be moved to the Recycle Bin. Show it in File Explorer and try again.",
                InfoBarSeverity.Error, LibraryAction("Show in File Explorer", () => RevealMediaAsync(row)));
            return;
        }

        recycled = new(row.Kind, row.Name, token);
        RecycleNotice.Message = $"Moved “{row.Name}” to the Recycle Bin.";
        AutomationProperties.SetName(RecycleNotice.ActionButton, $"Undo moving {row.Name} to the Recycle Bin");
        RecycleNotice.IsOpen = true;
        Notice.IsOpen = false;
        Render();
    }

    private void OnRecycleNoticeClosed(InfoBar sender, object args) => recycled = null;

    private async void OnUndoRecycle(object sender, RoutedEventArgs args)
    {
        await shell.RunAsync(() =>
        {
            if (recycled is not { } item)
                return Task.CompletedTask;
            recycled = null;
            RecycleNotice.IsOpen = false;

            try
            {
                files.Restore(item.Kind, item.Name, item.Token, LibraryOwner);
                Render();
                SelectMedia(item.Name);
                ShowNotice($"Put “{item.Name}” back.", InfoBarSeverity.Success);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
            {
                ShowNotice($"“{item.Name}” couldn’t be put back. Open the Recycle Bin in File Explorer and choose Restore.",
                    InfoBarSeverity.Error);
            }
            return Task.CompletedTask;
        });
    }

    private void SelectMedia(string name)
    {
        var list = MediaList;
        var row = list.Items.OfType<MediaRow>().FirstOrDefault(item => item.Name == name);
        if (row is null && !string.IsNullOrWhiteSpace(Search.Text))
        {
            Search.Text = "";
            row = list.Items.OfType<MediaRow>().FirstOrDefault(item => item.Name == name);
        }
        if (row is null)
            return;
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        var selected = row;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(list.SelectedItem, selected) && list.ContainerFromItem(selected) is Control container)
                container.Focus(FocusState.Programmatic);
        });
    }

    private ContentDialog LibraryRemovalDialog(string title, string detail, string primary) =>
        new SafeRemovalDialog(title, detail, primary) { XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme };

    private Button LibraryAction(string title, Func<Task> action)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Center };
        button.Click += async (_, _) => await shell.RunAsync(action);
        return button;
    }

    private static StackPanel LibraryEmpty(string title, string detail, Button? action = null)
    {
        var card = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520,
            Margin = new Thickness(0, 130, 0, 12), Spacing = 10
        };
        var heading = new TextBlock
        {
            Text = title.ToUpperInvariant(), Style = StyleFor("EmptyHeading"), TextAlignment = TextAlignment.Center
        };
        AutomationProperties.SetName(heading, title);
        card.Children.Add(heading);
        var message = Body(detail);
        message.MaxWidth = 420;
        message.TextAlignment = TextAlignment.Center;
        card.Children.Add(message);
        if (action is not null)
        {
            action.Margin = new Thickness(0, 4, 0, 0);
            card.Children.Add(action);
        }
        return card;
    }

    private StackPanel RecordingEmpty()
    {
        var card = LibraryEmpty("No recordings", "Record a match and watch it back here, from any point.");
        card.Children.Add(new LibraryRecordingSteps());
        var folder = LibraryAction("Show Demos Folder", OpenCurrentFolderAsync);
        folder.Margin = new Thickness(0, 8, 0, 0);
        card.Children.Add(folder);
        return card;
    }

    private void ShowNotice(string message, InfoBarSeverity severity, Button? action = null)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.ActionButton = action;
        Notice.IsOpen = true;
    }

    private sealed record RecycledMedia(MediaKind Kind, string Name, string Token);
}
