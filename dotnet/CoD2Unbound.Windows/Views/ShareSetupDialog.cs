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
    private readonly string appVersion;
    private readonly ConfigShareForm form;
    private readonly Func<bool> readOnly;
    private readonly Func<CancellationToken, Task<ConfigShareSent?>> publish;
    private readonly DataTemplate omittedLine;
    private readonly string shareTitle;
    private readonly TextBox name = new() { PlaceholderText = "The name players know you by" };
    private readonly TextBlock nameProblem = Caption("");
    private readonly StackPanel fields = new() { Spacing = 6 };
    private readonly StackPanel facts = new() { Spacing = 0 };
    private readonly Border factCard;
    private readonly InfoBar notice = new() { IsClosable = false };
    private readonly ProgressBar sending = new();
    private readonly StackPanel main;
    private CancellationTokenSource? cancellation;
    private Task? inFlight;
    private bool closed;
    private bool preview;
    private string previewCopy = "";

    internal ShareSetupDialog(string loadout, string appVersion, ConfigShareForm form, DataTemplate omittedLine, Func<bool> readOnly,
        Func<CancellationToken, Task<ConfigShareSent?>> publish)
    {
        // A C# subclass does not pick up the implicit ContentDialog style; use the shared sheet.
        Style = StyleFor("LauncherSheet");
        this.appVersion = appVersion;
        this.form = form;
        this.omittedLine = omittedLine;
        this.readOnly = readOnly;
        this.publish = publish;
        DefaultStyleKey = typeof(ContentDialog);
        DefaultButton = ContentDialogButton.Primary;
        HighContrastAdjustment = ElementHighContrastAdjustment.None;
        this.WithWidth("ShareSheetWidth");
        shareTitle = $"Share “{loadout}” with other players";
        AutomationProperties.SetName(name, "Name to show with the setup");
        AutomationProperties.SetAutomationId(name, "ShareCreditName");
        AutomationProperties.SetLiveSetting(nameProblem, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(sending, "Sending setup");
        name.Style = StyleFor("CapsuleTextBox");
        name.Text = form.Name;
        name.TextChanged += (_, _) =>
        {
            form.Name = name.Text;
            RefreshName();
        };

        factCard = new Border { Style = StyleFor("SheetFormGroup"), Child = facts };
        if (form.Prepared is { } prepared)
        {
            var (sent, leftOut) = PrivacyPresentation.ShareFacts(prepared);
            double size = (double)Application.Current.Resources["BodySize"];
            Fact(Fluent("Document", size), "What is sent", sent);
            Fact(Fluent("LockClosed", size), "What is left out", leftOut);
            Fact(SystemIcon("SystemGlobe", size), "Where it goes", PrivacyPresentation.ShareDestination);
        }
        fields.Children.Add(name);
        fields.Children.Add(nameProblem);
        fields.Children.Add(factCard);
        main = new StackPanel { Spacing = 12, Children = { fields, sending, notice } };

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
            if (!preview)
            {
                ShowPreview();
                return;
            }
            var data = new DataPackage();
            data.SetText(previewCopy);
            Clipboard.SetContent(data);
            SecondaryButtonText = "Copied";
            Announce("Copied");
        };
        ShowForm();

        void Fact(FontIcon symbol, string heading, string detail)
        {
            if (facts.Children.Count != 0)
                facts.Children.Add(new Border { Style = StyleFor("CardDivider"), Height = 1 });
            var tile = RowIconTile(symbol, VerticalAlignment.Top);
            var words = new StackPanel
            {
                Spacing = 3,
                Children = { new TextBlock { Text = heading, Style = StyleFor("SheetFactHeading") },
                    new TextBlock { Text = detail, Style = StyleFor("SheetFactDetail") } }
            };
            var row = new Grid { Style = StyleFor("SheetFactRow") };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new());
            Put(row, 0, tile);
            Put(row, 1, words);
            facts.Children.Add(row);
        }
    }

    internal async Task RunAsync()
    {
        await ShowAsync();
        if (inFlight is { } pending)
            await pending;
    }

    private void ShowForm()
    {
        preview = false;
        SheetPresentation.SetSheetTitle(this, "FluentShare", shareTitle, PrivacyPresentation.ShareIntro);
        Content = main;
        SecondaryButtonStyle = StyleFor("SharePreviewAction");
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
        name.IsEnabled = !busy;
        factCard.Opacity = busy ? 0.45 : 1;
        factCard.Visibility = IsVisible(form.ShowDetails);
        SecondaryButtonText = form.ShowDetails ? "See What’s Sent…" : "";
        IsSecondaryButtonEnabled = !busy;
        sending.Visibility = IsVisible(busy);
        sending.IsIndeterminate = busy;
        PrimaryButtonText = form.Phase switch
        {
            ConfigSharePhase.Offline => "Try Again",
            ConfigSharePhase.Sending => form.Action,
            _ => "Share Setup"
        };
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
                {
                    RefreshForm();
                    if (form.Phase == ConfigSharePhase.Offline && IsPrimaryButtonEnabled)
                        name.Focus(FocusState.Programmatic);
                }
            }
        }
        finally { cancellation = null; }
    }

    private void ShowPreview()
    {
        if (form.Prepared is not { } prepared)
            return;
        preview = true;
        SheetPresentation.SetSheetTitle(this, "FluentDocument", "What is sent", PrivacyPresentation.SharePreviewIntro(appVersion));
        PrimaryButtonText = "Done";
        IsPrimaryButtonEnabled = true;
        SecondaryButtonStyle = StyleFor("SheetButton");
        CloseButtonText = "";
        var sentLines = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, Height = 248, TextWrapping = TextWrapping.Wrap,
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
            Style = StyleFor("FieldCard"), Padding = new Thickness(8), Height = 248,
            Child = prepared.LeftOut.IsEmpty ? Caption("Nothing was left out of this loadout.") : list
        };
        var tabs = Tabs([$"Sent · {prepared.Lines}", $"Left Out · {prepared.LeftOut.Length}"], 0, Fill);
        AutomationProperties.SetName(tabs, "Lines to show");
        Content = new StackPanel { Spacing = 10, Children = { tabs, sentLines, omitted } };
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
        SheetPresentation.SetSheetTitle(this, "FluentCheckCircle", "Your setup was sent",
            $"It shows up in Player Setups under the name {sent.Name} once a person has looked at it, usually within a few days. There is nothing more for you to do.",
            SheetPresentation.Tone.Positive);
        PrimaryButtonText = form.Action;
        IsPrimaryButtonEnabled = true;
        SecondaryButtonText = "";
        CloseButtonText = "";
        string url = sent.Url.AbsoluteUri;
        string shortAddress = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var address = MonoCaption(shortAddress);
        AutomationProperties.SetName(address, $"Request address: {shortAddress}");
        address.TextWrapping = TextWrapping.Wrap;
        address.IsTextSelectionEnabled = true;
        address.LineHeight = 18;
        address.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var requestLink = RequestLink(sent.Url);
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new Border { Style = StyleFor("FieldCard"), Child = address },
                Caption("To take it back, choose Stop Sharing from this loadout’s menu. No reason is asked for."),
                requestLink
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
        requestLink.Focus(FocusState.Programmatic);
        Announce("Your setup was sent");
    }

    internal static HyperlinkButton RequestLink(Uri url)
    {
        var link = new HyperlinkButton { Content = "View Request", NavigateUri = url, Padding = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetName(link, "View request");
        return link;
    }

    private void Announce(string message) =>
        FrameworkElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, message, "ShareSetup");

}
