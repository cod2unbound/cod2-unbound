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
    private readonly ToggleSwitch game;
    private readonly ToggleSwitch server;

    internal ProblemReportDialog(ProblemReport report, bool hasGame, bool hasServer)
    {
        DefaultStyleKey = typeof(ContentDialog);
        Title = "Report a problem";
        this.WithSymbol("SystemBug", "Opens a new issue on GitHub in your browser, filled in with the details below. You can edit everything before you post it.");
        Style = StyleFor("LauncherSheet");
        PrimaryButtonText = "Open GitHub";
        SecondaryButtonText = "Copy Details";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 520d;
        Resources["ContentDialogMinWidth"] = 520d;

        var detail = new TextBlock
        {
            Text = report.Details, Style = StyleFor("Mono"), FontSize = 12, LineHeight = 18,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
        };
        AutomationProperties.SetName(detail, "Details that go in the report");
        var well = new Border
        {
            Style = StyleFor("FieldCard"), Child = detail, Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = (CornerRadius)Application.Current.Resources["CrateRadius"]
        };

        game = new ToggleSwitch { IsEnabled = hasGame, MinWidth = 0 };
        server = new ToggleSwitch { IsEnabled = hasServer, MinWidth = 0 };
        var choices = new StackPanel();
        choices.Children.Add(LogChoice(game, "Include the last game log", hasGame
            ? "Copied to the clipboard for you to paste. It can contain server names."
            : "There’s no game log yet. The game writes one when it crashes or freezes."));
        if (hasServer)
        {
            choices.Children.Add(new Border { Height = 1, Style = StyleFor("CardDivider") });
            choices.Children.Add(LogChoice(server, "Include the last server log",
                "From a server you hosted. Copied to the clipboard for you to paste. It can contain player names."));
        }
        var note = Caption("Never includes your CD key or your name.");
        note.Margin = new Thickness(6, 0, 0, 0);
        Content = new StackPanel
        {
            Spacing = 12,
            Children = { well, new Border { Style = StyleFor("GroupCard"), Child = choices }, note }
        };

        var copyStyle = new Style(typeof(Button)) { BasedOn = StyleFor("SheetButton") };
        copyStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, "Copy details"));
        SecondaryButtonStyle = copyStyle;

        int copyGeneration = 0;
        SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            int attempt = ++copyGeneration;
            SecondaryButtonStyle = StyleFor("SheetButton");
            try
            {
                CopyText(report.Details);
                SecondaryButtonText = "Copied";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                SecondaryButtonText = "Couldn’t Copy";
            }
            await Task.Delay(1800);
            if (attempt == copyGeneration)
            {
                SecondaryButtonText = "Copy Details";
                SecondaryButtonStyle = copyStyle;
            }
        };
    }

    internal bool IncludeGame => game.IsOn;
    internal bool IncludeServer => server.IsOn;

    private static Grid LogChoice(ToggleSwitch toggle, string label, string hint)
    {
        AutomationProperties.SetName(toggle, label);
        AutomationProperties.SetHelpText(toggle, hint);
        var row = Columns(14, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        row.Style = StyleFor("FormRow");
        row.Padding = new Thickness(16, 12, 16, 12);
        row.Children.Add(new StackPanel
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Caption(hint) }
        });
        toggle.VerticalAlignment = VerticalAlignment.Center;
        Put(row, 1, toggle);
        return row;
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
