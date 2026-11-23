using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.ApplicationModel.DataTransfer;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class ShareSetupDialog : ContentDialog
{
    private const string Intro = "It joins Player Setups, the public list in Configs that every CoD2 Unbound player can browse. A person looks at each setup before it is listed.";
    private const string PreviewIntro = "This text, the name you type, the game and app versions, and the count of left-out lines with their reasons. Nothing else leaves your PC.";
    private readonly ConfigShareForm form;
    private readonly Func<bool> readOnly;
    private readonly Func<CancellationToken, Task<ConfigShareSent?>> publish;
    private readonly DataTemplate omittedLine;
    private readonly string shareTitle;
    private readonly TextBox name = new() { Header = "Name to show with the setup", PlaceholderText = "The name players know you by" };
    private readonly TextBlock nameProblem = Caption("");
    private readonly StackPanel fields = new() { Spacing = 6 };
    private readonly StackPanel facts = new() { Spacing = 0 };
    private readonly Border factCard;
    private readonly InfoBar notice = new() { IsClosable = false };
    private readonly ProgressBar sending = new();
    private readonly Button see;
    private readonly StackPanel main;
    private CancellationTokenSource? cancellation;
    private Task? inFlight;
    private bool closed;
    private bool preview;
    private string previewCopy = "";

    internal ShareSetupDialog(string loadout, ConfigShareForm form, DataTemplate omittedLine, Func<bool> readOnly,
        Func<CancellationToken, Task<ConfigShareSent?>> publish)
    {
        this.form = form;
        this.omittedLine = omittedLine;
        this.readOnly = readOnly;
        this.publish = publish;
        DefaultStyleKey = typeof(ContentDialog);
        DefaultButton = ContentDialogButton.Primary;
        HighContrastAdjustment = ElementHighContrastAdjustment.None;
        Resources["ContentDialogMaxWidth"] = 548d;
        Resources["ContentDialogMinWidth"] = 548d;
        shareTitle = $"Share “{loadout}” with other players";
        AutomationProperties.SetName(name, (string)name.Header);
        AutomationProperties.SetAutomationId(name, "ShareCreditName");
        AutomationProperties.SetLiveSetting(nameProblem, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(sending, "Sending setup");
        name.Text = form.Name;
        name.TextChanged += (_, _) =>
        {
            form.Name = name.Text;
            RefreshName();
        };

        see = new Button { Content = "See what’s sent…", Style = StyleFor("SubtleLinkAction"), AccessKey = "W" };
        AutomationProperties.SetName(see, "See what’s sent when sharing a setup");
        AutomationProperties.SetAutomationId(see, "SharePreview");
        see.Click += (_, _) => ShowPreview();

        factCard = PrivacyPresentation.GroupCard(facts);
        if (form.Prepared is { } prepared)
        {
            Fact("Document", "What is sent", $"{Count(prepared.Settings, "setting", "settings")} and {Count(prepared.Binds, "key bind", "key binds")} from this loadout, the name above, the game and app versions, and how many lines were left out and why.");
            Fact("LockClosed", "What is left out", $"Your name in the game, chat lines, passwords, server addresses and file paths: {Count(prepared.LeftOut.Length, "line", "lines")} here. A config never holds your CD key.");
            Fact("PeopleCommunity", "Where it goes", "To cod2unbound.com, which posts it publicly on GitHub as a request for review. Anyone can copy what is public, and you can take it back at any time.");
        }
        fields.Children.Add(name);
        fields.Children.Add(nameProblem);
        fields.Children.Add(factCard);
        fields.Children.Add(see);
        main = new StackPanel { Spacing = 8, Children = { Body(Intro), fields, sending, notice } };

        Opened += (_, _) => name.Focus(FocusState.Programmatic);
        Closing += (_, args) =>
        {
            if (preview)
            {
                args.Cancel = true;
                ShowForm();
                name.Focus(FocusState.Programmatic);
                return;
            }
            closed = true;
            cancellation?.Cancel();
        };
        PrimaryButtonClick += (_, args) =>
        {
            if (form.Phase == ConfigSharePhase.Sent)
                return;
            args.Cancel = true;
            if (preview)
            {
                ShowForm();
                name.Focus(FocusState.Programmatic);
            }
            else if (form.CanSend && !readOnly())
                inFlight = SendAsync();
        };
        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            var data = new DataPackage();
            data.SetText(previewCopy);
            Clipboard.SetContent(data);
            SecondaryButtonText = "Copied";
            Announce("Copied");
        };
        ShowForm();

        void Fact(string icon, string heading, string detail)
        {
            if (facts.Children.Count != 0)
                facts.Children.Add(new Border { Style = StyleFor("CardDivider"), Height = 1 });
            var row = Columns(10, GridLength.Auto, new GridLength(1, GridUnitType.Star));
            row.Padding = new Thickness(10, 6, 10, 6);
            var symbol = Fluent(icon, 18);
            symbol.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetAccessibilityView(symbol, AccessibilityView.Raw);
            Put(row, 0, symbol);
            Put(row, 1, new StackPanel
            {
                Spacing = 2,
                Children = { new TextBlock { Text = heading, Style = StyleFor("BodyStrongTextBlockStyle") }, Caption(detail) }
            });
            facts.Children.Add(row);
        }
    }

    internal async Task RunAsync()
    {
        await ShowAsync();
        if (inFlight is { } pending)
            await pending;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("ContentScrollViewer") is ScrollViewer scroll)
        {
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            AutomationProperties.SetName(scroll, "Share setup details");
        }
    }

    private void ShowForm()
    {
        preview = false;
        Title = shareTitle;
        Content = main;
        SecondaryButtonText = "";
        CloseButtonText = "Cancel";
        RefreshForm();
    }

    private void RefreshName()
    {
        nameProblem.Text = form.NameProblem ?? "";
        nameProblem.Visibility = IsVisible(nameProblem.Text.Length != 0);
        if (!preview)
            IsPrimaryButtonEnabled = form.CanSend && !readOnly();
    }

    private void RefreshForm()
    {
        bool busy = form.Phase == ConfigSharePhase.Sending;
        name.IsEnabled = see.IsEnabled = !busy;
        fields.Opacity = busy ? 0.6 : 1;
        factCard.Visibility = see.Visibility = IsVisible(form.ShowDetails);
        sending.Visibility = IsVisible(busy);
        sending.IsIndeterminate = busy;
        PrimaryButtonText = form.Action;
        RefreshName();
        var problem = form.CanSend && readOnly()
            ? new ConfigShareNotice(ConfigShareNoticeLevel.Info, "", "Sharing is off while Call of Duty 2 is running.")
            : form.Notice;
        ConfigsPage.ShowShareNotice(notice, problem);
    }

    private async Task SendAsync()
    {
        using var request = new CancellationTokenSource();
        cancellation = request;
        try
        {
            var pending = publish(request.Token);
            RefreshForm();
            if (form.Phase == ConfigSharePhase.Sending)
                Announce("Sending…");
            var sent = await pending;
            if (!closed)
            {
                if (sent is not null)
                    ShowSent(sent);
                else
                    RefreshForm();
            }
        }
        finally { cancellation = null; }
    }

    private void ShowPreview()
    {
        if (form.Prepared is not { } prepared)
            return;
        preview = true;
        Title = "What is sent";
        PrimaryButtonText = "Back";
        IsPrimaryButtonEnabled = true;
        CloseButtonText = "";
        var sentLines = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, Height = 224, TextWrapping = TextWrapping.Wrap,
            Style = StyleFor("MonoField"), Text = prepared.Text
        };
        ScrollViewer.SetVerticalScrollBarVisibility(sentLines, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(sentLines, "The exact text that is sent");
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemsSource = prepared.LeftOut, ItemTemplate = omittedLine
        };
        AutomationProperties.SetName(list, "Lines left out and their reasons");
        var omitted = new Border
        {
            Style = StyleFor("FieldCard"), Padding = new Thickness(8), Height = 224,
            Child = prepared.LeftOut.IsEmpty ? Caption("Nothing was left out of this loadout.") : list
        };
        var tabs = Tabs([$"Sent · {prepared.Lines}", $"Left out · {prepared.LeftOut.Length}"], 0, Fill);
        AutomationProperties.SetName(tabs, "Lines to show");
        Content = new StackPanel { Spacing = 10, Children = { Body(PreviewIntro), tabs, sentLines, omitted } };
        Fill(0);
        tabs.Focus(FocusState.Programmatic);

        void Fill(int index)
        {
            bool sent = index == 0;
            previewCopy = sent ? prepared.Text : string.Concat(prepared.LeftOut.Select(line => $"{line.Shown}\t{line.Because}\n"));
            sentLines.Visibility = IsVisible(sent);
            omitted.Visibility = IsVisible(!sent);
            SecondaryButtonText = "Copy";
            IsSecondaryButtonEnabled = sent || !prepared.LeftOut.IsEmpty;
        }
    }

    private void ShowSent(ConfigShareSent sent)
    {
        Title = "Your setup was sent";
        PrimaryButtonText = form.Action;
        IsPrimaryButtonEnabled = true;
        CloseButtonText = "";
        var icon = Fluent("CheckCircle", 30);
        icon.Style = StyleFor("PositiveIcon");
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        var address = MonoCaption(sent.Url.AbsoluteUri);
        address.TextWrapping = TextWrapping.Wrap;
        address.IsTextSelectionEnabled = true;
        address.LineHeight = 18;
        address.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                icon,
                Body($"It shows up in Player Setups under the name {sent.Name} once a person has looked at it, usually within a few days. There is nothing more for you to do."),
                new Border { Style = StyleFor("FieldCard"), Child = address },
                Caption("To take it back, choose Stop sharing on this loadout. No reason is asked for."),
                RequestLink(sent.Url)
            }
        };
        if (!sent.Saved)
        {
            var warning = new InfoBar { IsClosable = false };
            ConfigsPage.ShowShareNotice(warning, new(ConfigShareNoticeLevel.Warning, "",
                "CoD2 Unbound couldn’t save its record. To take this setup back, choose Stop sharing before closing CoD2 Unbound."));
            body.Children.Add(warning);
        }
        Content = body;
        if (GetTemplateChild("PrimaryButton") is Button done)
            done.Focus(FocusState.Programmatic);
        Announce("Your setup was sent");
    }

    internal static HyperlinkButton RequestLink(Uri url)
    {
        var link = new HyperlinkButton { Content = "View request", NavigateUri = url, Padding = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetName(link, "View request");
        return link;
    }

    private void Announce(string message) =>
        FrameworkElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, message, "ShareSetup");

    private static string Count(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";
}
