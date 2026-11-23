using CoD2Unbound.Core;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using static CoD2Unbound.Windows.Views.PagePresentation;
using Windows.System;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private const string HelpUrl = "https://cod2unbound.com/help";

    private void InitializeShellShortcuts()
    {
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        foreach (var shortcut in LauncherShortcuts.All.Where(item => HasShellAccelerator(item.Command)))
        {
            foreach (string chord in shortcut.Chords)
            {
                var key = chord.Split('+')[^1] switch
                {
                    "," => (VirtualKey)0xBC, // VK_OEM_COMMA
                    "1" => VirtualKey.Number1, "2" => VirtualKey.Number2,
                    "3" => VirtualKey.Number3, "4" => VirtualKey.Number4,
                    var name => Enum.Parse<VirtualKey>(name)
                };
                var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.None };
                if (chord.Contains("Ctrl+", StringComparison.Ordinal))
                    accelerator.Modifiers |= VirtualKeyModifiers.Control;
                if (chord.Contains("Shift+", StringComparison.Ordinal))
                    accelerator.Modifiers |= VirtualKeyModifiers.Shift;
                accelerator.Invoked += async (sender, args) =>
                {
                    if (shortcut.Command == ShellCommand.FullScreen)
                        OnFullScreen(sender, args);
                    else if (shortcut.Command == ShellCommand.Find)
                        args.Handled = FocusPageSearch();
                    else if (CanRunShellCommand(shortcut.Command))
                    {
                        args.Handled = true;
                        await RunShellCommandAsync(shortcut.Command);
                    }
                };
                Root.KeyboardAccelerators.Add(accelerator);
            }
        }
    }

    private static bool HasShellAccelerator(ShellCommand command) => command is
        ShellCommand.Launch or ShellCommand.OpenGameMenu or ShellCommand.Home or ShellCommand.Servers or
        ShellCommand.Library or ShellCommand.Configs or ShellCommand.Settings or ShellCommand.Jump or ShellCommand.Find or
        ShellCommand.RefreshServers or ShellCommand.Connect or ShellCommand.Favorite or ShellCommand.CopyAddress or
        ShellCommand.FilterServers or ShellCommand.Import or ShellCommand.Paste or ShellCommand.NewLoadout or
        ShellCommand.FullScreen or ShellCommand.Help;

    private GameServer? LastJumpServer => (ContentFrame.Content as ServersPage)?.JumpSelectedServer ??
        (session.Library.Recent.FirstOrDefault() is { } address ? session.ServerForAddress(address) : null);

    private bool CanRunShellCommand(ShellCommand command)
    {
        if (command == ShellCommand.Help)
            return !closing;
        if (!initialized || !JumpBox.IsEnabled || closing)
            return false;
        if (busy && command != ShellCommand.Jump &&
            (command != ShellCommand.RefreshServers || ContentFrame.Content is not ServersPage { IsRefreshing: true }))
            return false;
        var configs = ContentFrame.Content as ConfigsPage;
        var servers = ContentFrame.Content as ServersPage;
        return command switch
        {
            ShellCommand.Launch or ShellCommand.OpenGameMenu => !session.Game.State.Running && session.Setup.DataPath.Length != 0,
            ShellCommand.Import or ShellCommand.Paste => !session.Game.State.Running && (configs?.CanImportFromShell ?? true),
            ShellCommand.Connect => !session.Game.State.Running,
            ShellCommand.Favorite or ShellCommand.CopyAddress => servers?.JumpSelectedServer is not null,
            ShellCommand.FilterServers => servers is { ShowsServerBrowser: true },
            ShellCommand.NewLoadout => configs is { CanCreateFromShell: true },
            ShellCommand.History or ShellCommand.Checkup => configs?.HasJumpLoadouts ?? jumpLoadouts.Count != 0,
            ShellCommand.GameDataFolder => Directory.Exists(session.Setup.DataPath),
            ShellCommand.CheckUpdates => session.CanCheckUpdates && updateRequest is null,
            ShellCommand.ClearRecent => !session.Library.Recent.IsEmpty,
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

    private IEnumerable<JumpTarget> JumpActions()
    {
        yield return Action(ShellCommand.Connect, "Connect to an address…");
        yield return Action(ShellCommand.RefreshServers, "Refresh server list");
        yield return Action(ShellCommand.Launch, LastJumpServer is { } server ? $"Launch on {QuakeColors.Plain(server.Name)}" : "Launch");
        yield return Action(ShellCommand.OpenGameMenu, "Open game menu");
        yield return Action(ShellCommand.Import, "Import config…");
        yield return Action(ShellCommand.Paste, "Paste config");
        yield return Action(ShellCommand.Home, "Home");
        yield return Action(ShellCommand.Servers, "Servers");
        yield return Action(ShellCommand.Library, "Library");
        yield return Action(ShellCommand.Configs, "Configs");
        yield return Action(ShellCommand.Settings, "Settings");
        yield return Action(ShellCommand.Checkup, "Run Check-up");
        yield return Action(ShellCommand.History, "Show History");
        yield return Action(ShellCommand.GameDataFolder, "Show game data in File Explorer");
        yield return Action(ShellCommand.MyFilesFolder, "Show my files in File Explorer");
        yield return Action(ShellCommand.LogsFolder, "Show logs in File Explorer");
        yield return Action(ShellCommand.CheckUpdates, "Check for updates…");
        yield return Action(ShellCommand.ShortcutHelp, "Keyboard shortcuts");
        yield return Action(ShellCommand.ReleaseNotes, "Release notes");
        yield return Action(ShellCommand.ReportProblem, "Report a problem…");
        yield return Action(ShellCommand.ClearRecent, "Clear recent servers");

        JumpTarget Action(ShellCommand command, string title) =>
            new(JumpGroup.Actions, command.ToString(), title, Keys: LauncherShortcuts.Keys(command), Enabled: CanRunShellCommand(command));
    }

    private async Task RunShellCommandAsync(ShellCommand command)
    {
        if (!CanRunShellCommand(command))
            return;
        if (command == ShellCommand.Jump)
        {
            JumpBox.Focus(FocusState.Keyboard);
            OpenJumpTo();
            return;
        }
        if (command == ShellCommand.Help)
        {
            await ShowProblemAsync(() => OpenAsync(HelpUrl));
            return;
        }
        if (command == ShellCommand.RefreshServers)
        {
            if (ContentFrame.Content is not ServersPage { ShowsServerBrowser: true })
                await RunAsync(() => NavigateAsync(LauncherPlace.Servers, refreshServers: false));
            if (ContentFrame.Content is ServersPage servers)
                await servers.RefreshAsync();
            return;
        }

        // The filter flyout keeps the page available for its Apply action.
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

        await RunAsync(async () =>
        {
            switch (command)
            {
                case ShellCommand.Home: await NavigateAsync(LauncherPlace.Home); break;
                case ShellCommand.Servers: await NavigateAsync(LauncherPlace.Servers); break;
                case ShellCommand.Library: await NavigateAsync(LauncherPlace.Library); break;
                case ShellCommand.Configs: await NavigateAsync(LauncherPlace.Configs); break;
                case ShellCommand.Settings: await NavigateAsync(LauncherPlace.Settings); break;
                case ShellCommand.Launch:
                case ShellCommand.OpenGameMenu:
                    await LaunchFromShellAsync(useServer: command == ShellCommand.Launch);
                    break;
                case ShellCommand.Connect:
                    if (ContentFrame.Content is not ServersPage { ShowsServerBrowser: true })
                        await NavigateAsync(LauncherPlace.Servers, refreshServers: false);
                    await ((ServersPage)ContentFrame.Content).ConnectFromShellAsync();
                    break;
                case ShellCommand.CopyAddress:
                    (ContentFrame.Content as ServersPage)?.CopyShellAddress();
                    break;
                case ShellCommand.Import:
                case ShellCommand.Paste:
                    await WithConfigsAsync(page => command == ShellCommand.Import ? page.ImportFromShellAsync() : page.PasteFromShellAsync());
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
                    await NavigateAsync(LauncherPlace.UpdateSettings);
                    await RunUpdateAsync(install: false);
                    break;
                case ShellCommand.ShortcutHelp: await KeyboardShortcutsDialog.ShowAsync(this); break;
                case ShellCommand.ReleaseNotes:
                    await ShowReleaseNotesAsync(session.Updates.State.Offer?.Version ?? session.Version);
                    break;
                case ShellCommand.ReportProblem:
                    await OpenSettingsPaneAsync("About");
                    await ((SettingsPage)ContentFrame.Content).ReportProblemFromShellAsync();
                    break;
                case ShellCommand.ClearRecent:
                    await ClearRecentFromShellAsync();
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

    private async Task OpenSettingsPaneAsync(string pane)
    {
        if (ContentFrame.Content is not SettingsPage)
            await NavigateAsync(LauncherPlace.Settings);
        ((SettingsPage)ContentFrame.Content).OpenJumpPane(pane);
    }

    private async Task LaunchFromShellAsync(bool useServer)
    {
        if (useServer && LastJumpServer is { } server)
        {
            string? problem = await JoinServerAsync(new(server.Address, null), UsageJoin.List, lookupUnknown: true);
            if (problem is not null && !session.Game.State.Running)
                throw new IOException(problem);
        }
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
