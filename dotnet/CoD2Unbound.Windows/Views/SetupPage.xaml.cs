using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SetupPage : Page
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly LauncherPlace cancelDestination;
    private SetupSession Setup => session.Setup;
    private SetupStep? displayed;
    private PlayerNameEditor? nameEditor;
    private CdKeyEditor? keyEditor;
    private RadioButtons? modes;
    private string? savedName;

    private static readonly string[] ModeValues = ["borderless", "exclusive", "windowed"];

    internal SetupPage(MainWindow shell, WindowsLauncherSession session, LauncherPlace cancelDestination)
    {
        this.shell = shell;
        this.session = session;
        this.cancelDestination = cancelDestination;
        InitializeComponent();
        Unloaded += (_, _) => StopSteamWatch();
        ActualThemeChanged += (_, _) => RenderPips(StepNumber(Setup.Step));
    }

    internal async Task RefreshAsync()
    {
        if (displayed != Setup.Step)
        {
            displayed = Setup.Step;
            IsTabStop = true;
            UseSystemFocusVisuals = false;
            if (IsLoaded)
                Focus(FocusState.Programmatic);
            session.Record(UsageEvent.Setup(Setup.Step));
            SetupScroll.ScrollToVerticalOffset(0);
        }

        int number = StepNumber(Setup.Step);
        string caption = Setup.Step switch
        {
            SetupStep.Data => "Game data", SetupStep.Key => "CD key", SetupStep.Personalize => "Make it yours",
            SetupStep.CrashReports => "Crash reports", SetupStep.UsageData => "Usage data",
            SetupStep.Ready => "Ready", _ => "Welcome"
        };
        SetupCaption.Text = $"{(session.ReviewingSetup ? "Setting up again · " : "")}Step {number} of 7 · {caption}";
        RenderPips(number);
        (SetupTitle.Text, SetupDescription.Text) = Setup.Step switch
        {
            SetupStep.Data => ("Find your game data",
                "Choose the Call of Duty 2 folder from your own copy. CoD2 Unbound uses the files where they are. Nothing is copied."),
            SetupStep.Key => ("Enter your CD key",
                "The 20-character key that came with your game. Many multiplayer servers require it."),
            SetupStep.Personalize => ("Make it yours", "Two things most players set first. Skip this to keep your current choices."),
            SetupStep.CrashReports => (PrivacyPresentation.CrashTitle, PrivacyPresentation.CrashDetail),
            SetupStep.UsageData => (PrivacyPresentation.UsageTitle, PrivacyPresentation.UsageDetail),
            SetupStep.Ready => ("You’re ready to play", Setup.HasStoredKey ? "Your game data is ready." :
                "Your game data is ready. You can add a CD key any time."),
            _ => ("Welcome to CoD2 Unbound", "Set up your copy of Call of Duty 2 for multiplayer.")
        };
        bool question = Setup.Step is SetupStep.CrashReports or SetupStep.UsageData;
        SetupBack.Visibility = IsVisible(Setup.Step != SetupStep.Welcome);
        SetupAlternate.Content = Setup.CanCancel ? "Cancel" : Setup.Step == SetupStep.Ready ? "Find a server" : "Skip for now";
        SetupAlternate.Visibility = IsVisible(Setup.CanCancel ||
            Setup.Step == SetupStep.Ready || (!session.ReviewingSetup && Setup.Step is SetupStep.Key or SetupStep.Personalize));
        SetupFind.Visibility = IsVisible(Setup.CanCancel && Setup.Step == SetupStep.Ready);
        DeclineReport.Visibility = IsVisible(question);
        SetupNext.Content = Setup.Step switch
        {
            SetupStep.CrashReports => "Send crash reports", SetupStep.UsageData => "Send usage data",
            SetupStep.Ready => "Open CoD2 Unbound", _ => "Continue"
        };

        StepContent.Children.Clear();
        keyEditor = null;
        nameEditor = null;
        switch (Setup.Step)
        {
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
                bool crash = Setup.Step == SetupStep.CrashReports;
                StepContent.Children.Add(PrivacyPresentation.Question(crash));
                StepContent.Children.Add(PrivacyPresentation.PreviewButton(crash, "W", shell, session.Reports));
                StepContent.Children.Add(Caption(PrivacyPresentation.ChangeLater));
                break;
            case SetupStep.Ready:
                await RenderReadyAsync();
                break;
        }
        UpdateContinue();
    }

    private static int StepNumber(SetupStep step) => step switch
    {
        SetupStep.Data => 2, SetupStep.Key => 3, SetupStep.Personalize => 4,
        SetupStep.CrashReports => 5, SetupStep.UsageData => 6, SetupStep.Ready => 7, _ => 1
    };

    private void OnFrameSizeChanged(object sender, SizeChangedEventArgs args)
    {
        double width = Math.Min(args.NewSize.Width <= 1050 ? 600 : 680, Math.Max(0, args.NewSize.Width - 60));
        SetupColumn.Width = FooterColumn.Width = width;
    }

    private void RenderPips(int step)
    {
        if (Pips is null)
            return;
        StepProgress.Value = step;
        AutomationProperties.SetName(StepProgress, $"Step {step} of 7");
        Pips.Children.Clear();
        for (int position = 1; position <= 7; position++)
        {
            string brush = position == step ? "AccentFillColorDefaultBrush" : position < step ? "TertiaryBrush" : "PanelStrokeBrush";
            var pip = new Border
            {
                Width = position == step ? 18 : 7, Height = 4, CornerRadius = new(2),
                Background = ThemeBrush(brush, ActualTheme, shell.IsHighContrast)
            };
            AutomationProperties.SetAccessibilityView(pip, AccessibilityView.Raw);
            Pips.Children.Add(pip);
        }
    }

    private async Task RenderKeyAsync()
    {
        if (Setup.HasStoredKey && !Setup.ReplacingKey)
        {
            string? ending = Setup.PendingKeyTail ?? await session.KeyEndingAsync();
            var replace = new Button { Content = "Replace…" };
            replace.Click += async (_, _) => await shell.RunAsync(async () =>
            {
                Setup.ReplaceKey();
                await RefreshAsync();
                keyEditor?.FocusInput();
            });
            StepContent.Children.Add(SettingRow("A CD key is already stored on this PC",
                ending is null ? "Verified" : $"•••• •••• •••• •••• {ending} · Verified", replace));
            StepContent.Children.Add(Caption("Continue keeps this key. You never have to type it again to move on."));
        }
        else
        {
            var editor = new CdKeyEditor(shell);
            keyEditor = editor;
            editor.Edited += UpdateContinue;
            StepContent.Children.Add(editor);
            if (!Setup.HasStoredKey && session.SetupCopies.Any(copy => copy.Source == GameInstallSource.Disc))
            {
                var useDiscKey = new Button { Content = "Use key from disc install…" };
                var result = new InfoBar
                {
                    IsClosable = false, Severity = InfoBarSeverity.Informational,
                    Message = "Windows doesn’t have a verified key for the disc install. Enter the key that came with your game."
                };
                useDiscKey.Click += (_, _) =>
                {
                    string? key = session.ReadDiscKeyForSetup();
                    result.IsOpen = key is null;
                    if (key is not null)
                        editor.Value = key;
                };
                StepContent.Children.Add(useDiscKey);
                StepContent.Children.Add(result);
            }
        }
        StepContent.Children.Add(Caption("Stored only on this PC. Sent only to game servers when you join, as the original game does."));
    }

    private async Task RenderPersonalizeAsync()
    {
        savedName = await session.ReadRawPlayerNameAsync();
        var editor = new PlayerNameEditor(shell)
        {
            NameText = Setup.Personalization?.Name ?? (savedName == SetupPlayer.Unknown ? "" : savedName ?? "")
        };
        nameEditor = editor;
        editor.Edited += UpdateContinue;

        const string modeLabel = "How the game fills your screen";
        var buttons = new RadioButtons();
        AutomationProperties.SetName(buttons, modeLabel);
        var descriptions = new[]
        {
            "Fills the screen. Switching apps is instant.", "The game takes over the display. Switching apps takes a moment.",
            "A window you can move and resize on your desktop."
        };
        for (int index = 0; index < ModeValues.Length; index++)
        {
            string label = SetupPlayer.DisplayMode(ModeValues[index]) + (index == 0 ? " (Recommended)" : "");
            buttons.Items.Add(RadioChoice(label, descriptions[index]));
        }
        buttons.SelectedIndex = Math.Max(0, Array.IndexOf(ModeValues, Setup.Personalization?.Display ?? session.Settings.Working.Fullscreen));
        modes = buttons;
        var display = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = modeLabel }, DataCard(buttons) } };
        StepContent.Children.Add(DataCard(editor, display,
            Caption("Change your name from the player card or your display mode in Settings.")));
    }

    private async Task RenderReadyAsync()
    {
        var settings = session.Settings.Working;
        string? name = Setup.Personalization?.Name ?? await session.ReadRawPlayerNameAsync();
        string player = string.IsNullOrWhiteSpace(name) ? SetupPlayer.Unknown : QuakeColors.Plain(name);
        bool crash = Setup.CrashChoice ?? settings.SendCrashReports;
        bool usage = Setup.UsageChoice ?? (settings.UsageConsent == UsageConsent.Yes);
        Add("Game data", Setup.DataPath + " · Version 1.3", "Found", "PositiveBrush");
        Add("CD key", Setup.HasStoredKey ? "Stored only on this PC" : "Not entered. Most servers need it. Add it from your player card.",
            Setup.HasStoredKey ? "Verified" : "Skipped", Setup.HasStoredKey ? "PositiveBrush" : "FavoriteBrush");
        Add("Player and display",
            $"{player} · {SetupPlayer.DisplayMode(Setup.Personalization?.Display ?? settings.Fullscreen)} at {LauncherText.Resolution(settings.Resolution)}",
            "Set", "PositiveBrush");
        Add("Crash reports", (crash ? "Sent to the project if the game crashes" : "Not sent") + " · Change in Settings › Privacy", crash ? "On" : "Off");
        Add("Usage data", (usage ? "Sent to the project" : "Not sent") + " · Change in Settings › Privacy", usage ? "On" : "Off");
        StepContent.Children.Add(Caption("Run setup again any time from Settings › Game data."));

        void Add(string title, string description, string status, string? brush = null)
        {
            var badge = new TextBlock { Text = status, VerticalAlignment = VerticalAlignment.Center };
            if (brush is not null && !shell.IsHighContrast)
                badge.Foreground = ThemeBrush(brush, ActualTheme, false);
            StepContent.Children.Add(SettingRow(title, description, badge));
        }
    }

    private static StackPanel ChoiceContent(string label, string detail) => new()
    {
        Spacing = 2, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Caption(detail) }
    };

    private static RadioButton RadioChoice(string label, string detail)
    {
        var button = new RadioButton { Content = ChoiceContent(label, detail) };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, detail);
        return button;
    }

    private void UpdateContinue() => SetupNext.IsEnabled = Setup.Step switch
    {
        SetupStep.Data => Setup.Data.State == SetupDataState.Found,
        SetupStep.Key => Setup.KeyReady(keyEditor?.Value ?? ""),
        SetupStep.Personalize => nameEditor?.Valid == true,
        _ => true
    };

    private async void OnSetupBack(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        StopSteamWatch();
        if (keyEditor is not null)
            keyEditor.Value = "";
        Setup.KeepStoredKey();
        Setup.Back();
        await RefreshAsync();
    });

    private async void OnFindServer(object sender, RoutedEventArgs args) => await ContinueAsync(findServer: true);

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
                    Setup.ReviewPersonalization(new(name.Length == 0 || name == savedName ? null : name,
                        ModeValues[modes!.SelectedIndex]));
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
        if (Setup.Step == SetupStep.Ready && !Setup.CanCancel)
        {
            await ContinueAsync(findServer: true);
            return;
        }
        await shell.RunAsync(async () =>
        {
            if (session.CancelSetup())
            {
                StopSteamWatch();
                if (keyEditor is not null)
                    keyEditor.Value = "";
                await shell.NavigateAsync(cancelDestination);
                return;
            }
            if (Setup.SkipKey())
                session.Record(UsageEvent.Setup(SetupStep.Key, skipped: true));
            else if (Setup.Step == SetupStep.Personalize)
                Setup.ReviewPersonalization(null);
            if (keyEditor is not null)
                keyEditor.Value = "";
            await RefreshAsync();
        });
    }

    private async void OnDefaultAction(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!SetupNext.IsEnabled || Setup.Step is SetupStep.CrashReports or SetupStep.UsageData ||
            FocusManager.GetFocusedElement(XamlRoot) is ButtonBase)
            return;
        args.Handled = true;
        await ContinueAsync(findServer: false);
    }

    private void OnCancelAction(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!Setup.CanCancel)
            return;
        args.Handled = true;
        OnAlternate(this, new RoutedEventArgs());
    }
}
