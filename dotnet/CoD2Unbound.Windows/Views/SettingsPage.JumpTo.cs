using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SettingsPage
{
    private static readonly (string Pane, string[] Rows)[] JumpPanes =
    [
        ("General", ["Game version", "Minimize CoD2 Unbound while the game is running", "Appearance",
            "Refresh the server list when Servers opens", "Loadout"]),
        ("Display", ["Monitor", "Mode", "Graphics", "Resolution", "Restore defaults"]),
        ("Game Data", ["Game data folder", "CD key", "Loadouts, demos and screenshots", "Game versions installed", "Set up again"]),
        ("Privacy", [PrivacyPresentation.CrashSwitch, PrivacyPresentation.Cod2xSwitch, PrivacyPresentation.UsageSwitch,
            "Install number", "Match summaries", "Saved match summaries"]),
        ("Updates", ["Check for updates automatically", "Check now", "What’s new", "Install and relaunch"]),
        ("Advanced", ["Launch options", "Logs", "Warnings", "Launcher settings"]),
        ("About", ["Release notes", "Support", "Help", "Acknowledgements"])
    ];

    internal static IEnumerable<JumpTarget> JumpTargets => JumpPanes.SelectMany(group =>
        group.Rows.Prepend(group.Pane).Select(title => new JumpTarget(JumpGroup.Settings, group.Pane, title, $"Settings › {group.Pane}")));

    internal void OpenJumpPane(string name)
    {
        if (Panes.Items.FirstOrDefault(item => string.Equals(item.Text, name, StringComparison.OrdinalIgnoreCase)) is { } pane)
            Panes.SelectedItem = pane;
    }

    internal void StepPane(int offset)
    {
        var pane = Panes.Items[(Panes.Items.IndexOf(Panes.SelectedItem) + offset + Panes.Items.Count) % Panes.Items.Count];
        Panes.SelectedItem = pane;
        if (textSettings.TextScaleFactor >= 2)
        {
            pane.Focus(FocusState.Keyboard);
            pane.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    internal Task ReportProblemFromShellAsync() =>
        shell.ShowProblemReportAsync();
}
