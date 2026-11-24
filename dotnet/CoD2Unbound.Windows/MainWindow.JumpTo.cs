using CoD2Unbound.Core;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private const string AppJumpPlaceholder = "Servers, loadouts, settings and actions";
    private const string IdleJumpPlaceholder = "Jump to…";
    private const string SettingJumpPlaceholder = "Setting or console name";
    private Control? jumpReturnFocus;
    private bool jumpOpen;
    private bool jumpSettings;
    private int jumpGeneration;
    private IReadOnlyList<ConfigProfile> jumpLoadouts = [];
    private JumpSuggestion[] jumpSuggestions = [];
    private int jumpIndex = -1;

    private void InitializeJumpTo()
    {
        InitializeShellShortcuts();
        JumpBox.GettingFocus += (_, args) =>
        {
            if (!jumpOpen && args.OldFocusedElement is Control old)
                jumpReturnFocus = old;
        };
        JumpBox.GotFocus += (_, _) => OpenJumpTo();
        JumpBox.Tapped += (_, _) => OpenJumpTo();
        JumpBox.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (jumpOpen && JumpBox.FocusState == FocusState.Unfocused && !JumpBox.IsSuggestionListOpen)
                CloseJumpTo(restoreFocus: false);
        });
        JumpBox.PreviewKeyDown += OnJumpKeyDown;
        JumpBox.TextChanged += (_, args) =>
        {
            if (jumpOpen && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                RenderJumpSuggestions();
        };
        JumpBox.QuerySubmitted += async (_, args) =>
        {
            var row = args.ChosenSuggestion as JumpSuggestion ?? jumpSuggestions.ElementAtOrDefault(jumpIndex);
            if (row?.Target is { Enabled: true } target)
                await OpenJumpTargetAsync(target);
            else if (jumpOpen)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (jumpOpen)
                        JumpBox.IsSuggestionListOpen = true;
                });
        };
    }

    private async void OpenJumpTo()
    {
        if (jumpOpen || !JumpBox.IsEnabled || !initialized)
            return;
        jumpOpen = true;
        int generation = ++jumpGeneration;
        jumpSettings = ContentFrame.Content is ConfigsPage { ShowsLoadout: true };
        JumpBox.PlaceholderText = jumpSettings ? SettingJumpPlaceholder : AppJumpPlaceholder;
        JumpShortcutHint.Visibility = Visibility.Collapsed;
        RenderJumpSuggestions();
        if (!jumpSettings)
        {
            await ShowProblemAsync(async () =>
            {
                var profiles = await session.ReadLoadoutChoicesAsync();
                if (jumpOpen && generation == jumpGeneration)
                {
                    jumpLoadouts = profiles;
                    if (JumpBox.Text.Length != 0)
                        RenderJumpSuggestions();
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
        JumpBox.IsSuggestionListOpen = false;
        JumpBox.Text = "";
        JumpBox.ItemsSource = null;
        JumpBox.PlaceholderText = IdleJumpPlaceholder;
        JumpShortcutHint.Visibility = Visibility.Visible;
        jumpSuggestions = [];
        jumpIndex = -1;
        var previous = jumpReturnFocus;
        jumpReturnFocus = null;
        if (restoreFocus)
        {
            if (previous is not { IsLoaded: true } || !previous.Focus(FocusState.Keyboard))
                (ContentFrame.Content as Page)?.Focus(FocusState.Programmatic);
        }
    }

    private void RenderJumpSuggestions()
    {
        var configs = ContentFrame.Content as ConfigsPage;
        var entries = jumpSettings && configs is not null ? configs.JumpSettings() :
            string.IsNullOrWhiteSpace(JumpBox.Text) ? JumpActions() : AppJumpTargets();
        var results = JumpIndex.Search(entries, JumpBox.Text, jumpSettings);
        Brush? selected = IsHighContrast ? null : ThemeBrush("ControlFillBrush", Root.ActualTheme, false);
        jumpSuggestions = results.Select((target, index) => new JumpSuggestion(target,
            !jumpSettings && (index == 0 || results[index - 1].Group != target.Group) ? GroupName(target.Group) : "",
            jumpSettings && index == results.Length - 1, selected)).ToArray();
        if (jumpSuggestions.Length == 0)
        {
            string message = jumpSettings
                ? "No setting matches. Try a word like “jump”, “turn” or “sound”."
                : $"Nothing matches “{JumpBox.Text.Trim()}”. Try a server, map, loadout or setting name.";
            jumpSuggestions = [new JumpSuggestion(message, jumpSettings)];
        }
        JumpBox.ItemsSource = jumpSuggestions;
        JumpBox.IsSuggestionListOpen = true;
        SelectSuggestion(Array.FindIndex(jumpSuggestions, row => row.Target?.Enabled == true), scroll: false);
    }

    private void SelectSuggestion(int index, bool scroll)
    {
        var rows = jumpSuggestions;
        if (rows.ElementAtOrDefault(jumpIndex) is { } old)
            old.IsSelected = false;
        jumpIndex = index;
        if (rows.ElementAtOrDefault(index) is not { } row)
            return;
        row.IsSelected = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!jumpOpen || jumpIndex != index || !ReferenceEquals(jumpSuggestions, rows))
                return;
            var list = VisualTreeHelper.GetOpenPopupsForXamlRoot(DialogRoot).Select(pop => FindSuggestionList(pop.Child))
                .FirstOrDefault(candidate => candidate is not null);
            if (list is null)
                return;
            if (list.SelectionMode != ListViewSelectionMode.Single)
                list.SelectionMode = ListViewSelectionMode.Single;
            list.SelectedIndex = index;
            if (scroll)
                list.ScrollIntoView(row);
        });
    }

    private ListView? FindSuggestionList(DependencyObject? node)
    {
        if (node is ListView list && list.Items.Count > 0 && ReferenceEquals(list.Items[0], jumpSuggestions.FirstOrDefault()))
            return list;
        if (node is null)
            return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindSuggestionList(VisualTreeHelper.GetChild(node, i)) is { } child)
                return child;
        return null;
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
                int step = args.Key == VirtualKey.Up ? -1 : 1;
                for (int i = jumpIndex + step; i >= 0 && i < jumpSuggestions.Length; i += step)
                {
                    if (jumpSuggestions[i].Target?.Enabled != true)
                        continue;
                    SelectSuggestion(i, scroll: true);
                    break;
                }
                break;
            case VirtualKey.Enter when !ControlKeyDown():
                args.Handled = true;
                if (jumpSuggestions.ElementAtOrDefault(jumpIndex)?.Target is { Enabled: true } target)
                    await OpenJumpTargetAsync(target);
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

    private async Task OpenJumpTargetAsync(JumpTarget target)
    {
        CloseJumpTo(restoreFocus: true);
        if (target.Group == JumpGroup.Actions && Enum.TryParse<ShellCommand>(target.Id, out var command))
        {
            await RunShellCommandAsync(command);
            return;
        }
        await RunAsync(async () =>
        {
            switch (target.Group)
            {
                case JumpGroup.Servers:
                    await NavigateAsync(LauncherPlace.Servers, ServerScope.All, target.Id, refreshServers: false);
                    ((ServersPage)ContentFrame.Content).RevealJumpServer(target.Id);
                    break;
                case JumpGroup.Loadouts:
                    await WithConfigsAsync(configs => { configs.OpenJumpLoadout(target.Id); return Task.CompletedTask; });
                    break;
                case JumpGroup.Settings:
                    await OpenSettingsPaneAsync(target.Id);
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
