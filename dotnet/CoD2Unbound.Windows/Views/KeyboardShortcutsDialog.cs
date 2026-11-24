using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class KeyboardShortcutsDialog : ContentDialog
{
    private KeyboardShortcutsDialog(MainWindow shell)
    {
        XamlRoot = shell.DialogRoot;
        RequestedTheme = shell.DialogTheme;
        Resources["ContentDialogMaxWidth"] = 720d;
        Width = 720;
        Title = "Keyboard shortcuts";
        this.WithSymbol("SystemKeyboard", "Every shortcut, in one place: Windows has no menu bar.");
        PrimaryButtonText = "Done";
        DefaultButton = ContentDialogButton.Primary;

        var entries = LauncherShortcuts.All;
        int firstColumn = (entries.Length + 1) / 2;
        var columns = Columns(20, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
        Put(columns, 0, ShortcutColumn(entries.Take(firstColumn)));
        Put(columns, 1, ShortcutColumn(entries.Skip(firstColumn)));
        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new ScrollViewer { MaxHeight = 460, Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            }
        };
    }

    internal static async Task ShowAsync(MainWindow shell) => await new KeyboardShortcutsDialog(shell).ShowAsync();

    private static StackPanel ShortcutColumn(IEnumerable<LauncherShortcut> shortcuts)
    {
        var column = new StackPanel { Spacing = 3 };
        foreach (var shortcut in shortcuts)
        {
            var line = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
            var name = Body(shortcut.Title);
            name.VerticalAlignment = VerticalAlignment.Center;
            Put(line, 0, name);
            if (shortcut.Keys.Length > 0)
                Put(line, 1, new Border
                {
                    Style = StyleFor("KeyCapLabel"),
                    Child = new TextBlock { Text = shortcut.Keys, Style = StyleFor("KeyCapText") }
                });
            AutomationProperties.SetName(line, shortcut.Keys.Length == 0 ? shortcut.Title : $"{shortcut.Title}, {shortcut.Keys}");
            column.Children.Add(line);
        }
        return column;
    }
}
