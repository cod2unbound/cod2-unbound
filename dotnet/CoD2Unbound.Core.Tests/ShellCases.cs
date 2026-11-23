using CoD2Unbound.Core;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void ShellCases()
    {
        Check("Jump groups and ranks titles, console names and maps", () =>
        {
            JumpTarget[] entries =
            [
                new(JumpGroup.Actions, "item", "Harbor"),
                new(JumpGroup.Servers, "1", "Night server", SearchName: "mp_harbor"),
                new(JumpGroup.Servers, "2", "Harbor"),
                new(JumpGroup.Servers, "3", "Harbor 24/7"),
                new(JumpGroup.Settings, "Display", "Window mode"),
                new(JumpGroup.Loadouts, "main", "Warm up"),
                new(JumpGroup.Bindings, "+attack", "Fire")
            ];
            Sequence(new[] { "2", "3", "1", "item" }, JumpIndex.Search(entries, " harbor ").Select(item => item.Id));
            Sequence(new[] { "item" }, JumpIndex.Search(entries, "").Select(item => item.Id));
            Sequence(new[] { "1" }, JumpIndex.Search(entries, "harbor night").Select(item => item.Id));
            Sequence(new[] { "+attack" }, JumpIndex.Search(entries, "fire").Select(item => item.Id));
            Equal(0, JumpIndex.Search(entries, "missing").Length);
        });
        Check("Jump to a loadout setting searches its title and console name", () =>
        {
            JumpTarget[] entries =
            [
                new(JumpGroup.ConfigSettings, "m_pitch", "Vertical look", Detail: "Aim", SearchName: "m_pitch"),
                new(JumpGroup.ConfigSettings, "m_yaw", "Horizontal look", Detail: "Aim", SearchName: "m_yaw")
            ];
            Equal("m_pitch", JumpIndex.Search(entries, "m_pitch", settingsOnly: true).Single().Id);
            Equal("m_yaw", JumpIndex.Search(entries, "horizontal", settingsOnly: true).Single().Id);
            Equal(0, JumpIndex.Search(entries, "aim", settingsOnly: true).Length);
        });
        Check("Hosting actions are searchable and advertise their contextual shortcuts", () =>
        {
            var actions = JumpIndex.HostingActions(command => command == ShellCommand.ExportHostedServer);
            var export = JumpIndex.Search(actions, "export another machine").Single();
            Equal(ShellCommand.ExportHostedServer.ToString(), export.Id);
            Equal("Alt+X", export.Keys);
            Equal(true, export.Enabled);
            var reveal = JumpIndex.Search(actions, "hosted server folder").Single();
            Equal(ShellCommand.RevealHostedServer.ToString(), reveal.Id);
            Equal(false, reveal.Enabled);
            Equal(true, reveal.Keys.StartsWith("Shift+F10", StringComparison.Ordinal));
            Equal(5, actions.Count());
        });

        Check("Shortcut help and routing share the command and chord list", () =>
        {
            Equal("Ctrl+R or F5", LauncherShortcuts.Keys(ShellCommand.RefreshServers));
            Equal("Ctrl+Shift+Enter", LauncherShortcuts.Keys(ShellCommand.OpenGameMenu));
            Equal("Ctrl+C", LauncherShortcuts.Keys(ShellCommand.CopyFile));
            Equal("Ctrl+Shift+C", LauncherShortcuts.Keys(ShellCommand.CopyAddress));
            var chords = LauncherShortcuts.All.SelectMany(item => item.Chords).ToArray();
            Equal(chords.Length, chords.Distinct().Count());
        });
        Check("Taskbar actions round-trip and recent destinations contain only an address", () =>
        {
            var entries = TaskbarJumpList.Recent(
                Enumerable.Range(1, 7).Select(index => new GameServer($"127.0.0.1:{28960 + index}",
                    System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("hostname", "^1Night sessions"), 0, [])),
                ["--join-recent=127.0.0.1:28961"]);
            Equal(5, entries.Length);
            Equal("Night sessions", entries[0].Title);
            Equal(new TaskbarActivation(ShellCommand.Launch, "127.0.0.1:28962"), TaskbarJumpList.Parse(entries[0].Arguments));
            Sequence(new[] { ShellCommand.Launch, ShellCommand.OpenGameMenu, ShellCommand.Connect },
                TaskbarJumpList.Tasks.Select(task => TaskbarJumpList.Parse(task.Arguments)!.Command));
            Equal(null, TaskbarJumpList.Parse("--join-recent=127.0.0.1 password secret"));
        });
        Check("Jump to lists running game actions and disables close while a close is pending", () =>
        {
            var actions = JumpIndex.GameActions(running: true, closingGame: true);
            Equal(true, actions.Single(action => action.Id == nameof(ShellCommand.ReturnToGame)).Enabled);
            Equal(false, actions.Single(action => action.Id == nameof(ShellCommand.CloseGame)).Enabled);
            Equal(true, JumpIndex.GameActions(running: false, closingGame: false).All(action => !action.Enabled));
            Equal(true, LauncherShortcuts.All.All(shortcut => !shortcut.Chords.IsEmpty));
        });
    }
}
