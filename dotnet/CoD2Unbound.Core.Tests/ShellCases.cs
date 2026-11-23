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
    }
}
