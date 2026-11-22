using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal static class PrivacyPresentation
{
    internal const string CrashTitle = "Send crash reports?";
    internal const string CrashDetail = "If the game crashes, CoD2 Unbound can send this project a report of what went wrong. It is how problems on PCs like yours get found and fixed. The choice is yours.";
    internal const string UsageTitle = "Send usage data?";
    internal const string UsageDetail = "CoD2 Unbound can send the project a record of how the app is used: which screens and features, never who you are. It shows what to fix first, and lets Player Setups show the setups players use most. The choice is yours.";
    internal const string ChangeLater = "Either answer can be changed any time in Settings › Privacy.";
    internal const string CrashSwitch = "Send crash reports to CoD2 Unbound";
    internal const string UsageSwitch = "Send usage data to CoD2 Unbound";
    internal const string Never = "Your name, your CD key, server names and addresses, chat, what is in your configs, file paths, or anything you type.";
    internal const string NumberDetail = "Made at random on this PC, it links your usage data from one session to the next. Reset starts a new one; turning usage data off forgets it.";

    private static readonly (string, string)[] CrashRows =
    [
        ("What a report holds", "What failed, the app’s version, your Windows version, your PC’s model and the report the game wrote when it stopped."),
        ("What it never holds", "No name, no account and no CD key. Your Windows user name and every command line are taken out first, and where the report came from is not recorded."),
        ("Where it goes", "To this project’s report service, Sentry, hosted in the EU. Nothing is sent unless the game crashes, and each report is sent once.")
    ];

    private static readonly (string, string)[] UsageRows =
    [
        ("What is sent", "Which screens you open and which features you use, such as setup steps, games started and how they ended, joining servers, player setups viewed or imported, and settings you change, by name. With the app’s version, your Windows version, your PC’s model and processor, your display, your system language and the country you connect from."),
        ("What never is", Never + " Your network address is used once to work out your country, then discarded; nothing finer than the country is kept."),
        ("Where it goes", "To PostHog, a service hosted in the EU, where only this project can see it. A random install number made on this PC links your sessions; turning this off forgets it.")
    ];

    internal static Border Question(bool crash)
    {
        var rows = new StackPanel { Spacing = 13 };
        foreach (var (title, detail) in crash ? CrashRows : UsageRows)
            rows.Children.Add(TextRow(title, detail, 150));
        return new Border { Style = StyleFor("FieldCard"), Child = rows };
    }

    internal static Button PreviewButton(bool crash, string key, MainWindow shell, WindowsReports reports)
    {
        var button = new Button { Style = StyleFor("SubtleLinkAction"), Content = "See what’s sent…", AccessKey = key };
        AutomationProperties.SetName(button, crash ? "See what’s sent in a crash report" : "See what’s sent in usage data");
        ToolTipService.SetToolTip(button, crash ? "What a crash report holds" : "What usage data holds, and one event exactly as it is sent");
        if (crash)
            button.Click += (_, _) => CrashFlyout(reports).ShowAt(button);
        else
            button.Click += async (_, _) => await ShowUsageAsync(shell, reports);
        return button;
    }

    private static Flyout CrashFlyout(WindowsReports reports)
    {
        var info = reports.Facts.Crash();
        var panel = new StackPanel { Width = 388, Spacing = 7 };
        panel.Children.Add(Heading("What a crash report holds"));
        foreach (var (title, detail) in new (string, string)[]
        {
            ("What failed", "The report’s first reason line, such as the error that stopped the game."),
            ("When", "The time the report was written."),
            ("CoD2 Unbound", info.Release),
            ("Game version", "CoD2x or Original 1.3, whichever crashed."),
            ("Windows", info.OsVersion),
            ("PC model", info.Model),
            ("The report", "The crash report the game wrote, with your Windows user name and every command line removed.")
        })
        {
            var row = TextRow(title, detail, 105);
            if (title == "The report")
            {
                string envelope = reports.CrashPreview();
                AutomationProperties.SetHelpText(row.Children[1], envelope);
                ToolTipService.SetToolTip(row, new ToolTip { Content = Caption(envelope), MaxWidth = 410 });
            }
            panel.Children.Add(row);
        }
        panel.Children.Add(Caption("No account, name or CD key goes with it, and a report that seems to hold a CD key is never sent. The report service is told not to record where it came from. Each report is sent once."));
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenter.Setters.Add(new Setter(Control.IsTabStopProperty, true));
        return new Flyout
        {
            Content = panel, Placement = FlyoutPlacementMode.Top,
            SystemBackdrop = new DesktopAcrylicBackdrop(), FlyoutPresenterStyle = presenter
        };
    }

    private static async Task ShowUsageAsync(MainWindow shell, WindowsReports reports)
    {
        if (reports.Usage.Preview() is not { } payload)
            return;
        var content = new StackPanel { Spacing = 14, Padding = new Thickness(0, 0, 12, 4) };
        content.Children.Add(Heading("What is sent"));
        content.Children.Add(Body("What the launcher records, and one real example as it would leave this PC."));
        foreach (var (title, detail) in new (string, string)[]
        {
            ("Screens and features", "Which screens you open and what you use there: setup steps, games started and how they ended, joining a server and how, player setups viewed, tried or imported, loadouts made, checks run, hosting, updates."),
            ("Settings", "Which settings you change, by the setting’s name. Not what you type into a field."),
            ("This PC, your country", "The app’s version, your Windows version, your PC’s model and processor, your display’s size and refresh rate, your system language, and the country your connection comes from (worked out from your network address, which is then discarded)."),
            ("Problems", "Errors the launcher shows you, by kind, such as “the game couldn’t start”."),
            ("Never", Never)
        })
            content.Children.Add(TextRow(title, detail, 132));

        var copy = new Button { Style = StyleFor("SmallSubtleAction"), Content = "Copy", AccessKey = "C" };
        AutomationProperties.SetName(copy, "Copy the example event");
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(payload);
            Clipboard.SetContent(package);
            copy.Content = "Copied";
            AutomationProperties.SetName(copy, "Copied");
            FrameworkElementAutomationPeer.FromElement(copy)?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, "Copied", "UsagePreviewCopy");
        };
        var caption = Columns(8, new(1, GridUnitType.Star), GridLength.Auto);
        Put(caption, 0, Heading("Example: you joined a server from the list"));
        Put(caption, 1, copy);
        content.Children.Add(caption);
        var sample = MonoCaption(payload);
        sample.TextWrapping = TextWrapping.Wrap;
        sample.LineHeight = 18;
        sample.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        sample.IsTextSelectionEnabled = true;
        AutomationProperties.SetName(sample, "The exact text of one event");
        content.Children.Add(new Border { Style = StyleFor("FieldCard"), Padding = new Thickness(12), Child = sample });
        content.Children.Add(Caption("The install number is made at random on this PC. It links your sessions together, not to you, and is forgotten when you turn usage data off. The server’s name and address are not in it."));

        var scroller = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(160, shell.DialogRoot.Size.Height - 170), IsTabStop = true
        };
        AutomationProperties.SetName(scroller, "What is sent");
        var dialog = new ContentDialog
        {
            XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme,
            Content = scroller, PrimaryButtonText = "Done", DefaultButton = ContentDialogButton.Primary
        };
        AutomationProperties.SetName(dialog, "What is sent");
        dialog.Opened += (_, _) =>
        {
            scroller.Focus(FocusState.Programmatic);
            scroller.ChangeView(null, 0, null, disableAnimation: true);
        };
        await dialog.ShowAsync();
    }

    internal static Grid TextRow(string title, string detail, double titleWidth)
    {
        var row = Columns(12, new(titleWidth), new(1, GridUnitType.Star));
        Put(row, 0, Heading(title));
        Put(row, 1, Body(detail));
        return row;
    }

    private static TextBlock Heading(string title) => new()
    {
        Text = title, Style = StyleFor("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap
    };
}
