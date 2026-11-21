using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SettingsPage : Page
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly SettingsViewModel model;
    private CancellationTokenSource? saveDelay;
    private CancellationTokenSource? updateCheck;
    private (string Mode, WindowsRenderMode? Renderer)? previousDisplay;
    private string? advancedDraft;
    private string? advancedProblem;

    internal SettingsPage(MainWindow shell, WindowsLauncherSession session)
    {
        this.shell = shell;
        this.session = session;
        model = new(session);
        InitializeComponent();
        model.RefreshData();
        Panes.SelectedItem = Panes.Items[0];
    }

    private GameSettings Current => model.Settings.Working;

    private void OnPaneChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        Feedback.IsOpen = false;
        PaneScroll.ChangeView(null, 0, null);
        Render();
    }

    private void Render()
    {
        Fields.Children.Clear();
        switch (Panes.Items.IndexOf(Panes.SelectedItem))
        {
            case 0: General(); break;
            case 1: Display(); break;
            case 2: GameData(); break;
            case 3: Privacy(); break;
            case 4: Updates(); break;
            case 5: Advanced(); break;
            case 6: About(); break;
        }
    }

    private void General()
    {
        Group("Game");
        Choice("Game version", "Used when you open the game’s own menu. Joining a server uses the version that server runs.",
            ["CoD2x 1.4.6.8", "Original 1.3"], (int)Current.Engine,
            async value => await ChangeAsync(Current with { Engine = (GameEdition)value }));
        Switch("Minimize CoD2 Unbound while the game is running", "It comes back when you close the game.",
            Current.HideWhilePlaying, value => ChangeAsync(Current with { HideWhilePlaying = value }));
        Group("Launcher");
        Choice("Appearance", null, ["Use system setting", "Light", "Dark"], (int)Current.Appearance,
            async value => await ChangeAsync(Current with { Appearance = (LauncherAppearance)value }));
        Switch("Refresh the server list when Servers opens", "Off, nothing is contacted until you press Refresh.",
            Current.RefreshServersOnOpen, value => ChangeAsync(Current with { RefreshServersOnOpen = value }));
        var hint = new InfoBar
        {
            Title = "Looking for mouse, brightness, network, HUD or sound?",
            Message = "They live in your loadouts, so every loadout keeps its own.",
            IsOpen = true, IsClosable = false,
            ActionButton = Action("Open Configs", () => shell.NavigateAsync(LauncherPlace.Configs))
        };
        hint.Margin = new Thickness(0, 16, 0, 0);
        Fields.Children.Add(hint);
    }

    private void Display()
    {
        string[] modes = ["exclusive", "borderless", "windowed"];
        Group("Display");
        Choice("Mode", null, ["Full screen", "Borderless", "Windowed"], Current.Fullscreen == "spaces" ? 1 : Array.IndexOf(modes, Current.Fullscreen),
            async value => await ChangeAsync(Current with { Fullscreen = modes[value] }));
        Choice("Graphics", "Choose how the game draws on Windows.", ["Direct3D 9", "DirectX 7"], (int)model.Renderer,
            async value => await ChangeAsync(Current with { WindowsRenderer = (WindowsRenderMode)value }));
        Row("Resolution", "Every loadout keeps its own resolution.",
            Action("Open Configs", () => shell.NavigateAsync(LauncherPlace.Configs)));
        var restore = Action("Restore defaults", async () =>
        {
            previousDisplay = (Current.Fullscreen, Current.WindowsRenderer);
            var defaults = new GameSettings();
            await ChangeAsync(Current with { Fullscreen = defaults.Fullscreen, WindowsRenderer = null });
            Render();
            ShowFeedback("Display defaults restored.", InfoBarSeverity.Success);
        });
        restore.IsEnabled = Current.Fullscreen != new GameSettings().Fullscreen || model.Renderer != WindowsRenderMode.Direct3D9;
        if (previousDisplay is { } previous)
            Row("Applies the next time the game starts.", null, Buttons(restore, Action("Undo", async () =>
            {
                await ChangeAsync(Current with { Fullscreen = previous.Mode, WindowsRenderer = previous.Renderer });
                previousDisplay = null;
                Render();
            })));
        else
            Row("Applies the next time the game starts.", null, restore);
    }

    private void GameData()
    {
        var data = model.Data;
        Group("Game files");
        var choose = Action("Change…", async () =>
        {
            if (await shell.PickFolderAsync() is { } path)
            {
                await model.ChooseAsync(path);
                Render();
            }
        });
        choose.IsEnabled = !session.Game.State.Running;
        var reveal = Action("Show in File Explorer", () => OpenAsync(data.Path));
        reveal.IsEnabled = data.State == SetupDataState.Found;
        var dataActions = Buttons(reveal, choose);
        if (model.HasPendingData)
        {
            var use = Action("Use this folder", async () => { await model.SaveDataAsync(); Render(); });
            use.IsEnabled = data.State == SetupDataState.Found && !session.Game.State.Running;
            dataActions = Buttons(use, Action("Cancel", () =>
            {
                model.CancelDataChange();
                Render();
                return Task.CompletedTask;
            }));
        }
        Row(DataSummary(data), string.Join("\n", new[]
        {
            data.Path, data.Problem ?? (data.State == SetupDataState.Found ? SetupSession.FoundSummary : "")
        }.Where(line => line.Length != 0)), dataActions);
        Row("CD key", session.Setup.HasStoredKey ? "Verified · Stored only on this PC" : "No key stored.",
            Action(session.Setup.HasStoredKey ? "Replace…" : "Add key…", ReplaceKeyAsync));
        Group("Your files");
        Row("Loadouts, demos and screenshots", session.HomePath, Action("Show in File Explorer", () => OpenAsync(session.HomePath)));
        Row("Game versions installed", string.Join(" · ", new[]
        {
            session.HasClient(GameEdition.Cod2x) ? "CoD2x 1.4.6.8" : null,
            session.HasClient(GameEdition.Original) ? "Original 1.3" : null
        }.OfType<string>()));
    }

    private async Task ReplaceKeyAsync()
    {
        string? ending = await session.KeyEndingAsync();
        var password = new PasswordBox { Header = "CD key", PasswordRevealMode = PasswordRevealMode.Hidden };
        var content = new StackPanel { Spacing = 12 };
        if (ending is not null)
            content.Children.Add(Caption($"Stored key ends in {ending}."));
        content.Children.Add(password);
        var dialog = Dialog("CD key", content, ending is null ? "Add key" : "Replace key");
        dialog.IsPrimaryButtonEnabled = false;
        password.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = CdKey.IsValid(password.Password);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && await session.ReplaceKeyAsync(password.Password))
        {
            Render();
            ShowFeedback("CD key saved.", InfoBarSeverity.Success);
        }
        password.Password = "";
    }

    private void Privacy()
    {
        Group("Sharing");
        Switch("Send crash reports to CoD2 Unbound", "Record your choice for crash reports.",
            Current.SendCrashReports, value => ChangeAsync(Current with { SendCrashReports = value, CrashReportsAsked = true }));
        Switch("Send CoD2x crash reports to the CoD2x project", "Record your choice for reports about faults in CoD2x’s code.",
            Current.SendCoD2xReports, value => ChangeAsync(Current with { SendCoD2xReports = value }));
        Switch("Send usage data to CoD2 Unbound", "Record your choice for anonymous usage data.",
            Current.SendUsageData, value => ChangeAsync(Current with { SendUsageData = value }));
        Group("On this PC");
        Switch("Match summaries", "Kept on this PC for Check-up.", Current.MatchSummariesEnabled,
            value => ChangeAsync(Current with { MatchSummariesEnabled = value }));
        var delete = Action("Delete summaries…", async () =>
        {
            if (session.Game.State.Running || !await ConfirmAsync("Delete match summaries?", "Delete the match summaries on this PC?", "Delete summaries"))
                return;
            await session.DeleteMatchHistoryAsync();
            Render();
            ShowFeedback("Match summaries deleted.", InfoBarSeverity.Success);
        });
        delete.IsEnabled = !session.Game.State.Running && !session.ReadMatches().IsEmpty;
        Row("Saved match summaries", null, delete);
    }

    private void Updates()
    {
        var state = session.Updates.State;
        var check = Action(updateCheck is not null ? "Cancel" : state.FailedStage is null ? "Check now" : "Try again", CheckUpdatesAsync, exclusive: false);
        check.IsEnabled = session.CanCheckUpdates;
        Row($"CoD2 Unbound {session.Version}", updateCheck is not null ? "Checking for updates…" : state.Message, check);
        if (state.ReleaseUrl is { } release)
            Row($"Version {state.AvailableVersion}", null, Action("Open release page", () => OpenAsync(release.AbsoluteUri)));
        Switch("Check automatically", null, Current.CheckUpdatesAutomatically,
            value => ChangeAsync(Current with { CheckUpdatesAutomatically = value }));
    }

    internal void RefreshUpdates()
    {
        if (ReferenceEquals(Panes.SelectedItem, Panes.Items[4]))
            Render();
    }

    private async Task CheckUpdatesAsync()
    {
        if (updateCheck is { } running)
        {
            running.Cancel();
            return;
        }
        await shell.RunAsync(async () =>
        {
            using var request = new CancellationTokenSource();
            updateCheck = request;
            Render();
            try { await session.CheckUpdatesAsync(automatic: false, request.Token); }
            finally
            {
                updateCheck = null;
                Render();
            }
        });
    }

    private void Advanced()
    {
        Group("Additional game options");
        var draft = new TextBox
        {
            Text = advancedDraft ?? Current.Advanced, AcceptsReturn = true, MinHeight = 112,
            MaxHeight = 240, TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(draft, "Additional game options");
        var problem = Caption(advancedProblem ?? "");
        problem.Style = StyleFor("NegativeText");
        var clear = Action("Clear", () => { draft.Text = ""; return Task.CompletedTask; }, exclusive: false);
        clear.IsEnabled = draft.Text.Length > 0;
        draft.TextChanged += async (_, _) =>
        {
            clear.IsEnabled = draft.Text.Length > 0;
            string text = advancedDraft = draft.Text.ReplaceLineEndings("\n");
            await shell.ShowProblemAsync(async () =>
            {
                var review = await session.ReviewAdvancedAsync(text, Current);
                if (advancedDraft != text)
                    return;
                advancedProblem = review.Problem;
                problem.Text = review.Problem ?? (review.LoadoutOwnedLines.IsEmpty ? "" :
                    $"These lines are controlled by your loadout: {string.Join(", ", review.LoadoutOwnedLines)}.");
                if (review.Candidate is not null)
                    await ChangeAsync(Current with { Advanced = text });
            });
        };
        var editor = new StackPanel { Spacing = 8 };
        editor.Children.Add(Caption("Passed to the game when it starts, one setting and its value per line."));
        editor.Children.Add(draft);
        editor.Children.Add(problem);
        editor.Children.Add(clear);
        Fields.Children.Add(new Border { Style = StyleFor("FieldCard"), Child = editor });
        Group("Logs and reset");
        var logs = Action("Show in File Explorer", () => OpenAsync(session.LogsPath));
        logs.IsEnabled = Directory.Exists(session.LogsPath);
        var copyLog = Action("Copy last game log", async () =>
        {
            if (await session.ReadLatestLogAsync() is { } log)
            {
                var content = new DataPackage();
                content.SetText(log);
                Clipboard.SetContent(content);
                ShowFeedback("Copied the game log.", InfoBarSeverity.Success);
            }
        });
        copyLog.IsEnabled = logs.IsEnabled;
        Row("Logs", "Game logs stay on this PC.", Buttons(logs, copyLog));
        var warnings = Action("Reset warnings", async () =>
        {
            await ChangeAsync(Current with { DismissedWarnings = [] });
            Render();
        });
        warnings.IsEnabled = !Current.DismissedWarnings.IsEmpty;
        Row("Warnings", null, warnings);
        Row("Launcher settings", null, Action("Reset settings…", async () =>
        {
            if (!await ConfirmAsync("Reset launcher settings?",
                "Restore the original settings and keep your loadouts, demos, screenshots and CD key.", "Reset settings"))
                return;
            advancedDraft = advancedProblem = null;
            previousDisplay = null;
            model.Settings.Reset(new GameSettings());
            await ChangeAsync(Current);
            Render();
            ShowFeedback("Settings are back to how they started.", InfoBarSeverity.Success);
        }));
    }

    private void About()
    {
        Row($"CoD2 Unbound {session.Version}",
            "Call of Duty 2 multiplayer, on every platform. Free and open source. You bring your own copy of the game.");
        Row("Release notes", null, Action("Release notes for every version", () => OpenAsync("https://github.com/cod2unbound/cod2-unbound/releases")));
        Row("Support", null, Buttons(Action("Website", () => OpenAsync("https://github.com/cod2unbound/cod2-unbound")),
            Action("Report a problem…", () => OpenAsync("https://github.com/cod2unbound/cod2-unbound/issues"))));
        Row("Updates", null, Action("Check for updates…", () =>
        {
            Panes.SelectedItem = Panes.Items[4];
            return CheckUpdatesAsync();
        }, exclusive: false));
        Row("Call of Duty", "Call of Duty is a registered trademark of Activision Publishing, Inc. CoD2 Unbound is a community project. Bring your own game data to play.");
    }

    private async Task ChangeAsync(GameSettings settings)
    {
        model.Settings.Replace(settings);
        shell.ApplyAppearance(settings.Appearance);
        saveDelay?.Cancel();
        using var delay = new CancellationTokenSource();
        saveDelay = delay;
        try { await shell.ShowProblemAsync(() => model.Settings.SaveAfterQuietAsync(delay.Token)); }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(saveDelay, delay))
                saveDelay = null;
        }
    }

    private void Group(string name) => Fields.Children.Add(new TextBlock
    {
        Text = name, Style = StyleFor("BodyStrongTextBlockStyle"),
        Margin = new Thickness(0, Fields.Children.Count == 0 ? 4 : 16, 0, 4)
    });

    private void Row(string label, string? description = null, FrameworkElement? action = null) =>
        Fields.Children.Add(SettingRow(label, description, action));

    private void Choice(string label, string? description, string[] options, int selected, Func<int, Task> change)
    {
        var choice = new ComboBox { ItemsSource = options, SelectedIndex = selected, MinWidth = 180 };
        AutomationProperties.SetName(choice, label);
        choice.SelectionChanged += async (_, _) =>
        {
            if (choice.SelectedIndex >= 0)
                await change(choice.SelectedIndex);
        };
        Row(label, description, choice);
    }

    private void Switch(string label, string? description, bool value, Func<bool, Task> change)
    {
        var control = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsOn = value, MinWidth = 0 };
        AutomationProperties.SetName(control, label);
        control.Toggled += async (_, _) => await change(control.IsOn);
        Row(label, description, control);
    }

    private Button Action(string label, Func<Task> run, bool exclusive = true)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            if (exclusive)
                await shell.RunAsync(run);
            else
                await shell.ShowProblemAsync(run);
        };
        return button;
    }

    private void ShowFeedback(string message, InfoBarSeverity severity)
    {
        Feedback.Severity = severity;
        Feedback.Message = message;
        Feedback.IsOpen = true;
    }

    private ContentDialog Dialog(string title, object content, string primary) => new()
    {
        XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = title, Content = content, PrimaryButtonText = primary,
        CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
    };

    private async Task<bool> ConfirmAsync(string title, string message, string primary) =>
        await Dialog(title, Caption(message), primary).ShowAsync() == ContentDialogResult.Primary;
}
