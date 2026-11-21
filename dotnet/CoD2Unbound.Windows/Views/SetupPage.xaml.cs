using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SetupPage : Page
{
    private readonly MainWindow shell;
    private readonly SetupViewModel model;

    internal SetupPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        model = new(session);
        InitializeComponent();
    }

    internal void Refresh()
    {
        var setup = model.Setup;
        (SetupTitle.Text, SetupDescription.Text) = setup.Step switch
        {
            SetupStep.Welcome => ("Welcome to CoD2 Unbound", "Set up your copy of Call of Duty 2 for multiplayer."),
            SetupStep.Data => ("Game Data", "CoD2 Unbound uses the game files from your Steam or retail copy."),
            SetupStep.Key => ("Your CD key", "Some servers need a Call of Duty 2 multiplayer key. You can add it now or skip this step."),
            SetupStep.Ready => ("Ready to play", "Your game data is ready. Finish setup to open Home."),
            _ => throw new InvalidOperationException("Unknown launcher setup step.")
        };
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
        await setup.ContinueAsync(KeyInput.Password);
        KeyInput.Password = "";
        if (setup.Step == SetupStep.Personalize)
            setup.PersonalizationSaved();
        if (setup.Step == SetupStep.Complete)
        {
            model.Finish();
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
            model.Setup.PersonalizationSaved();
        KeyInput.Password = "";
        Refresh();
    }

    private async void OnChooseSetupFolder(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        if (await shell.PickFolderAsync() is { } path)
        {
            model.Setup.ChooseData(path);
            Refresh();
        }
    });

}
