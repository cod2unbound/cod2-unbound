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
        double itemWidth = Math.Floor(width / columns);
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
        if (FindMedia(args.OriginalSource as DependencyObject) is not { } item)
            return;
        args.Handled = true;
        ShowFileMenu(item.Row, item.Container,
            args.TryGetPosition(item.Container, out var position) ? new FlyoutShowOptions { Position = position } : null);
    }

    private async void OnMediaDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (FindMedia(args.OriginalSource as DependencyObject) is not { FromButton: false } item)
            return;
        args.Handled = true;
        if (item.Row.CanPlay)
            await shell.RunAsync(() => OpenFileAsync(item.Row));
    }

    private async void OnLibraryKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (ShowingMatches || XamlRoot is not { } root)
            return;
        var focus = FocusManager.GetFocusedElement(root) as DependencyObject;
        if (args.Key == VirtualKey.Escape && Search.Text.Length != 0)
        {
            args.Handled = true;
            ClearSearch();
            MediaList.Focus(FocusState.Keyboard);
            return;
        }
        if (FindMedia(focus) is not { } item)
            return;

        var row = item.Row;
        bool control = KeyPressed(VirtualKey.Control);
        bool shift = KeyPressed(VirtualKey.Shift);
        bool alt = KeyPressed(VirtualKey.Menu);
        if (!control && !alt && (args.Key == VirtualKey.Application || (shift && args.Key == VirtualKey.F10)))
        {
            args.Handled = true;
            ShowFileMenu(row, item.Container);
            return;
        }
        if (!item.FromButton && args.Key == VirtualKey.Space && !control && !alt && !shift)
        {
            args.Handled = true;
            return;
        }

        Func<Task>? action = (args.Key, control, shift, alt) switch
        {
            (VirtualKey.Enter, false, false, false) when !item.FromButton && row.CanPlay => () => OpenFileAsync(row),
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

    private static (MediaRow Row, SelectorItem Container, bool FromButton)? FindMedia(DependencyObject? source)
    {
        bool fromButton = false;
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            fromButton |= current is Button;
            if (current is SelectorItem { Content: MediaRow row } container)
                return (row, container, fromButton);
        }
        return null;
    }

    private void ShowFileMenu(MediaRow row, FrameworkElement anchor, FlyoutShowOptions? options = null)
    {
        MediaList.SelectedItem = row;
        bool demo = row.Kind == MediaKind.Demo;
        var menu = new MenuFlyout();
        Add(demo ? "Play" : "Open", () => OpenFileAsync(row), "Enter", row.CanPlay);
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
            Style = StyleFor("CapsuleTextBox"),
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
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
            var (_, message) = MediaLibrary.Rename(row.File, name.Text);
            ShowProblem(message);
            dialog.IsPrimaryButtonEnabled = message is null;
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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
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
        RecycleNotice.Visibility = Visibility.Visible;
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
            Text = title.ToUpperInvariant(), Style = StyleFor("EmptyHeading"), TextAlignment = TextAlignment.Center,
            FontSize = 26, CharacterSpacing = 119
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
