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

    private void OnFileRowLoaded(object sender, RoutedEventArgs args) => SetFileRowColumns((Grid)sender);

    private void OnFileRowChanged(FrameworkElement sender, DataContextChangedEventArgs args) => SetFileRowColumns((Grid)sender);

    private static void SetFileRowColumns(Grid grid)
    {
        if (grid.DataContext is MediaRow row)
            grid.ColumnDefinitions[0].Width = row.FirstColumnWidth;
    }

    private void OnScreenshotGridSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (Screenshots.ItemsPanelRoot is ItemsWrapGrid grid && args.NewSize.Width > 0)
        {
            double available = Math.Max(1, args.NewSize.Width - 20);
            int columns = Math.Clamp((int)(available / 200), 1, 3);
            grid.ItemWidth = Math.Floor(available / columns);
        }
    }

    private async void OnPlayDemo(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: MediaRow row })
            await shell.RunAsync(() => OpenFileAsync(row));
    }

    private void OnFileMenu(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: MediaRow row } button)
            ShowFileMenu(row, button);
    }

    private void OnMediaContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (CurrentKind == MediaKind.Config || FindMedia(args.OriginalSource as DependencyObject) is not { } item)
            return;
        args.Handled = true;
        ShowFileMenu(item.Row, item.Container,
            args.TryGetPosition(item.Container, out var position) ? new FlyoutShowOptions { Position = position } : null);
    }

    private async void OnMediaDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (CurrentKind == MediaKind.Config ||
            FindMedia(args.OriginalSource as DependencyObject) is not { FromButton: false } item)
            return;
        args.Handled = true;
        await shell.RunAsync(() => OpenFileAsync(item.Row));
    }

    private async void OnLibraryKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (ShowingMatches || CurrentKind == MediaKind.Config || XamlRoot is not { } root ||
            FindMedia(FocusManager.GetFocusedElement(root) as DependencyObject) is not { } item)
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
            (VirtualKey.Enter, false, false, false) when !item.FromButton => () => OpenFileAsync(row),
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
        bool config = row.Kind == MediaKind.Config;
        bool demo = row.Kind == MediaKind.Demo;
        var menu = new MenuFlyout();
        Add(demo ? "Play" : "Open", () => OpenFileAsync(row), config ? null : "Enter");
        Add("Show in File Explorer", () => RevealMediaAsync(row));
        if (!config)
        {
            Add(demo ? "Copy" : "Copy image", () => CopyMediaAsync(row), "Ctrl+C");
            Add("Share…", () => files.ShareAsync(row.Kind, row.Name, shell.LibraryShare));
        }
        if (demo || config)
            Add("Rename…", () => RenameAsync(row), demo ? "F2" : null);
        Add("Move to Recycle Bin…", () => RecycleAsync(row), config ? null : "Delete");
        if (options is null)
            menu.ShowAt(anchor);
        else
            menu.ShowAt(anchor, options);

        void Add(string title, Func<Task> action, string? shortcut = null)
        {
            var item = new MenuFlyoutItem { Text = title };
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
        bool config = row.Kind == MediaKind.Config;
        var name = new TextBox
        {
            Header = config ? "Name" : "Recording name",
            Text = Path.GetFileNameWithoutExtension(row.Name),
            MaxLength = 251
        };
        var problem = Wrapped("");
        var content = new StackPanel { Spacing = 8 };
        if (!config)
            content.Children.Add(Wrapped("Choose a name you’ll recognize in your library."));
        content.Children.Add(name);
        content.Children.Add(problem);
        AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
        var dialog = LibraryDialog(config ? "Rename file" : "Rename recording", content, "Rename", ContentDialogButton.Primary);
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
                problem.Text = config ? error.Message : error is MediaNameInUseException
                    ? "A recording already has that name. Choose another."
                    : "This recording couldn’t be renamed. Check that it’s still in the folder and try again.";
                args.Cancel = true;
            }
        };
        Validate();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Render();
            if (!config && renamed is not null)
            {
                SelectMedia(renamed);
                if (renamed != row.Name)
                    ShowNotice($"Renamed to “{renamed}”.", InfoBarSeverity.Success);
            }
        }

        void Validate()
        {
            var (_, message) = MediaLibrary.Rename(row.File, name.Text);
            problem.Text = message ?? "";
            dialog.IsPrimaryButtonEnabled = message is null;
        }
    }

    private async Task RecycleAsync(MediaRow row)
    {
        bool config = row.Kind == MediaKind.Config;
        ContentDialog dialog = config
            ? new SafeRemovalDialog("Move this file to the Recycle Bin?", row.Name, "Move to Recycle Bin")
                { XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme }
            : LibraryRemovalDialog($"Move “{row.Name}” to the Recycle Bin?",
                "You can put it back with Undo, or from the Recycle Bin until you empty it.", "Move to Recycle Bin");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        string token;
        try { token = files.Recycle(row.Kind, row.Name, LibraryOwner); }
        catch (Exception error) when (!config && error is (IOException or UnauthorizedAccessException or COMException))
        {
            ShowNotice("This file couldn’t be moved to the Recycle Bin. Show it in File Explorer and try again.",
                InfoBarSeverity.Error, LibraryAction("Show in File Explorer", () => RevealMediaAsync(row)));
            return;
        }

        if (!config)
        {
            recycled = new(row.Kind, row.Name, token);
            RecycleNotice.Message = $"Moved “{row.Name}” to the Recycle Bin.";
            AutomationProperties.SetName(RecycleNotice.ActionButton, $"Undo moving {row.Name} to the Recycle Bin");
            RecycleNotice.Visibility = Visibility.Visible;
            RecycleNotice.IsOpen = true;
            Notice.IsOpen = false;
        }
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
        var row = (list.ItemsSource as MediaRow[])?.FirstOrDefault(item => item.Name == name);
        if (row is null && !string.IsNullOrWhiteSpace(Search.Text))
        {
            Search.Text = "";
            row = (list.ItemsSource as MediaRow[])?.FirstOrDefault(item => item.Name == name);
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

    private ContentDialog LibraryDialog(string title, FrameworkElement content, string primary, ContentDialogButton defaultButton) =>
        new()
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = title, Content = content,
            PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = defaultButton
        };

    private ContentDialog LibraryRemovalDialog(string title, string detail, string primary) =>
        new SafeRemovalDialog(title, detail, primary)
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Content = Wrapped(detail),
            PrimaryButtonStyle = StyleFor("LauncherAccentButton")
        };

    private Button LibraryAction(string title, Func<Task> action, bool accent = false)
    {
        var button = new Button { Content = title };
        if (accent)
            button.Style = StyleFor("LauncherAccentButton");
        button.Click += async (_, _) => await shell.RunAsync(action);
        return button;
    }

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static StackPanel LibraryEmpty(string glyph, string title, string detail, Button? action = null)
    {
        var card = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520,
            Margin = new Thickness(0, 40, 0, 12), Spacing = 12
        };
        card.Children.Add(Fluent(glyph, 32));
        var heading = new TextBlock { Text = title, Style = StyleFor("SectionTitle"), TextAlignment = TextAlignment.Center };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        card.Children.Add(heading);
        var message = Wrapped(detail);
        message.TextAlignment = TextAlignment.Center;
        card.Children.Add(message);
        if (action is not null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            card.Children.Add(action);
        }
        return card;
    }

    private static StackPanel RecordingEmpty()
    {
        var card = LibraryEmpty("VideoClip", "No recordings", "Record a match and watch it back here, from any point.");
        var steps = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        steps.Children.Add(Wrapped("1. In a match, open the console (the ~ key)."));
        steps.Children.Add(Wrapped("2. Type /record."));
        steps.Children.Add(Wrapped("3. Type /stoprecord."));
        card.Children.Add(steps);
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
