using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HostingPage
{
    private const string ExportTitle = "Export for Another PC…";
    private const string RevealFolderTitle = "Show Its Folder in File Explorer";
    private const string DeleteTitle = "Delete Server…";
    private readonly Button exportButton;
    private readonly InfoBar exportStatus;
    private (ShellCommand Command, Func<Task> Action)? exportFeedback;

    private HostedServer? SelectedSaved => form is { IsNew: true } ? null : Selected;

    internal bool CanRunCommand(ShellCommand command) => command switch
    {
        ShellCommand.ExportHostedServer => form is not { IsNew: true },
        ShellCommand.RevealHostedServer => SelectedSaved is { } server && hosting.HasServerFolder(server.Id),
        ShellCommand.DeleteHostedServer => SelectedSaved is { } server && !hosting.IsActive(server.Id),
        ShellCommand.RevealHostedExport or ShellCommand.RetryHostedExport =>
            exportStatus.IsOpen && exportFeedback?.Command == command,
        _ => false
    };

    internal Task RunCommandAsync(ShellCommand command) => command switch
    {
        ShellCommand.ExportHostedServer when CanRunCommand(command) => ExportSelectedAsync(),
        ShellCommand.RevealHostedServer when SelectedSaved is { } server => hosting.RevealServerFolderAsync(server.Id),
        ShellCommand.DeleteHostedServer when SelectedSaved is { } server => DeleteServerAsync(server),
        ShellCommand.RevealHostedExport or ShellCommand.RetryHostedExport when CanRunCommand(command) => RunFeedbackActionAsync(),
        _ => Task.CompletedTask
    };

    private void AttachRowMenu(ListViewItem row)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (row.Content is not HostedServer server)
                return;
            menu.Items.Add(Item(ExportTitle, "X", () => ExportSavedAsync(server.Id), automationName: "Export for another machine…"));
            menu.Items.Add(Item(RevealFolderTitle, "F", () => hosting.RevealServerFolderAsync(server.Id),
                hosting.HasServerFolder(server.Id), "Show its folder in File Explorer"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item(DeleteTitle, "D", () => DeleteServerAsync(server), !hosting.IsActive(server.Id)));
        };
        row.ContextFlyout = menu;

        MenuFlyoutItem Item(string title, string accessKey, Func<Task> action, bool enabled = true, string? automationName = null)
        {
            var item = new MenuFlyoutItem { Text = title, AccessKey = accessKey, IsEnabled = enabled };
            AutomationProperties.SetName(item, automationName ?? title);
            item.Click += async (_, _) => await shell.RunAsync(action);
            return item;
        }
    }

    private async Task DeleteServerAsync(HostedServer server)
    {
        if (hosting.IsActive(server.Id))
            return;
        string message = hosting.HasServerFolder(server.Id)
            ? "Its settings are removed. Its folder, with the log, moves to the Recycle Bin."
            : "This removes the server from Hosting.";
        var dialog = new SafeRemovalDialog($"Delete “{server.Name}”?", message, "Delete")
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        await hosting.DeleteAsync(server.Id, WinRT.Interop.WindowNative.GetWindowHandle(shell));
        await RefreshAsync();
    }

    private Task ExportSelectedAsync() => SelectedSaved is { } server
        ? ExportSavedAsync(server.Id)
        : ExportAsync(HostedServer.New(edition: session.Settings.Working.Engine), running: false);

    private async Task ExportSavedAsync(string id)
    {
        var saved = await hosting.ReadSavedAsync(id);
        await ExportAsync(saved, hosting.IsActive(id));
    }

    private async Task ExportAsync(HostedServer server, bool running)
    {
        var passwords = ExportToggle("Passwords", isOn: false);
        var match = ExportToggle("zPAM match settings", isOn: true);
        string edition = server.Engine == GameEdition.Cod2x ? "CoD2x" : "the original 1.3";
        const string explanation = "Writes a folder you can copy to a Linux or Windows server. Nothing is uploaded.";
        var rows = new List<FrameworkElement>
        {
            FormRow("server.cfg", Caption("Name, rules, maps and limits, with a comment on every line."), ReadyLabel("Included")),
            FormRow("Start commands", Caption($"One for Linux, one for Windows, for {edition}."), ReadyLabel("Included"))
        };
        if (server.Rules == HostedRules.Zpam)
            rows.Add(FormRow("zPAM match settings",
                Caption("The match mode and its rules. zPAM’s files are not copied; the read-me links the release to download."), match));
        var passwordNote = Caption("");
        rows.Add(FormRow("Passwords", passwordNote, passwords));
        passwords.Toggled += (_, _) => UpdatePasswordNote();
        UpdatePasswordNote();
        var content = new StackPanel { Spacing = 8, Children = { Card(GroupRows([.. rows])) } };
        if (running)
            content.Children.Add(Caption("The server keeps running; the export uses its saved settings."));
        content.Children.Add(Caption("Game files are never included."));
        var dialog = Dialog($"Export “{server.Name}” for another PC", content, "Export…")
            .WithSymbol("FluentDocument", explanation);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await WriteExportAsync(server, passwords.IsOn, match.IsOn);

        void UpdatePasswordNote()
        {
            passwordNote.Text = passwords.IsOn ? "On, the file holds your passwords. Share it with care."
                : "Off, the file says CHANGE_ME where each password goes.";
            AutomationProperties.SetHelpText(passwords, passwordNote.Text);
        }
    }

    private static ToggleSwitch ExportToggle(string name, bool isOn)
    {
        var toggle = new ToggleSwitch { IsOn = isOn, OffContent = "Off", OnContent = "On", MinWidth = 0 };
        AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    private async Task WriteExportAsync(HostedServer server, bool passwords, bool matchSettings)
    {
        if (await shell.PickFolderAsync("Choose where to write the server files") is not { } parent)
            return;
        string path;
        try { path = await hosting.ExportAsync(server, parent, passwords, matchSettings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowExportStatus(InfoBarSeverity.Error,
                "The export couldn’t be written there. Choose a folder you can write to and try again.",
                "Try again", "Y", ShellCommand.RetryHostedExport, () => WriteExportAsync(server, passwords, matchSettings));
            return;
        }
        ShowExportStatus(InfoBarSeverity.Success, $"Exported to {path}.", "Show in File Explorer", "B",
            ShellCommand.RevealHostedExport, () => WindowsHosting.RevealFolderAsync(path));
    }

    private void ShowExportStatus(InfoBarSeverity severity, string message, string title, string key,
        ShellCommand command, Func<Task> action)
    {
        exportFeedback = (command, action);
        exportStatus.Severity = severity;
        exportStatus.Message = message;
        var button = exportStatus.ActionButton as Button ?? Action(title, RunFeedbackActionAsync);
        button.Content = title;
        button.AccessKey = key;
        exportStatus.ActionButton = button;
        exportStatus.Visibility = Visibility.Visible;
        exportStatus.IsOpen = true;
    }

    private Task RunFeedbackActionAsync() => exportFeedback?.Action() ?? Task.CompletedTask;
}
