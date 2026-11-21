using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SettingsPage : Page
{
    private readonly MainWindow shell;
    private readonly SettingsViewModel model;
    private bool updatingSettings = true;

    internal SettingsPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        model = new(session);
        InitializeComponent();
        model.RefreshData();
        RenderSettingsData();
        SyncChoices(model.Settings.Working);
    }

    private async void OnChooseSettingsFolder(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        if (await shell.PickFolderAsync() is { } path)
        {
            await model.ChooseAsync(path);
            RenderSettingsData();
        }
    });

    private async void OnSaveFolder(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        await model.SaveDataAsync();
        RenderSettingsData();
    });

    private void OnCancelFolder(object sender, RoutedEventArgs args)
    {
        if (shell.IsBusy)
            return;
        model.CancelDataChange();
        shell.ClearProblem();
        RenderSettingsData();
    }

    private void RenderSettingsData()
    {
        var data = model.Data;
        SettingsDataStatus.Text = DataSummary(data);
        SettingsDataDetail.Text = data.Problem ?? (data.State == SetupDataState.Found ? SetupSession.FoundSummary : "");
        SettingsDataDetail.Visibility = IsVisible(SettingsDataDetail.Text.Length > 0);
        SettingsDataPath.Text = data.Path;
        SettingsDataPath.Visibility = IsVisible(data.Path.Length > 0);
        SettingsDataActions.Visibility = IsVisible(model.HasPendingData);
        SaveFolder.IsEnabled = data.State == SetupDataState.Found;
    }

    private void SyncChoices(GameSettings settings)
    {
        updatingSettings = true;
        try
        {
            EditionChoice.SelectedIndex = settings.Engine == GameEdition.Original ? 1 : 0;
            ThemeChoice.SelectedIndex = (int)settings.Appearance;
        }
        finally { updatingSettings = false; }
    }

    private async void OnSettingsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (updatingSettings || ThemeChoice.SelectedIndex < 0 || EditionChoice.SelectedIndex < 0)
            return;
        var settings = model.Settings;
        if (shell.IsBusy)
        {
            SyncChoices(settings.Working);
            return;
        }
        var choice = settings.Working with
        {
            Engine = EditionChoice.SelectedIndex == 1 ? GameEdition.Original : GameEdition.Cod2x,
            Appearance = (LauncherAppearance)ThemeChoice.SelectedIndex
        };
        await shell.RunAsync(async () =>
        {
            try { await model.SaveSettingsAsync(choice); }
            catch
            {
                SyncChoices(settings.Working);
                throw;
            }
            shell.ApplyAppearance(choice.Appearance);
            SyncChoices(choice);
        });
    }

}
