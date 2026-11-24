using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class ProblemReportDialog : ContentDialog
{
    private readonly CheckBox game;
    private readonly CheckBox server;

    internal ProblemReportDialog(ProblemReport report, bool hasGame, bool hasServer)
    {
        DefaultStyleKey = typeof(ContentDialog);
        Title = "Report a problem";
        Style = StyleFor("LauncherSheet");
        PrimaryButtonText = "Open GitHub";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 600d;
        Resources["ContentDialogMinWidth"] = 600d;

        var detail = new TextBlock { Text = report.Details, Style = StyleFor("Mono"), TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true };
        AutomationProperties.SetName(detail, "Details that go in the report");
        var copy = new Button { VerticalAlignment = VerticalAlignment.Top, Content = IconLabel("Copy", "Copy Details", 16) };
        AddCopyFeedback(copy, "Copy Details", () => Task.FromResult(report.Details));

        var details = Columns(10, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(details, 0, new Border { Style = StyleFor("FieldCard"), Child = detail });
        Put(details, 1, copy);
        game = new CheckBox { Content = "Include the last game log", IsEnabled = hasGame, IsChecked = false };
        server = new CheckBox { Content = "Include the last server log", IsEnabled = hasServer, IsChecked = false };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Body("Opens a new issue on GitHub in your browser, filled in with the details below. You can edit everything before you post it."));
        content.Children.Add(details);
        content.Children.Add(LogChoice(game, hasGame ? "Copied to the clipboard for you to paste. It can contain server names." :
            "There's no game log yet. The game writes one when it crashes or freezes."));
        content.Children.Add(LogChoice(server, hasServer ? "From a server you hosted. Copied to the clipboard for you to paste. It can contain player names." :
            "There's no server log yet. It appears after you host a server."));
        content.Children.Add(Caption("Never includes your CD key or your name."));
        Content = content;
        Opened += (_, _) => copy.Focus(FocusState.Programmatic);
    }

    internal bool IncludeGame => game.IsChecked == true;
    internal bool IncludeServer => server.IsChecked == true;

    private static StackPanel LogChoice(CheckBox box, string hint)
    {
        AutomationProperties.SetHelpText(box, hint);
        var description = Caption(hint);
        description.Margin = new Thickness(28, 0, 0, 0);
        return new StackPanel { Children = { box, description } };
    }

    internal static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    internal static void AddCopyFeedback(Button button, string label, Func<Task<string>> read)
    {
        AutomationProperties.SetName(button, label);
        int generation = 0;
        button.Click += async (_, _) =>
        {
            int attempt = ++generation;
            try
            {
                CopyText(await read());
                button.Content = IconLabel("Check", "Copied", 16);
                AutomationProperties.SetName(button, "Copied");
                if (FrameworkElementAutomationPeer.CreatePeerForElement(button) is { } peer)
                    peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
                        AutomationNotificationProcessing.MostRecent, "Copied", label);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                button.Content = "Couldn't copy";
                AutomationProperties.SetName(button, "Couldn't copy");
            }
            await Task.Delay(1800);
            if (attempt != generation)
                return;
            button.Content = IconLabel("Copy", label, 16);
            AutomationProperties.SetName(button, label);
        };
    }
}
