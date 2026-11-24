using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

/// <summary>Setup, as the Mac's SetupView: the scene behind every step, a heading and one glass card,
/// and the glass capsule at the bottom with Back, a capsule per shown step and the step's actions.</summary>
public sealed partial class SetupPage : Page, IFullWindowPage, IShellBackdrop
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly LauncherPlace cancelDestination;
    private SetupSession Setup => session.Setup;
    private SetupStep? displayed;
    private PlayerNameEditor? nameEditor;
    private CdKeyEditor? keyEditor;
    private StackPanel? keyFeedback;
    private string displayMode = ModeValues[0];
    private string? savedName;
    private bool discKeyMissing;
    private bool focusPending;
    private Control? focusTarget;
    private Button? previewLink;

    private static readonly string[] ModeValues = ["borderless", "exclusive", "windowed"];
    private static readonly string[] ModeDetails =
    [
        "Fills the screen. Switching apps is instant.", "The game takes over the display. Switching apps takes a moment.",
        "A window you can move and resize on your desktop."
    ];

    internal SetupPage(MainWindow shell, WindowsLauncherSession session, LauncherPlace cancelDestination)
    {
        this.shell = shell;
        this.session = session;
        this.cancelDestination = cancelDestination;
        InitializeComponent();
        Backdrop = new Grid { IsHitTestVisible = false, Children = { SceneArt, SceneEdge, Vignette } };
        AutomationProperties.SetAccessibilityView(Backdrop, AccessibilityView.Raw);
        Backdrop.SizeChanged += (_, args) => PlaceScene(args.NewSize.Height);
        SetupMark.Shadow = new ThemeShadow();
        SetupMark.Translation = new System.Numerics.Vector3(0, 0, 32);
        Unloaded += (_, _) => StopSteamWatch();
        Loaded += async (_, _) =>
        {
            RenderBackdrop();
#if UPDATE_FIXTURES
            await PreviewAsync();
#else
            await Task.CompletedTask;
#endif
        };
        ActualThemeChanged += async (_, _) =>
        {
            RenderBackdrop();
            await RefreshAsync();
        };
    }

    internal async Task RefreshAsync()
    {
        if (displayed != Setup.Step)
        {
            displayed = Setup.Step;
            discKeyMissing = false;
            focusPending = true;
            IsTabStop = true;
            UseSystemFocusVisuals = false;
            session.Record(UsageEvent.Setup(Setup.Step));
            SetupScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
        focusTarget = null;
        var step = Setup.Step;
        bool welcome = step == SetupStep.Welcome;
        SetupMark.Visibility = IsVisible(welcome);
        string eyebrow = welcome ? "Reporting for duty" : StepTitle(step);
        SetupEyebrow.Text = eyebrow.ToUpperInvariant();
        // The Mac sets the Welcome eyebrow 14 further below the mark.
        SetupEyebrow.Margin = new Thickness(0, welcome ? 14 : 0, 0, 0);
        SetupTitle.FontSize = welcome ? 40 : 30;
        SetupTitle.LineHeight = welcome ? 46 : 36;
        (SetupTitle.Text, SetupDescription.Text) = step switch
        {
            SetupStep.Data => (Setup.Data.State == SetupDataState.Found ? "Game data found" : "Find your game data",
                "Choose the Call of Duty 2 folder from your own copy. CoD2 Unbound uses the files where they are; nothing is copied."),
            SetupStep.Key => ("Enter your CD key",
                "The 20-character key that came with your game. It unlocks multiplayer on official and CoD2x servers."),
            SetupStep.Personalize => ("Make it yours", "Two things most players set first. Skip this and you start with the game’s defaults."),
            SetupStep.CrashReports => (PrivacyPresentation.CrashTitle, PrivacyPresentation.CrashDetail),
            SetupStep.UsageData => (PrivacyPresentation.UsageTitle, PrivacyPresentation.UsageDetail),
            SetupStep.Ready => ("You’re ready to play", Setup.HasStoredKey ? "Everything the game needs is in place." :
                "Everything the game needs is in place, except your CD key."),
            _ => ("Welcome to CoD2 Unbound",
                "Call of Duty 2 multiplayer, set up for this PC. Setup takes about two minutes and uses your own copy of the game.")
        };
        RenderFooter();
        Layout();

        StepContent.Children.Clear();
        keyEditor = null;
        keyFeedback = null;
        nameEditor = null;
        switch (step)
        {
            case SetupStep.Welcome:
                RenderWelcome();
                break;
            case SetupStep.Data:
                RenderData();
                break;
            case SetupStep.Key:
                await RenderKeyAsync();
                break;
            case SetupStep.Personalize:
                await RenderPersonalizeAsync();
                break;
            case SetupStep.CrashReports:
            case SetupStep.UsageData:
                RenderQuestion(step == SetupStep.CrashReports);
                break;
            case SetupStep.Ready:
                await RenderReadyAsync();
                break;
        }
        AfterRender();
        UpdateContinue();
        FocusStep();
    }

    /// <summary>Fixture builds fill in a board state's typed text here; other builds compile no call.</summary>
    partial void AfterRender();

    private void RenderFooter()
    {
        var step = Setup.Step;
        bool question = step is SetupStep.CrashReports or SetupStep.UsageData;
        SetupBack.Visibility = IsVisible(step != SetupStep.Welcome);
        // The board: the first capsule sits 10 in from the bar's edge on Welcome, 18 after Back elsewhere.
        StepDots.Margin = new Thickness(step == SetupStep.Welcome ? 10 : 18, 0, 0, 0);
        SetupCancel.Visibility = IsVisible(Setup.CanCancel);
        string? alternate = step switch
        {
            SetupStep.Key or SetupStep.Personalize => "Skip for Now",
            SetupStep.Ready => "Find a Server",
            _ => null
        };
        SetupAlternate.Content = alternate;
        SetupAlternate.Visibility = IsVisible(alternate is not null);
        DeclineReport.Visibility = IsVisible(question);
        SetupNext.Content = step switch
        {
            SetupStep.Welcome => "Get Started",
            SetupStep.CrashReports => "Send Crash Reports",
            SetupStep.UsageData => "Send Usage Data",
            SetupStep.Ready => "Open CoD2 Unbound",
            _ => "Continue"
        };
        RenderDots();
    }

    /// <summary>SetupView.swift centers Welcome above the footer and pins every other step to 128 points.</summary>
    private void Layout()
    {
        double width = SetupFrame.ActualWidth, height = SetupFrame.ActualHeight;
        panelWidth = width > 0 && width < 1200 ? NarrowPanelWidth : PanelWidth;
        if (width > 0)
            panelWidth = Math.Min(panelWidth, Math.Max(320, width - 48));
        SetupColumn.Width = panelWidth;
        bool welcome = Setup.Step == SetupStep.Welcome;
        var padding = new Thickness(24, welcome ? 16 : 128, 24, 84 + 16);
        if (SetupStage.Padding != padding)
            SetupStage.Padding = padding;
        SetupStage.MinHeight = Math.Max(0, height);
        SetupColumn.VerticalAlignment = welcome ? VerticalAlignment.Center : VerticalAlignment.Top;
    }

    // The default column spacing in the stock RadioButtons template.
    private const double CrateGap = 7;

    private async void OnFrameSizeChanged(object sender, SizeChangedEventArgs args)
    {
        double before = panelWidth;
        Layout();
        if (before != panelWidth && displayed is not null)
            await RefreshAsync();
    }

    private void RenderWelcome()
    {
        var rows = new List<UIElement>
        {
            Row("folder", "Your Call of Duty 2 folder", "From a Steam, disc or Mac 1.3 install. Used where it is, never changed."),
            Row("key", "Your CD key", "The 20 characters that came with the game. Needed for online play.")
        };
        bool crash = Setup.Steps.Contains(SetupStep.CrashReports), usage = Setup.Steps.Contains(SetupStep.UsageData);
        if (crash && usage)
            rows.Add(Row("shield", "Two questions about sharing",
                "Whether to send crash reports and usage data. Your choice, and either can be changed later."));
        else if (crash || usage)
            rows.Add(Row("shield", "A question about sharing",
                $"Whether to send {(crash ? "crash reports" : "usage data")}. Your choice, and it can be changed later."));
        StepContent.Children.Add(GlassPanel(6, 12, GroupRows(58, [.. rows])));
        var note = Note("Nothing leaves this PC during setup.", "lock");
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.Margin = new Thickness(0, 6, 0, 0);
        StepContent.Children.Add(note);
        focusTarget = SetupNext;
    }

    private async Task RenderKeyAsync()
    {
        if (Setup.HasStoredKey && !Setup.ReplacingKey)
        {
            string? ending = Setup.PendingKeyTail ?? await session.KeyEndingAsync();
            var replace = Action("Replace…", async (_, _) => await shell.RunAsync(async () =>
            {
                Setup.ReplaceKey();
                focusPending = true;
                await RefreshAsync();
            }));
            var keep = Note("Continue keeps this key. You never have to type it again to move on.");
            keep.Margin = new Thickness(18, 0, 0, 8);
            StepContent.Children.Add(GlassPanel(6, 12,
                Group(Row("key", "A CD key is already stored on this PC", "•••• •••• •••• •••• " + (ending ?? "••••"),
                    trailing: replace, status: Status(SetupStatus.Ready, "Verified"))),
                keep));
            focusTarget = SetupNext;
            return;
        }

        var editor = new CdKeyEditor(shell, large: true);
        keyEditor = editor;
        keyFeedback = new StackPanel { Spacing = 12 };
        editor.Edited += () =>
        {
            RenderKeyFeedback();
            UpdateContinue();
        };
        editor.Submitted += async () =>
        {
            if (SetupNext.IsEnabled)
                await ContinueAsync(findServer: false);
        };
        var items = new List<UIElement> { editor, keyFeedback };
        if (!Setup.HasStoredKey && session.SetupCopies.Any(copy => copy.Source == GameInstallSource.Disc))
        {
            if (discKeyMissing)
                items.Add(Banner(SetupBannerTone.Neutral, "info",
                    "Windows doesn’t have a verified key for the disc install. Enter the key that came with your game."));
            else
            {
                var useDiscKey = Action("Use Key from Disc Install…", async (_, _) => await shell.RunAsync(async () =>
                {
                    string? key = session.ReadDiscKeyForSetup();
                    if (key is not null && CdKey.IsValid(key))
                    {
                        editor.Value = key;
                        return;
                    }
                    discKeyMissing = true;
                    focusPending = true;
                    string typed = editor.Value;
                    await RefreshAsync();
                    if (keyEditor is not null)
                        keyEditor.Value = typed;
                }));
                items.Add(Group(Row("folder", "A disc install is on this PC",
                    "Its key may be stored with it. Setup reads it only when you ask.", trailing: useDiscKey)));
            }
        }
        var stored = Note("Stored only on this PC, in the game’s own settings. Sent only to game servers when you join, as the original game does.", "lock");
        stored.Margin = new Thickness(8, 0, 8, 0);
        items.Add(stored);
        StepContent.Children.Add(GlassPanel(22, 12, [.. items]));
        RenderKeyFeedback();
        focusTarget = null;
        if (focusPending)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => keyEditor?.FocusInput());
    }

    /// <summary>Under the key: the hint, the characters to go, the checksum banner or the verified line.</summary>
    private void RenderKeyFeedback()
    {
        if (keyEditor is null || keyFeedback is null)
            return;
        keyFeedback.Children.Clear();
        var (state, left) = CdKey.Check(keyEditor.Value);
        UIElement line = state switch
        {
            CdKeyState.Mismatch => Banner(SetupBannerTone.Error, "warning",
                "The checksum doesn’t match. Check for a mistyped character: 0 and O, 1 and I are the usual ones."),
            CdKeyState.Verified => Status(SetupStatus.Ready, "Checksum verified."),
            CdKeyState.Typing => Note($"{left} character{(left == 1 ? "" : "s")} to go. Spaces and dashes are fine."),
            _ => Note("Spaces and dashes are fine. Find it on the manual, the disc sleeve, or in Steam under Manage › CD Keys.")
        };
        if (state != CdKeyState.Mismatch && line is FrameworkElement element)
            element.Margin = new Thickness(8, 0, 0, 0);
        keyFeedback.Children.Add(line);
    }

    private async Task RenderPersonalizeAsync()
    {
        savedName = await session.ReadRawPlayerNameAsync();
        var editor = new PlayerNameEditor(shell, setup: true)
        {
            NameText = Setup.Personalization?.Name ?? (savedName == SetupPlayer.Unknown ? "" : savedName ?? "")
        };
        nameEditor = editor;
        editor.Edited += UpdateContinue;
        editor.Submitted += async () =>
        {
            if (SetupNext.IsEnabled)
                await ContinueAsync(findServer: false);
        };
        if (displayed == SetupStep.Personalize && focusPending)
            displayMode = Setup.Personalization?.Display ?? session.Settings.Working.Fullscreen;
        if (Array.IndexOf(ModeValues, displayMode) < 0)
            displayMode = ModeValues[0];

        const string modeLabel = "How the game fills your screen";
        var heading = new TextBlock
        {
            Text = modeLabel, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Style = StyleFor("SetupDetail"), Margin = new Thickness(6, 0, 0, 0)
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        // The stock RadioButtons control keeps the system's arrow keys, roving focus and selection; its items are
        // radio buttons templated as the Mac's crates, sized to share the panel's width in three columns.
        var crates = new RadioButtons { MaxColumns = ModeValues.Length, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(crates, modeLabel);
        for (int index = 0; index < ModeValues.Length; index++)
        {
            string value = ModeValues[index];
            string title = SetupPlayer.DisplayMode(value);
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(new TextBlock { Text = title, Style = StyleFor("SetupCrateTitle") });
            if (index == 0)
                content.Children.Add(new Border
                {
                    Style = StyleFor("SetupBadge"),
                    Child = new TextBlock { Text = "Recommended", Style = StyleFor("TypeLabel"), Foreground = Token("accent"), VerticalAlignment = VerticalAlignment.Center }
                });
            content.Children.Add(new TextBlock { Text = ModeDetails[index], Style = StyleFor("SetupCrateDetail") });
            var crate = new RadioButton { Style = StyleFor("SetupCrate"), Content = content };
            AutomationProperties.SetName(crate, title + (index == 0 ? ", recommended" : ""));
            AutomationProperties.SetHelpText(crate, ModeDetails[index]);
            crates.Items.Add(crate);
        }
        crates.SelectedIndex = Array.IndexOf(ModeValues, displayMode);
        crates.SelectionChanged += (_, _) =>
        {
            if (crates.SelectedIndex >= 0)
                displayMode = ModeValues[crates.SelectedIndex];
        };
        // The Mac's crates share one width and the tallest one's height (SetupView.swift: maxWidth .infinity,
        // minHeight 84, in an HStack).
        ShareCells(crates, ModeValues.Length, CrateGap);
        var both = Note("Both can be changed any time: the name on your player card, the display in Settings.");
        both.Margin = new Thickness(6, 0, 0, 0);
        StepContent.Children.Add(GlassPanel(16, 16, editor, new StackPanel { Spacing = 8, Children = { heading, crates } }, both));
        focusTarget = null;
        if (focusPending)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => nameEditor?.FocusInput());
    }

    private void RenderQuestion(bool crash)
    {
        var link = PrivacyPresentation.PreviewButton(crash, "W", shell, session.Reports);
        // The Mac's quiet action: a link-weight button, not a capsule.
        link.Style = StyleFor("SetupLink");
        previewLink = link;
        var change = new TextBlock { Text = PrivacyPresentation.ChangeLater, Style = StyleFor("SetupDetail"), VerticalAlignment = VerticalAlignment.Center };
        var foot = Columns(8, new(1, GridUnitType.Star), GridLength.Auto);
        foot.Padding = new Thickness(10, 0, 4, 2);
        Put(foot, 0, change);
        Put(foot, 1, link);
        string[] symbols = ["eye", "lock", "share"];
        var rows = PrivacyPresentation.QuestionRows(crash)
            .Select((row, index) => (UIElement)Row(symbols[index], row.Title, row.Detail)).ToArray();
        StepContent.Children.Add(GlassPanel(6, 12, Group(rows), foot));
        // Neither answer is the default: nothing takes the focus and Enter presses neither.
        focusTarget = null;
    }

    private async Task RenderReadyAsync()
    {
        var settings = session.Settings.Working;
        string? name = Setup.Personalization?.Name ?? await session.ReadRawPlayerNameAsync();
        string player = string.IsNullOrWhiteSpace(name) ? SetupPlayer.Unknown : QuakeColors.Plain(name);
        bool crash = Setup.CrashChoice ?? settings.SendCrashReports;
        bool usage = Setup.UsageChoice ?? (settings.UsageConsent == UsageConsent.Yes);
        var rows = new List<UIElement>
        {
            Row("folder", "Game data", Setup.DataPath + " · Version 1.3", trailing: Status(SetupStatus.Ready, "Found")),
            Setup.HasStoredKey
                ? Row("key", "CD key", "Stored only on this PC", trailing: Status(SetupStatus.Ready, "Verified"))
                : Row("key", "CD key", "Not entered. Most servers need it; add it from your player card.",
                    trailing: Status(SetupStatus.NeedsChange, "Skipped")),
            Row("person", "Player and display",
                $"{player} · {SetupPlayer.DisplayMode(Setup.Personalization?.Display ?? settings.Fullscreen)} at {LauncherText.Resolution(settings.Resolution)}",
                trailing: Status(SetupStatus.Ready, "Set"))
        };
        if (Setup.Steps.Contains(SetupStep.CrashReports))
            rows.Add(crash
                ? Row("warning", "Crash reports", "Sent to the project if the game crashes · Change in Settings › Privacy", trailing: Status(SetupStatus.Ready, "On"))
                : Row("warning", "Crash reports", "Not sent · Change in Settings › Privacy", trailing: Status(SetupStatus.Off, "Off")));
        if (Setup.Steps.Contains(SetupStep.UsageData))
            rows.Add(usage
                ? Row("people", "Usage data", "Sent to the project · Change in Settings › Privacy", trailing: Status(SetupStatus.Ready, "On"))
                : Row("people", "Usage data", "Not sent · Change in Settings › Privacy", trailing: Status(SetupStatus.Off, "Off")));
        StepContent.Children.Add(GlassPanel(6, 12, Group([.. rows])));
        var again = Note("Run setup again any time from Settings › Game Data.");
        again.HorizontalAlignment = HorizontalAlignment.Center;
        again.Margin = new Thickness(0, 6, 0, 0);
        StepContent.Children.Add(again);
        focusTarget = SetupNext;
    }

    private void FocusStep()
    {
        if (!focusPending)
            return;
        focusPending = false;
        var target = focusTarget;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded)
                return;
            if (target is { IsEnabled: true } && target.Visibility == Visibility.Visible)
                target.Focus(FocusState.Programmatic);
            else if (keyEditor is null && nameEditor is null)
                Focus(FocusState.Programmatic);
        });
    }

    private void UpdateContinue()
    {
        SetupNext.IsEnabled = Setup.Step switch
        {
            SetupStep.Data => Setup.Data.State == SetupDataState.Found,
            SetupStep.Key => Setup.KeyReady(keyEditor?.Value ?? ""),
            SetupStep.Personalize => nameEditor?.Valid == true,
            _ => true
        };
        // ModelSetup.swift: the key step offers Skip for Now only while no key is kept or checks out.
        if (Setup.Step == SetupStep.Key)
            SetupAlternate.Visibility = IsVisible(!(Setup.HasStoredKey && !Setup.ReplacingKey) && keyEditor?.Verified != true);
    }

    private async void OnSetupBack(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        StopSteamWatch();
        if (keyEditor is not null)
            keyEditor.Value = "";
        Setup.KeepStoredKey();
        Setup.Back();
        await RefreshAsync();
    });

    private async void OnContinueSetup(object sender, RoutedEventArgs args) => await ContinueAsync(findServer: false);
    private async void OnDeclineReport(object sender, RoutedEventArgs args) => await AnswerAsync(false);

    private Task AnswerAsync(bool send) => shell.RunAsync(async () =>
    {
        Setup.ReviewPrivacy(send);
        await RefreshAsync();
    });

    private async Task ContinueAsync(bool findServer)
    {
        if (Setup.Step is SetupStep.CrashReports or SetupStep.UsageData)
        {
            await AnswerAsync(true);
            return;
        }
        await shell.RunAsync(async () =>
        {
            var previous = Setup.Step;
            try
            {
                if (previous == SetupStep.Ready)
                {
                    if (await session.CommitSetupAsync())
                    {
                        await shell.NavigateAsync(findServer ? LauncherPlace.Servers : LauncherPlace.Home);
                        return;
                    }
                }
                else if (previous == SetupStep.Personalize && nameEditor is { Valid: true } editor)
                {
                    string name = editor.NameText.Trim();
                    Setup.ReviewPersonalization(new(name.Length == 0 || name == savedName ? null : name, displayMode));
                }
                else if (await Setup.ContinueAsync(keyEditor?.Value ?? ""))
                {
                    if (previous == SetupStep.Data && Setup.DataWasPicked)
                        session.Record(UsageEvent.Setup(SetupStep.Data, ownFiles: true));
                    if (keyEditor is not null)
                        keyEditor.Value = "";
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                session.ShowError(UsageProblem.SaveSetup);
                await RefreshAsync();
                throw;
            }
            await RefreshAsync();
        });
    }

    private async void OnAlternate(object sender, RoutedEventArgs args)
    {
        if (Setup.Step == SetupStep.Ready)
        {
            await ContinueAsync(findServer: true);
            return;
        }
        await shell.RunAsync(async () =>
        {
            if (Setup.SkipKey())
                session.Record(UsageEvent.Setup(SetupStep.Key, skipped: true));
            else if (Setup.Step == SetupStep.Personalize)
                Setup.ReviewPersonalization(null);
            if (keyEditor is not null)
                keyEditor.Value = "";
            await RefreshAsync();
        });
    }

    private async void OnCancelSetup(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        if (!session.CancelSetup())
            return;
        StopSteamWatch();
        if (keyEditor is not null)
            keyEditor.Value = "";
        await shell.NavigateAsync(cancelDestination);
    });

    private async void OnDefaultAction(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!SetupNext.IsEnabled || Setup.Step is SetupStep.CrashReports or SetupStep.UsageData ||
            FocusManager.GetFocusedElement(XamlRoot) is ButtonBase or TextBox)
            return;
        args.Handled = true;
        await ContinueAsync(findServer: false);
    }

    private void OnCancelAction(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!Setup.CanCancel)
            return;
        args.Handled = true;
        OnCancelSetup(this, new RoutedEventArgs());
    }
}
