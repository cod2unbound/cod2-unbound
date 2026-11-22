using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SetupPage : Page
{
    private readonly MainWindow shell;
    private readonly SetupViewModel model;
    private readonly WindowsLauncherSession session;
    private SetupStep? displayed;

    internal SetupPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        this.session = session;
        model = new(session);
        InitializeComponent();
    }

    internal void Refresh()
    {
        var setup = model.Setup;
        (SetupTitle.Text, SetupDescription.Text) = setup.Step switch
        {
            SetupStep.Welcome => ("Welcome to CoD2 Unbound", "Set up your copy of Call of Duty 2 for multiplayer."),
            SetupStep.Data => ("Game data", "CoD2 Unbound uses the game files from your Steam or retail copy."),
            SetupStep.Key => ("Your CD key", "Some servers need a Call of Duty 2 multiplayer key. You can add it now or skip this step."),
            SetupStep.CrashReports => (PrivacyPresentation.CrashTitle, PrivacyPresentation.CrashDetail),
            SetupStep.UsageData => (PrivacyPresentation.UsageTitle, PrivacyPresentation.UsageDetail),
            SetupStep.Ready => ("Ready to play", "Your game data is ready. Finish setup to open Home."),
            _ => throw new InvalidOperationException("Unknown launcher setup step.")
        };
        bool question = setup.Step is SetupStep.CrashReports or SetupStep.UsageData;
        SendReport.Visibility = DeclineReport.Visibility = PrivacyQuestion.Visibility = IsVisible(question);
        SetupNext.Visibility = IsVisible(!question);
        if (displayed != setup.Step)
        {
            displayed = setup.Step;
            session.Record(UsageEvent.Setup(setup.Step));
            PrivacyQuestion.Children.Clear();
            if (question)
            {
                bool crash = setup.Step == SetupStep.CrashReports;
                PrivacyQuestion.Children.Add(PrivacyPresentation.Question(crash));
                PrivacyQuestion.Children.Add(Caption(PrivacyPresentation.ChangeLater));
                var preview = PrivacyPresentation.PreviewButton(crash, "W", shell, session.Reports);
                preview.TabIndex = 3;
                PrivacyQuestion.Children.Add(preview);
                string send = crash ? "Send crash reports" : "Send usage data";
                SendReport.Content = send;
                AutomationProperties.SetName(SendReport, send);
                IsTabStop = true;
                UseSystemFocusVisuals = false;
                Focus(FocusState.Programmatic);
            }
        }
        bool keyStep = setup.Step == SetupStep.Key;
        SetupKeyCard.Visibility = SkipKey.Visibility = IsVisible(keyStep);
        StoredKey.Visibility = ReplaceKey.Visibility = IsVisible(keyStep && setup.HasStoredKey && !setup.ReplacingKey);
        KeyInput.Visibility = IsVisible(keyStep && (!setup.HasStoredKey || setup.ReplacingKey));
        SetupBack.Visibility = IsVisible(setup.Step != SetupStep.Welcome);
        SetupDataCard.Visibility = IsVisible(setup.Step == SetupStep.Data);
        SetupDataStatus.Text = DataSummary(setup.Data);
        SetupDataDetail.Text = setup.Data.Problem ?? (setup.Data.State == SetupDataState.Found ? SetupSession.FoundSummary : "");
        SetupDataDetail.Visibility = IsVisible(SetupDataDetail.Text.Length > 0);
        SetupDataPath.Text = setup.Data.Path;
        SetupDataPath.Visibility = IsVisible(SetupDataPath.Text.Length > 0);
        SetupNext.Content = setup.Step == SetupStep.Ready ? "Finish setup" : "Continue";
        UpdateContinue();
    }

    private void OnSetupBack(object sender, RoutedEventArgs args)
    {
        if (shell.IsBusy)
            return;
        // The core exposes the hidden Personalize step for hosts with a profile editor.
        model.Setup.Back();
        if (model.Setup.Step == SetupStep.Personalize)
            model.Setup.Back();
        shell.ClearProblem();
        KeyInput.Password = "";
        Refresh();
    }

    private void OnReplaceKey(object sender, RoutedEventArgs args)
    {
        if (shell.IsBusy)
            return;
        model.Setup.ReplaceKey();
        Refresh();
        KeyInput.Focus(FocusState.Programmatic);
    }

    private void OnKeyChanged(object sender, RoutedEventArgs args) => UpdateContinue();

    private void UpdateContinue() => SetupNext.IsEnabled = model.Setup.Step switch
    {
        SetupStep.Data => model.Setup.Data.State == SetupDataState.Found,
        SetupStep.Key => model.Setup.KeyReady(KeyInput.Password),
        _ => true
    };

    private async void OnContinueSetup(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        var setup = model.Setup;
        var previous = setup.Step;
        try { await setup.ContinueAsync(KeyInput.Password); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            session.ShowError(UsageProblem.SaveSetup);
            throw;
        }
        if (previous == SetupStep.Data && setup.Step == SetupStep.Key)
            session.Record(UsageEvent.Setup(SetupStep.Data, ownFiles: true));
        KeyInput.Password = "";
        if (setup.Step == SetupStep.Personalize)
            setup.PersonalizationSaved();
        if (setup.Step == SetupStep.Complete)
        {
            await model.FinishAsync();
            await shell.NavigateAsync(LauncherPlace.Home);
        }
        else
        {
            Refresh();
        }
    });

    private void OnSkipKey(object sender, RoutedEventArgs args)
    {
        if (shell.IsBusy)
            return;
        if (model.Setup.SkipKey())
        {
            session.Record(UsageEvent.Setup(SetupStep.Key, skipped: true));
            model.Setup.PersonalizationSaved();
        }
        KeyInput.Password = "";
        Refresh();
    }

    private async void OnSendReport(object sender, RoutedEventArgs args) => await AnswerAsync(true);
    private async void OnDeclineReport(object sender, RoutedEventArgs args) => await AnswerAsync(false);

    private Task AnswerAsync(bool send) => shell.RunAsync(async () =>
    {
        if (model.Setup.Step == SetupStep.CrashReports)
        {
            await session.SetCrashAsync(send);
            model.Setup.CrashQuestionAnswered();
        }
        else if (model.Setup.Step == SetupStep.UsageData)
        {
            await session.SetUsageAsync(send, UsageSource.Setup);
            model.Setup.UsageQuestionAnswered();
        }
        Refresh();
    });

    private async void OnChooseSetupFolder(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        if (await shell.PickFolderAsync() is { } path)
        {
            if (model.Setup.ChooseData(path).State == SetupDataState.Damaged)
                session.ShowError(UsageProblem.GameData);
            Refresh();
        }
    });

}
