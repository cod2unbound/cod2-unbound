using CoD2Unbound.Core;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows;

// Jump to… (Ctrl+K), the Mac's JumpPalette: one field over the window that finds servers already in memory
// (never a network request), loadouts, every Settings pane and row by its visible name, and the commands the
// Mac keeps in its menu bar. Up and Down move, Enter opens, Ctrl+Enter launches on a selected server, Escape
// or a click outside closes. On a loadout in Configs it searches that loadout's settings instead.
public sealed partial class MainWindow
{
    private const string AppJumpPlaceholder = "Servers, loadouts, settings and actions";
    private const string SettingJumpPlaceholder = "Setting or console name";
    private Control? jumpReturnFocus;
    private bool jumpOpen;
    private bool jumpEnabled;
    private bool jumpSettings;
    private int jumpGeneration;
    private IReadOnlyList<ConfigProfile> jumpLoadouts = [];
    private JumpTarget[] jumpResults = [];
    private ListViewItem[] jumpRows = [];
    private readonly HashSet<uint> jumpResultPresses = [];
    private bool? jumpPendingSelection;

    private void InitializeJumpTo()
    {
        InitializeShellShortcuts();
        JumpBox.PreviewKeyDown += OnJumpKeyDown;
        JumpBox.TextChanged += (_, _) =>
        {
            if (jumpOpen)
                RenderJumpResults();
        };
        // A click outside dismisses the popup, as on the Mac; the field keeps focus while it is open.
        JumpPopup.Closed += (_, _) =>
        {
            if (jumpOpen)
                CloseJumpTo(restoreFocus: false);
        };
        Root.SizeChanged += (_, _) => PlaceJumpTo();
        foreach (var (keys, words) in new[] { (new[] { "↑", "↓" }, "to move"), (["Enter"], "to open"), (["Ctrl+Enter"], "to launch on a server") })
        {
            var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            foreach (string key in keys)
                hint.Children.Add(KeyCap(key));
            hint.Children.Add(new TextBlock
            {
                Text = words, Style = StyleFor("JumpFooterText")
            });
            JumpFooter.Children.Add(hint);
        }
    }

    private static Border KeyCap(string key) => new()
    {
        Style = StyleFor("KeyCapLabel"),
        Child = new TextBlock { Text = key, Style = StyleFor("KeyCapText") }
    };

    // HomeJump board: 640 wide, centred, 122 below the window's top.
    private void PlaceJumpTo()
    {
        bool largeText = new global::Windows.UI.ViewManagement.UISettings().TextScaleFactor > 1.2;
        JumpPanel.Width = Math.Min(largeText ? 780 : 640, Math.Max(0, Root.ActualWidth - 32));
        JumpFooter.Orientation = largeText ? Orientation.Vertical : Orientation.Horizontal;
        JumpFooter.Spacing = largeText ? 4 : 12;
        JumpList.MaxHeight = largeText ? Math.Clamp(Root.ActualHeight - 360, 130, 380) : 380;
        JumpPopup.HorizontalOffset = Math.Max(16, (Root.ActualWidth - JumpPanel.Width) / 2);
        JumpPopup.VerticalOffset = 122;
        JumpPanel.MaxHeight = Math.Max(200, Root.ActualHeight - 138);
    }

    internal async void OpenJumpTo()
    {
        if (!jumpEnabled || !initialized)
            return;
        if (jumpOpen)
        {
            JumpBox.Focus(FocusState.Keyboard);
            JumpBox.SelectAll();
            return;
        }
        jumpReturnFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        jumpOpen = true;
        int generation = ++jumpGeneration;
        jumpSettings = ContentFrame.Content is ConfigsPage { ShowsLoadout: true };
        JumpBox.PlaceholderText = jumpSettings ? SettingJumpPlaceholder : AppJumpPlaceholder;
        JumpBox.Text = "";
        PlaceJumpTo();
        JumpPopup.IsOpen = true;
        JumpBox.Focus(FocusState.Keyboard);
        RenderJumpResults();
        if (!jumpSettings)
        {
            await ShowProblemAsync(async () =>
            {
                var profiles = await session.ReadLoadoutChoicesAsync();
                if (jumpOpen && generation == jumpGeneration)
                {
                    jumpLoadouts = profiles;
                    if (JumpBox.Text.Length != 0)
                        RenderJumpResults(preserveSelection: true);
                }
            });
        }
    }

    private void CloseJumpTo(bool restoreFocus)
    {
        if (!jumpOpen)
            return;
        jumpOpen = false;
        ++jumpGeneration;
        JumpPopup.IsOpen = false;
        JumpList.Items.Clear();
        jumpResults = [];
        jumpRows = [];
        AutomationProperties.SetItemStatus(JumpBox, "");
        jumpResultPresses.Clear();
        jumpPendingSelection = null;
        var previous = jumpReturnFocus;
        jumpReturnFocus = null;
        if (restoreFocus)
        {
            if (previous is not { IsLoaded: true } || !previous.Focus(FocusState.Keyboard))
                FocusPageAction();
        }
    }

    private void RenderJumpResults(bool preserveSelection = false)
    {
        preserveSelection &= jumpPendingSelection ?? true;
        // Keep the pressed button alive until WinUI dispatches release or cancellation.
        if (jumpResultPresses.Count != 0)
        {
            jumpPendingSelection = preserveSelection;
            return;
        }
        jumpPendingSelection = null;

        string query = JumpBox.Text;
        JumpTarget[] results = [.. JumpResults(query)];
        bool empty = results.Length == 0;
        string previousEmpty = JumpEmpty.Text;
        JumpList.Visibility = IsVisible(!empty);
        JumpEmpty.Visibility = IsVisible(empty);
        if (empty)
            JumpEmpty.Text = jumpSettings
                ? "No setting matches. Try a word like “jump”, “turn” or “sound”."
                : $"Nothing matches “{query.Trim()}”. Try a server, map, loadout or setting name.";
        bool sameResults = results.SequenceEqual(jumpResults);
        string count = results.Length == 1 ? "1 result" : $"{results.Length} results";
        if (AutomationProperties.GetItemStatus(JumpBox) != count || !sameResults ||
            (empty && JumpEmpty.Text != previousEmpty))
        {
            AutomationProperties.SetItemStatus(JumpBox, count);
            Announce(JumpBox, empty ? JumpEmpty.Text : count, "JumpResults");
        }
        if (sameResults)
            return;

        int previousIndex = preserveSelection ? JumpIndexOf(JumpList.SelectedItem) : -1;
        JumpTarget? previous = previousIndex >= 0 ? jumpResults[previousIndex] : null;
        jumpResults = results;
        JumpList.Items.Clear();
        var rows = new List<ListViewItem>();
        JumpGroup? group = null;
        foreach (var target in jumpResults)
        {
            if (!jumpSettings && group != target.Group)
            {
                group = target.Group;
                JumpList.Items.Add(new ListViewItem
                {
                    Style = StyleFor("JumpHeaderItem"),
                    Content = new TextBlock { Text = GroupName(target.Group), Style = StyleFor("GroupLabelText") }
                });
            }
            var row = JumpRow(target, rows.Count + 1, results.Length);
            rows.Add(row);
            JumpList.Items.Add(row);
        }
        jumpRows = [.. rows];
        int selected = previous is null ? 0 : Array.FindIndex(jumpResults,
            target => target.Group == previous.Group && target.Id == previous.Id);
        SelectJumpRow(empty ? -1 : Math.Max(0, selected));
    }

    // The Mac's result list: with nothing typed, the three most recent servers and the first actions; with a
    // query, at most six of each kind (five actions), so the first screen shows every kind of result. Actions
    // that cannot run now are left out, as the Mac leaves them out.
    private IEnumerable<JumpTarget> JumpResults(string query)
    {
        var configs = ContentFrame.Content as ConfigsPage;
        if (jumpSettings && configs is not null)
            return JumpIndex.Search(configs.JumpSettings(), query, settingsOnly: true);
        if (string.IsNullOrWhiteSpace(query))
        {
            var recent = session.Library.Recent.Take(3).Select(session.ServerForAddress).Select(JumpIndex.Server);
            return recent.Concat(JumpActions().Where(action => action.Enabled).Take(6));
        }
        var counts = new Dictionary<JumpGroup, int>();
        return JumpIndex.Search(AppJumpTargets(), query).Where(target => target.Enabled).Where(target =>
        {
            counts[target.Group] = counts.GetValueOrDefault(target.Group) + 1;
            return counts[target.Group] <= (target.Group == JumpGroup.Actions ? 5 : 6);
        });
    }

    private ListViewItem JumpRow(JumpTarget target, int position, int count)
    {
        var line = new Grid { ColumnSpacing = 12 };
        bool largeText = JumpFooter.Orientation == Orientation.Vertical;
        GlyphAlign.SetIsEnabled(line, true);
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = largeText ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = largeText ? new GridLength(0) : new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new FontIcon
        {
            FontFamily = (FontFamily)Application.Current.Resources["FluentRegular"], FontSize = 15,
            Glyph = new JumpSuggestion(target, "", false, null).Icon, VerticalAlignment = VerticalAlignment.Center,
            Foreground = IsHighContrast ? null : ThemeBrush("chipIcon", Root.ActualTheme, false)
        });
        Put(line, 1, new TextBlock
        {
            Text = target.Title, Style = StyleFor("BodyStrongTextBlockStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = largeText ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Foreground = IsHighContrast ? null : ThemeBrush("text", Root.ActualTheme, false)
        });
        if (target.Detail.Length != 0)
        {
            var detail = new TextBlock
            {
                Text = target.Detail, Style = StyleFor("BodyTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Foreground = IsHighContrast ? null : ThemeBrush("secondary", Root.ActualTheme, false)
            };
            if (largeText)
            {
                line.RowDefinitions.Add(new() { Height = GridLength.Auto });
                line.RowDefinitions.Add(new() { Height = GridLength.Auto });
                detail.TextWrapping = TextWrapping.Wrap;
                Grid.SetRow(detail, 1);
                Grid.SetColumnSpan(detail, 3);
            }
            Put(line, largeText ? 1 : 2, detail);
        }
        var hint = new TextBlock
        {
            Style = StyleFor("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("tertiary", Root.ActualTheme, IsHighContrast), Text = target.Keys
        };
        Put(line, 3, hint);
        if (IsHighContrast)
            foreach (var element in line.Children)
                if (element is TextBlock text)
                    text.ClearValue(TextBlock.ForegroundProperty);
                else if (element is FontIcon icon)
                    icon.ClearValue(FontIcon.ForegroundProperty);
        var action = new Button { Style = StyleFor("JumpResultAction"), Content = line };
        action.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnJumpResultPointerPressed), handledEventsToo: true);
        PointerEventHandler finished = OnJumpResultPointerFinished;
        action.AddHandler(UIElement.PointerReleasedEvent, finished, handledEventsToo: true);
        action.AddHandler(UIElement.PointerCaptureLostEvent, finished, handledEventsToo: true);
        action.AddHandler(UIElement.PointerCanceledEvent, finished, handledEventsToo: true);
        var row = new ListViewItem { Style = StyleFor("JumpRowItem"), Content = action, Tag = (target, hint) };
        // Selection stays on the native row for the search keys and UI Automation. The button invokes.
        string label = target.Detail.Length == 0 ? target.Title : $"{target.Title}, {target.Detail}";
        AutomationProperties.SetName(row, label);
        AutomationProperties.SetName(action, label);
        foreach (var element in new Control[] { row, action })
        {
            AutomationProperties.SetPositionInSet(element, position);
            AutomationProperties.SetSizeOfSet(element, count);
            if (target.Group == JumpGroup.Servers)
                AutomationProperties.SetHelpText(element, "Enter shows it in Servers. Ctrl+Enter launches on it.");
        }
        action.Click += async (_, _) =>
        {
            if (jumpOpen && JumpIndexOf(row) >= 0)
                await OpenJumpTargetAsync(target, launch: false);
        };
        if (target.Group == JumpGroup.Actions)
            AutomationProperties.SetAutomationId(row, $"JumpCommand{target.Id}");
        PaintJumpRow(row, selected: false);
        return row;
    }

    private void OnJumpResultPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Button action && args.GetCurrentPoint(action).Properties.IsLeftButtonPressed)
            jumpResultPresses.Add(args.Pointer.PointerId);
    }

    private void OnJumpResultPointerFinished(object sender, PointerRoutedEventArgs args)
    {
        if (!jumpResultPresses.Remove(args.Pointer.PointerId) || jumpPendingSelection is null)
            return;
        int generation = jumpGeneration;
        // Let the native button finish dispatching this release before applying queued results.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (jumpOpen && generation == jumpGeneration && jumpPendingSelection is bool preserveSelection)
                RenderJumpResults(preserveSelection);
        });
    }

    private int JumpIndexOf(object? item) => item is ListViewItem row ? Array.IndexOf(jumpRows, row) : -1;

    private void SelectJumpRow(int index)
    {
        if (jumpRows.ElementAtOrDefault(index) is not { } row)
        {
            JumpList.SelectedItem = null;
            return;
        }
        JumpList.SelectedItem = row;
        JumpList.ScrollIntoView(row);
    }

    private void OnJumpSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        foreach (var item in args.RemovedItems.OfType<ListViewItem>())
            PaintJumpRow(item, selected: false);
        foreach (var item in args.AddedItems.OfType<ListViewItem>())
            PaintJumpRow(item, selected: true);
    }

    // The board's selected server row offers its launch key; other rows show their shortcut.
    private void PaintJumpRow(ListViewItem row, bool selected)
    {
        if (row.Tag is not (JumpTarget target, TextBlock hint) || row.Content is not Button action)
            return;
        bool server = target.Group == JumpGroup.Servers;
        hint.Text = server ? (selected ? "Launch Ctrl+Enter" : "") : target.Keys;
        if (IsHighContrast)
        {
            hint.ClearValue(TextBlock.ForegroundProperty);
            action.Foreground = ThemeBrush(selected ? "SelectionTextBrush" : "text", Root.ActualTheme, true);
        }
        else
            hint.Foreground = ThemeBrush(server ? "secondary" : "tertiary", Root.ActualTheme, false);
    }

    private async void OnJumpKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!jumpOpen)
            return;
        switch (args.Key)
        {
            case VirtualKey.Escape:
                args.Handled = true;
                CloseJumpTo(restoreFocus: true);
                break;
            case VirtualKey.Up:
            case VirtualKey.Down:
                args.Handled = true;
                if (jumpResults.Length != 0)
                    SelectJumpRow(Math.Clamp(JumpIndexOf(JumpList.SelectedItem) + (args.Key == VirtualKey.Up ? -1 : 1), 0, jumpResults.Length - 1));
                break;
            case VirtualKey.Enter:
                args.Handled = true;
                if (jumpResults.ElementAtOrDefault(JumpIndexOf(JumpList.SelectedItem)) is { } target)
                    await OpenJumpTargetAsync(target, launch: ControlKeyDown());
                break;
        }
    }

    private static bool ControlKeyDown() =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) &
         global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private IEnumerable<JumpTarget> AppJumpTargets()
    {
        var library = session.Library;
        foreach (var server in library.Cache.Concat(library.Favorites.Concat(library.Recent).Select(session.ServerForAddress))
            .DistinctBy(server => server.Address))
            yield return JumpIndex.Server(server);
        foreach (var profile in jumpLoadouts)
            yield return JumpIndex.Loadout(profile);
        foreach (var setting in SettingsPage.JumpTargets)
            yield return setting;
        if (jumpLoadouts.Count != 0)
            foreach (var binding in ConfigsPage.JumpBindings(session.Settings.Working.Engine))
                yield return binding;
        foreach (var action in JumpActions())
            yield return action;
    }

    private async Task OpenJumpTargetAsync(JumpTarget target, bool launch)
    {
        CloseJumpTo(restoreFocus: true);
        if (target.Group == JumpGroup.Actions && Enum.TryParse<ShellCommand>(target.Id, out var command))
        {
            await RunShellCommandAsync(command);
            return;
        }
        if (target.Group == JumpGroup.Settings)
        {
            OpenSettingsWindow(target.Id);
            return;
        }
        await RunAsync(async () =>
        {
            switch (target.Group)
            {
                case JumpGroup.Servers when launch && CanRunShellCommand(ShellCommand.Launch):
                    await LaunchFromShellAsync(useServer: true, recentAddress: target.Id);
                    break;
                case JumpGroup.Servers:
                    await NavigateAsync(LauncherPlace.Servers, ServerScope.All, target.Id, refreshServers: false);
                    ((ServersPage)ContentFrame.Content).RevealJumpServer(target.Id);
                    break;
                case JumpGroup.Loadouts:
                    await WithConfigsAsync(configs => { configs.OpenJumpLoadout(target.Id); return Task.CompletedTask; });
                    break;
                case JumpGroup.Bindings:
                    await WithConfigsAsync(configs => { configs.OpenJumpBinding(target.Id); return Task.CompletedTask; });
                    break;
                case JumpGroup.ConfigSettings when ContentFrame.Content is ConfigsPage configs:
                    configs.OpenJumpSetting(target.Id);
                    break;
            }
        });
    }

    private static string GroupName(JumpGroup group) => group == JumpGroup.Bindings ? "Bind actions" : group.ToString();
}
