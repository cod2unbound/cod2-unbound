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
    internal const string UsageDetail = "CoD2 Unbound can send the project a record of which screens and features you use, linked by a random install number. It shows what to fix first, and lets Player Setups show the setups players use most. The choice is yours.";
    internal const string ChangeLater = "Either answer can be changed any time in Settings › Privacy.";
    internal const string CrashSwitch = "Send crash reports to CoD2 Unbound";
    internal const string Cod2xSwitch = "Send CoD2x crash reports to the CoD2x project";
    internal const string UsageSwitch = "Send usage data to CoD2 Unbound";
    internal const string Never = "Your name, your CD key, server names and addresses, chat, what is in your configs, file paths, or anything you type.";
    internal const string NumberDetail = "Made at random on this PC, it links your usage data from one session to the next. Reset starts a new one; turning usage data off forgets it.";

    private static readonly (string, string)[] CrashRows =
    [
        ("What a report holds", "What failed and when, which game version crashed, the app’s version, your Windows version, your PC’s model and the report the game wrote when it stopped."),
        ("What it never holds", "No name, no account and no CD key. The crash report leaves out your Windows user name and command lines. The launcher asks Sentry to leave your network address out of the report."),
        ("Where it goes", "To this project’s report service, Sentry. Sending requires crash reporting to be on when the game starts and when it crashes.")
    ];

    private static readonly (string, string)[] UsageRows =
    [
        ("What is sent", "That you opened the launcher, which screens you open and what you use there, such as setup steps, the games you start (game version and graphics settings) and how long they ran, starting the game to join a server, the player setups you view or import, and the settings you change. For some settings, it also sends the choice you pick from a set list. With the time to the minute, the app’s version, your Windows version, your PC’s model and hardware (processor, memory and graphics card), your display and your system language. PostHog can work out your location from your network address."),
        ("What never is", Never + " The launcher leaves your network address out of the events."),
        ("Where it goes", "To this project’s PostHog service, hosted in the EU. A random install number made on this PC links your sessions; turning this off forgets it.")
    ];

    private static readonly (string, string)[] UsagePreviewRows =
    [
        ("Screens", "That the launcher was opened, at most once a day, and which screens you open, such as Servers or Settings › Display."),
        ("Setup", "Each step you reach or skip after you say yes, and whether you picked your game folder yourself."),
        ("Games", "Each game you start, with its game version (CoD2x or Original 1.3), graphics (Direct3D 9 or DirectX 7) and display mode; when it ends, whether it closed normally or unexpectedly, and how many whole minutes it ran."),
        ("Servers", "Refreshing the list, and which list was open: All, Favorites or Recent. Starting the game to join a server, counted before the server accepts you: how you chose it (from the list, a favorite, a link or a typed address) and its game version, never which server."),
        ("Player setups", "The catalogue ID of each player setup you view, try or import: the short public name in its link, such as “harrow” in cod2unbound.com/setups/harrow. That you made a loadout or checked a config, nothing more."),
        ("Hosting", "Starting a server: for your network or the internet, and its game version."),
        ("Updates", "Each update offered, and each step as it downloads, is checked, becomes ready and installs, with the version you had and the new one."),
        ("Settings", "Which settings you change, by name. For some settings with a set list of choices, such as a switch, the game version, appearance, display mode or graphics, also the choice you pick. Never what you type into a field."),
        ("Problems", "Errors the launcher shows you, by kind, such as “the game couldn’t start”."),
        ("This PC, your country", "The app’s version, your Windows version, your PC’s model, its processor (name and number of logical processors), whether the PC and launcher are x64 or Arm, its memory in GB, its graphics card (name, memory in GB and driver version), your display’s size and refresh rate, and your system language. PostHog can work out your country and more specific location from your network address."),
        ("With every event", "The time, to the minute; a random number for the event; and the install number. Each event has a name, identifies the launcher and asks PostHog to skip person profiles. When you say yes, also where: in setup or in Settings."),
        ("Never", "Your name, your PC’s name or serial numbers, your CD key, server names and addresses, chat, what is in your configs, file paths, or anything you type.")
    ];

    internal static Border Question(bool crash)
    {
        var rows = new StackPanel();
        string[] icons = ["Eye", "LockClosed", "Share"];
        var words = crash ? CrashRows : UsageRows;
        for (int i = 0; i < words.Length; i++)
        {
            var (title, detail) = words[i];
            var row = Columns(12, new(20), new(1, GridUnitType.Star));
            var icon = Fluent(icons[i], 20);
            icon.Style = StyleFor("PrivacyQuestionIcon");
            icon.VerticalAlignment = VerticalAlignment.Top;
            Put(row, 0, icon);
            Put(row, 1, new StackPanel { Spacing = 2, Children = { Heading(title), Detail(detail, 13, 19) } });
            AddRow(rows, row);
        }
        return GroupCard(rows);
    }

    internal static Button PreviewButton(bool crash, string key, MainWindow shell, WindowsReports reports)
    {
        var button = new Button { Style = StyleFor("SubtleLinkAction"), Content = "See what’s sent…", AccessKey = key };
        AutomationProperties.SetName(button, crash ? "See what’s sent in a crash report" : "See what’s sent in usage data");
        ToolTipService.SetToolTip(button, crash ? "What a crash report holds" : "What usage data holds, with an example event");
        if (crash)
            button.Click += (_, _) => CrashFlyout(reports).ShowAt(button);
        else
        {
            bool showing = false;
            button.Click += async (_, _) =>
            {
                if (showing)
                    return;
                showing = true;
                try { await ShowUsageAsync(shell, reports); }
                finally { showing = false; }
            };
        }
        return button;
    }

    private static Flyout CrashFlyout(WindowsReports reports)
    {
        var info = reports.Facts.Crash();
        var panel = new StackPanel { Width = 388, Spacing = 7 };
        var heading = Heading("What a crash report holds");
        heading.FontSize = 18;
        heading.LineHeight = 24;
        heading.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        panel.Children.Add(heading);
        foreach (var (title, detail) in new (string, string)[]
        {
            ("What failed", "The report’s first reason line, such as the error that stopped the game."),
            ("When", "The time the report was written, and the time it was sent."),
            ("Report number", "Made from the report’s file name and contents, so the same report keeps its number if sending is retried."),
            ("CoD2 Unbound", info.Release),
            ("Game version", "CoD2x or Original 1.3, whichever crashed."),
            ("Windows", info.OsVersion),
            ("PC model", info.Model),
            ("The report", "The crash report the game wrote, with exception details, stack frames and module versions.")
        })
        {
            var row = Columns(12, new(105), new(1, GridUnitType.Star));
            Put(row, 0, Heading(title));
            Put(row, 1, Body(detail));
            if (title == "The report")
            {
                string envelope = reports.CrashPreview();
                AutomationProperties.SetHelpText(row.Children[1], envelope);
                ToolTipService.SetToolTip(row, new ToolTip { Content = Caption(envelope), MaxWidth = 410 });
            }
            panel.Children.Add(row);
        }
        panel.Children.Add(Caption("No account, name or CD key goes with it, and a report that seems to hold a CD key is never sent. The launcher asks Sentry to leave your network address out of the report and keeps track of reports Sentry has accepted."));
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenter.Setters.Add(new Setter(Control.IsTabStopProperty, true));
        return new Flyout
        {
            Content = panel, Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
            SystemBackdrop = new DesktopAcrylicBackdrop(), FlyoutPresenterStyle = presenter
        };
    }

    private static async Task ShowUsageAsync(MainWindow shell, WindowsReports reports)
    {
        if (await reports.Usage.PreviewAsync() is not { } payload)
            return;
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(Body("What the launcher records, with an example event in the format the launcher sends."));
        var groups = new StackPanel();
        foreach (var (title, detail) in UsagePreviewRows)
        {
            var row = Columns(12, new(132), new(1, GridUnitType.Star));
            Put(row, 0, Heading(title));
            Put(row, 1, Detail(detail, 12, 17));
            AddRow(groups, row);
        }
        content.Children.Add(GroupCard(groups));

        var copyLabel = new TextBlock { Text = "Copy" };
        var copy = new Button { Style = StyleFor("SmallSubtleAction"), AccessKey = "C", IsTabStop = false,
            Content = Buttons(Fluent("Copy", 14), copyLabel) };
        AutomationProperties.SetName(copy, "Copy the example event");
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(payload);
            Clipboard.SetContent(package);
            copyLabel.Text = "Copied";
            AutomationProperties.SetName(copy, "Copied");
            FrameworkElementAutomationPeer.FromElement(copy)?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, "Copied", "UsagePreviewCopy");
        };
        var caption = Columns(8, new(1, GridUnitType.Star), GridLength.Auto);
        var exampleTitle = Heading("Example: you joined a server from the list");
        exampleTitle.VerticalAlignment = VerticalAlignment.Center;
        Put(caption, 0, exampleTitle);
        Put(caption, 1, copy);
        content.Children.Add(caption);
        var sample = MonoCaption(payload);
        sample.TextWrapping = TextWrapping.Wrap;
        sample.LineHeight = 18;
        sample.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        AutomationProperties.SetName(sample, "The exact text of one event");
        content.Children.Add(new Border { Style = StyleFor("FieldCard"), Padding = new Thickness(12), Child = sample });
        content.Children.Add(Caption("The install number is made at random on this PC. It links your sessions together and is forgotten on this PC when you turn usage data off. The example leaves out the server’s name and address."));
        var privacyNotice = new HyperlinkButton { Content = "Read the full privacy notice",
            NavigateUri = new Uri("https://cod2unbound.com/privacy"), IsTabStop = false };
        content.Children.Add(privacyNotice);

        var dialog = new UsagePreviewDialog
        {
            XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme,
            Title = "What is sent", Content = content, PrimaryButtonText = "Done", DefaultButton = ContentDialogButton.Primary
        };
        // ContentDialog chooses focusable content ahead of its default button.
        dialog.Opened += (_, _) => dialog.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            copy.IsTabStop = true;
            sample.IsTextSelectionEnabled = true;
            privacyNotice.IsTabStop = true;
        });
        await dialog.ShowAsync();
    }

    internal static Border GroupCard(StackPanel rows) =>
        new() { Style = StyleFor("FieldCard"), Padding = new Thickness(0), Child = rows };

    internal static void AddContact(StackPanel contacts, string title, string detail)
    {
        var row = Columns(16, new(190), new(1, GridUnitType.Star));
        Put(row, 0, new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        Put(row, 1, Detail(detail, 13, 19));
        AddRow(contacts, row);
    }

    private static void AddRow(StackPanel rows, Grid row)
    {
        if (rows.Children.Count != 0)
            rows.Children.Add(new Border { Style = StyleFor("CardDivider"), Height = 1 });
        row.Padding = new Thickness(16, 10, 16, 10);
        rows.Children.Add(row);
    }

    private static TextBlock Detail(string text, double size, double lineHeight) => new()
    {
        Text = text, Style = StyleFor("SecondaryText"), FontSize = size, LineHeight = lineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight
    };

    private sealed class UsagePreviewDialog : ContentDialog
    {
        public UsagePreviewDialog() => DefaultStyleKey = typeof(ContentDialog);

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (GetTemplateChild("ContentScrollViewer") is ScrollViewer scroll)
            {
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                AutomationProperties.SetName(scroll, "What is sent");
            }
        }
    }

    private static TextBlock Heading(string title) => new()
    {
        Text = title, Style = StyleFor("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap
    };
}
