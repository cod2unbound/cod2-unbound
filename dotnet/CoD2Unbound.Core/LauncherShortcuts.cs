using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public enum ShellCommand
{
    Launch, OpenGameMenu, Home, Servers, Library, Configs, Settings, Jump, Find,
    RefreshServers, Connect, Favorite, Import, Paste, Undo, Open, CopyAddress, FilterServers,
    NewLoadout, DuplicateLoadout, ExportLoadout, Rename, Delete, CopyFile, ContextMenu, FullScreen, Help, Close,
    Checkup, History, GameDataFolder, MyFilesFolder, LogsFolder, CheckUpdates, ShortcutHelp, ReleaseNotes,
    ReportProblem, ClearRecent, SaveLoadoutText, ReturnToGame, CloseGame
}

public sealed record LauncherShortcut(ShellCommand Command, string Title, ImmutableArray<string> Chords)
{
    public string Keys => string.Join(" or ", Chords);
}

public static class LauncherShortcuts
{
    public static ImmutableArray<LauncherShortcut> All { get; } =
    [
        new(ShellCommand.Launch, "Launch", ["Ctrl+Enter"]),
        new(ShellCommand.OpenGameMenu, "Open game menu", ["Ctrl+Shift+Enter"]),
        new(ShellCommand.ReturnToGame, "Return to game", []),
        new(ShellCommand.CloseGame, "Close game", []),
        new(ShellCommand.Home, "Home", ["Ctrl+1"]),
        new(ShellCommand.Servers, "Servers", ["Ctrl+2"]),
        new(ShellCommand.Library, "Library", ["Ctrl+3"]),
        new(ShellCommand.Configs, "Configs", ["Ctrl+4"]),
        new(ShellCommand.Settings, "Settings", ["Ctrl+,"]),
        new(ShellCommand.Jump, "Jump to…", ["Ctrl+K"]),
        new(ShellCommand.Find, "Find", ["Ctrl+F"]),
        new(ShellCommand.RefreshServers, "Refresh servers", ["Ctrl+R", "F5"]),
        new(ShellCommand.Connect, "Connect to an address", ["Ctrl+L"]),
        new(ShellCommand.Favorite, "Toggle server favorite", ["Ctrl+D"]),
        new(ShellCommand.Import, "Import config", ["Ctrl+O"]),
        new(ShellCommand.Paste, "Paste config", ["Ctrl+Shift+V"]),
        new(ShellCommand.Undo, "Undo config import", ["Ctrl+Z"]),
        new(ShellCommand.Open, "Launch server or open Library item", ["Enter"]),
        new(ShellCommand.CopyAddress, "Copy server address", ["Ctrl+Shift+C"]),
        new(ShellCommand.FilterServers, "Server filters", ["Ctrl+Shift+F"]),
        new(ShellCommand.NewLoadout, "New loadout", ["Ctrl+N"]),
        new(ShellCommand.DuplicateLoadout, "Duplicate loadout", ["Ctrl+Shift+D"]),
        new(ShellCommand.ExportLoadout, "Export loadout", ["Ctrl+Shift+E"]),
        new(ShellCommand.Rename, "Rename", ["F2"]),
        new(ShellCommand.Delete, "Delete", ["Delete"]),
        new(ShellCommand.CopyFile, "Copy demo or screenshot", ["Ctrl+C"]),
        new(ShellCommand.SaveLoadoutText, "Apply loadout text", ["Ctrl+S"]),
        new(ShellCommand.ContextMenu, "Row context menu", ["Shift+F10", "Menu key"]),
        new(ShellCommand.FullScreen, "Full screen", ["F11"]),
        new(ShellCommand.Help, "Help", ["F1"]),
        new(ShellCommand.Close, "Close", ["Alt+F4"])
    ];

    public static string Keys(ShellCommand command) => All.FirstOrDefault(item => item.Command == command)?.Keys ?? "";
}
