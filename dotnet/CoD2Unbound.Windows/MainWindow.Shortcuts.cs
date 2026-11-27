using CoD2Unbound.Core;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using static CoD2Unbound.Windows.Views.PagePresentation;
using Windows.System;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    internal const string HelpUrl = "https://cod2unbound.com/help";
    private bool closingHomeSheet;
    private (HomePage Page, ShellCommand Command)? homeShortcutAfterSheet;

    private void InitializeShellShortcuts()
    {
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        foreach (var shortcut in LauncherShortcuts.All.Where(item => HasShellAccelerator(item.Command)))
            AddShellAccelerators(Root, shortcut);
        Root.GotFocus += OnHomeButtonFocused;
    }

    private void OnHomeButtonFocused(object sender, RoutedEventArgs args)
    {
        if (ContentFrame.Content is not HomePage || args.OriginalSource is not Button button)
            return;

        // A dismissed sheet restores focus to its opener. WinUI checks accelerators
        // on the focused button before the button can consume Enter as a click.
        button.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        foreach (var shortcut in LauncherShortcuts.All.Where(item => item.Command is ShellCommand.Launch or ShellCommand.OpenGameMenu))
            AddShellAccelerators(button, shortcut, scopeToOwner: true);
    }

    private void AddShellAccelerators(UIElement owner, LauncherShortcut shortcut, bool scopeToOwner = false)
    {
        foreach (string chord in shortcut.Chords)
        {
            var key = chord.Split('+')[^1] switch
            {
                "," => (VirtualKey)0xBC, // VK_OEM_COMMA
                "/" => (VirtualKey)0xBF, // VK_OEM_2
                "1" => VirtualKey.Number1, "2" => VirtualKey.Number2,
                "3" => VirtualKey.Number3, "4" => VirtualKey.Number4,
                var name => Enum.Parse<VirtualKey>(name)
            };
            var modifiers = VirtualKeyModifiers.None;
            if (chord.Contains("Ctrl+", StringComparison.Ordinal))
                modifiers |= VirtualKeyModifiers.Control;
            if (chord.Contains("Shift+", StringComparison.Ordinal))
                modifiers |= VirtualKeyModifiers.Shift;
            if (scopeToOwner && owner.KeyboardAccelerators.Any(item => item.Key == key && item.Modifiers == modifiers))
                continue;

            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            if (scopeToOwner)
                accelerator.ScopeOwner = owner;
            accelerator.Invoked += async (sender, args) =>
            {
                if (shortcut.Command == ShellCommand.FullScreen)
                    OnFullScreen(sender, args);
                else if (shortcut.Command == ShellCommand.Find)
                    args.Handled = FocusPageSearch();
                else if (DeferHomeShortcut(shortcut.Command))
                    args.Handled = true;
                else if (scopeToOwner || CanRunShellCommand(shortcut.Command))
                {
                    args.Handled = true;
                    await RunShellCommandAsync(shortcut.Command);
                }
            };
            owner.KeyboardAccelerators.Add(accelerator);
        }
    }

    internal async Task<ContentDialogResult> ShowShellSheetAsync(ContentDialog sheet)
    {
        if (!busy || ContentFrame.Content is not HomePage)
            return await sheet.ShowSheetAsync();

        closingHomeSheet = false;
        homeShortcutAfterSheet = null;
        sheet.Closing += OnClosing;
        try { return await sheet.ShowSheetAsync(); }
        finally { sheet.Closing -= OnClosing; }

        void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (!args.Cancel && args.Result == ContentDialogResult.None)
                closingHomeSheet = true;
        }
    }

    private bool DeferHomeShortcut(ShellCommand command)
    {
        // WinUI can restore Home focus before the sheet's owning action releases busy.
        if (!busy || !closingHomeSheet || ContentFrame.Content is not HomePage home)
            return false;
        homeShortcutAfterSheet ??= (home, command);
        return true;
    }

    private Task ResumeHomeShortcutAsync()
    {
        var pending = homeShortcutAfterSheet;
        closingHomeSheet = false;
        homeShortcutAfterSheet = null;
        return pending is { } shortcut && ReferenceEquals(shortcut.Page, ContentFrame.Content)
            ? RunShellCommandAsync(shortcut.Command) : Task.CompletedTask;
    }

    private static bool HasShellAccelerator(ShellCommand command) => command is
        ShellCommand.Launch or ShellCommand.OpenGameMenu or ShellCommand.Home or ShellCommand.Servers or
        ShellCommand.Library or ShellCommand.Configs or ShellCommand.Settings or ShellCommand.Jump or ShellCommand.Find or
        ShellCommand.RefreshServers or ShellCommand.Connect or ShellCommand.Favorite or ShellCommand.CopyAddress or
        ShellCommand.FilterServers or ShellCommand.Import or ShellCommand.Paste or ShellCommand.NewLoadout or
        ShellCommand.FullScreen or ShellCommand.Help or ShellCommand.ShortcutHelp or ShellCommand.ToggleSidebar;

    private GameServer? LastJumpServer => session.Library.Recent.FirstOrDefault() is { } address
        ? session.ServerForAddress(address) : null;

    private bool CanRunShellCommand(ShellCommand command)
    {
        if (command == ShellCommand.Help)
            return initialized && jumpEnabled && !busy && !closing;
        if (!initialized || !jumpEnabled || closing)
            return false;
        if (busy && command is not (ShellCommand.Jump or ShellCommand.ReturnToGame) &&
            (command != ShellCommand.RefreshServers || ContentFrame.Content is not ServersPage { IsRefreshing: true }))
            return false;
        var configs = ContentFrame.Content as ConfigsPage;
        var servers = ContentFrame.Content as ServersPage;
        return command switch
        {
            ShellCommand.Launch or ShellCommand.OpenGameMenu => !session.Game.State.Running && session.Setup.DataPath.Length != 0,
            ShellCommand.ReturnToGame => session.Game.State.Running,
            ShellCommand.ToggleSidebar => Nav.IsPaneVisible && Nav.IsPaneToggleButtonVisible,
            ShellCommand.CloseGame => session.Game.State is { Running: true, Quitting: false },
            ShellCommand.Import or ShellCommand.Paste => !session.Game.State.Running && (configs?.CanImportFromShell ?? true),
            ShellCommand.Favorite or ShellCommand.CopyAddress => servers is { CanRunRowShortcut: true },
            ShellCommand.FilterServers => servers is { ShowsServerBrowser: true },
            ShellCommand.NewLoadout => configs is { CanCreateFromShell: true },
            ShellCommand.History or ShellCommand.Checkup => configs?.HasJumpLoadouts ?? jumpLoadouts.Count != 0,
            ShellCommand.GameDataFolder => Directory.Exists(session.Setup.DataPath),
            ShellCommand.CheckUpdates => session.CanCheckUpdates && updateRequest is null,
            ShellCommand.ClearRecent => !session.Library.Recent.IsEmpty,
            ShellCommand.ExportHostedServer or ShellCommand.RevealHostedServer or ShellCommand.DeleteHostedServer or
                ShellCommand.RevealHostedExport or ShellCommand.RetryHostedExport =>
                servers?.ActiveHosting?.CanRunCommand(command) == true,
            _ => true
        };
    }

    private bool FocusPageSearch()
    {
        if (!initialized || busy || closing)
            return false;
        return ContentFrame.Content switch
        {
            ServersPage servers => servers.FocusShellSearch(),
            ConfigsPage configs => configs.FocusShellSearch(),
            LibraryPage library => library.FocusShellSearch(),
            _ => false
        };
    }

    // The Mac's menu-bar commands, in its order and words (JumpPalette.swift actions), plus what Windows adds:
    // returning to and closing the game, and the hosting commands while Hosting is open.
    private IEnumerable<JumpTarget> JumpActions()
    {
        yield return Action(ShellCommand.Connect, "Connect to Address…");
        yield return Action(ShellCommand.RefreshServers, "Refresh Server List");
        yield return Action(ShellCommand.Launch, LastJumpServer is { } server ? $"Launch on {QuakeColors.Plain(server.Name)}" : "Launch");
        yield return Action(ShellCommand.OpenGameMenu, "Open Game Menu");
        foreach (var action in JumpIndex.GameActions(CanRunShellCommand(ShellCommand.ReturnToGame),
                     closingGame: !CanRunShellCommand(ShellCommand.CloseGame)))
            yield return action with { Title = action.Id == nameof(ShellCommand.ReturnToGame) ? "Return to Game" : "Close Game" };
        yield return Action(ShellCommand.Import, "Import Config…");
        yield return Action(ShellCommand.Paste, "Paste Config");
        yield return Action(ShellCommand.Home, "Home");
        yield return Action(ShellCommand.Servers, "Servers");
        yield return Action(ShellCommand.Library, "Library");
        yield return Action(ShellCommand.Configs, "Configs");
        yield return Action(ShellCommand.Settings, "Settings…");
        yield return Action(ShellCommand.Checkup, "Run Check-up");
        yield return Action(ShellCommand.History, "Show History");
        yield return Action(ShellCommand.GameDataFolder, "Show Game Data in File Explorer");
        yield return Action(ShellCommand.MyFilesFolder, "Show My Files in File Explorer");
        yield return Action(ShellCommand.LogsFolder, "Show Logs in File Explorer");
        yield return Action(ShellCommand.CheckUpdates, "Check for Updates…");
        yield return Action(ShellCommand.ShortcutHelp, "Keyboard Shortcuts");
        yield return Action(ShellCommand.ReleaseNotes, "Release Notes");
        yield return Action(ShellCommand.ReportProblem, "Report a Problem…");
        yield return Action(ShellCommand.ClearRecent, "Clear Recent Servers");
        if (ContentFrame.Content is ServersPage { ActiveHosting: not null })
            foreach (var target in JumpIndex.HostingActions(CanRunShellCommand))
                yield return target;

        JumpTarget Action(ShellCommand command, string title) =>
            new(JumpGroup.Actions, command.ToString(), title, Keys: LauncherShortcuts.Keys(command), Enabled: CanRunShellCommand(command));
    }

    private async Task RunShellCommandAsync(ShellCommand command)
    {
        if (!CanRunShellCommand(command))
            return;
        if (command == ShellCommand.ReturnToGame)
        {
            ReturnToGame();
            return;
        }
        if (command == ShellCommand.Jump)
        {
            OpenJumpTo();
            return;
        }
        if (command == ShellCommand.ToggleSidebar)
        {
            Nav.IsPaneOpen = !Nav.IsPaneOpen;
            return;
        }
        if (command == ShellCommand.Settings)
        {
            OpenSettingsWindow(null);
            return;
        }
        if (command == ShellCommand.Help)
        {
            ShowHelpMenu();
            return;
        }
        if (command == ShellCommand.RefreshServers)
        {
            if (ContentFrame.Content is not ServersPage { ShowsServerBrowser: true })
                await RunAsync(() => NavigateAsync(LauncherPlace.Servers, ServerScope.All, refreshServers: false));
            if (ContentFrame.Content is ServersPage servers)
                await servers.RefreshAsync();
            return;
        }

        if (command == ShellCommand.FilterServers && ContentFrame.Content is ServersPage filterPage)
        {
            filterPage.OpenShellFilters();
            return;
        }
        if (command == ShellCommand.Favorite && ContentFrame.Content is ServersPage favorites)
        {
            await favorites.ToggleShellFavoriteAsync();
            return;
        }
        if (command == ShellCommand.CopyAddress && ContentFrame.Content is ServersPage copyPage)
        {
            await copyPage.CopyShellAddressAsync();
            return;
        }

        await RunAsync(async () =>
        {
            switch (command)
            {
                case ShellCommand.Home: await NavigateAsync(LauncherPlace.Home); break;
                case ShellCommand.Servers: await NavigateAsync(LauncherPlace.Servers); break;
                case ShellCommand.Library: await NavigateAsync(LauncherPlace.Library); break;
                case ShellCommand.Configs: await NavigateAsync(LauncherPlace.Configs); break;
                case ShellCommand.Launch:
                case ShellCommand.OpenGameMenu:
                    await LaunchFromShellAsync(useServer: command == ShellCommand.Launch);
                    break;
                case ShellCommand.CloseGame: await CloseActiveGameAsync(); break;
                case ShellCommand.Connect: await ConnectFromShellAsync(); break;
                case ShellCommand.Import:
                case ShellCommand.Paste:
                    await WithConfigsAsync(page => command == ShellCommand.Import ? page.PickConfigAsync() : page.PasteConfigAsync());
                    break;
                case ShellCommand.NewLoadout:
                    await ((ConfigsPage)ContentFrame.Content).CreateFromShellAsync();
                    break;
                case ShellCommand.Checkup:
                    await WithConfigsAsync(page => { page.OpenCheckup(); return Task.CompletedTask; });
                    break;
                case ShellCommand.History:
                    await WithConfigsAsync(page => { page.OpenJumpHistory(); return Task.CompletedTask; });
                    break;
                case ShellCommand.GameDataFolder: await OpenAsync(session.Setup.DataPath); break;
                case ShellCommand.MyFilesFolder: await OpenAsync(session.HomePath); break;
                case ShellCommand.LogsFolder:
                    Directory.CreateDirectory(session.LogsPath);
                    await OpenAsync(session.LogsPath);
                    break;
                case ShellCommand.CheckUpdates:
                    OpenSettingsWindow(SettingsPage.UpdatesPane);
                    await RunUpdateAsync(install: false);
                    break;
                case ShellCommand.ShortcutHelp: await KeyboardShortcutsDialog.ShowAsync(this); break;
                case ShellCommand.ReleaseNotes:
                    await ShowReleaseNotesAsync(session.Updates.State.Offer?.Version ?? session.Version);
                    break;
                case ShellCommand.ReportProblem:
                    await ShowProblemReportAsync();
                    break;
                case ShellCommand.ClearRecent:
                    await ClearRecentFromShellAsync();
                    break;
                case ShellCommand.ExportHostedServer:
                case ShellCommand.RevealHostedServer:
                case ShellCommand.DeleteHostedServer:
                case ShellCommand.RevealHostedExport:
                case ShellCommand.RetryHostedExport:
                    if (ContentFrame.Content is ServersPage { ActiveHosting: { } hosting })
                        await hosting.RunCommandAsync(command);
                    break;
            }
        });
    }

    private async Task WithConfigsAsync(Func<ConfigsPage, Task> action)
    {
        if (ContentFrame.Content is not ConfigsPage)
            await NavigateAsync(LauncherPlace.Configs);
        await action((ConfigsPage)ContentFrame.Content);
    }


    internal Task OpenConnectAsync() => RunAsync(ConnectFromShellAsync);

    private async Task ConnectFromShellAsync()
    {
        if (ContentFrame.Content is not ServersPage { ShowsServerBrowser: true })
            await NavigateAsync(LauncherPlace.Servers, ServerScope.All, refreshServers: false);
        await ((ServersPage)ContentFrame.Content).ConnectFromShellAsync();
    }

    private async Task LaunchFromShellAsync(bool useServer, string? recentAddress = null)
    {
        if ((recentAddress ?? (useServer ? LastJumpServer?.Address : null)) is { } address)
        {
            string? problem = await JoinServerAsync(new(address, null), UsageJoin.List, lookupUnknown: true);
            if (problem is not null && !session.Game.State.Running)
                throw new IOException(problem);
        }
        else if (session.Game.State.Running)
            ShowShellNotice("Call of Duty 2 is already running.", InfoBarSeverity.Informational);
        else
            await RunGameStartAsync(session.Settings.Working.Engine, () => session.LaunchAsync());
        if (ContentFrame.Content is HomePage home)
            await home.RefreshPlayHistoryAsync();
    }

    private async Task ClearRecentFromShellAsync()
    {
        await session.ClearRecentServersAsync();
        if (ContentFrame.Content is HomePage home)
            await home.RefreshPlayHistoryAsync();
        else if (ContentFrame.Content is ServersPage servers)
            servers.UpdateContrast();
    }
}
