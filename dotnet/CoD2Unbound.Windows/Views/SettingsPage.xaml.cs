using System.Globalization;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SettingsPage : Page
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly SettingsViewModel model;
    private CancellationTokenSource? saveDelay;
    private Border? updateCard;
    private TextBlock? updateMessage;
    private ProgressBar? updateProgress;
    private LauncherUpdateState? renderedUpdate;
    private (string Mode, WindowsRenderMode? Renderer)? previousDisplay;
    private string? advancedDraft;
    private string? advancedProblem;
    private int lastPane = -1;

    internal SettingsPage(MainWindow shell, WindowsLauncherSession session, bool openGameData = false, bool openUpdates = false)
    {
        this.shell = shell;
        this.session = session;
        model = new(session);
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateProgressTheme();
        model.RefreshData();
        var initial = Panes.Items[openUpdates ? 4 : openGameData ? 2 : 0];
        Panes.SelectedItem = initial;
        Loaded += (_, _) =>
        {
            if (Panes.SelectedItem is null)
                Panes.SelectedItem = initial;
        };
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
        updateCard = null;
        int pane = Panes.Items.IndexOf(Panes.SelectedItem);
        if (pane >= 0 && lastPane != pane)
        {
            lastPane = pane;
            session.Opened(UsagePage.General + pane);
        }
        switch (pane)
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
            ActionButton = OpenConfigs()
        };
        hint.Margin = new Thickness(0, 16, 0, 0);
        Fields.Children.Add(hint);
    }

    private void Display()
    {
        string[] modes = ["exclusive", "borderless", "windowed"];
        Group("Display");
        Fields.Children.Add(Body("Applies the next time the game starts."));
        Choice("Mode", null, ["Full screen", "Borderless", "Windowed"], Current.Fullscreen == "spaces" ? 1 : Array.IndexOf(modes, Current.Fullscreen),
            async value => await ChangeAsync(Current with { Fullscreen = modes[value] }));
        Choice("Graphics", "Choose how the game draws on Windows.", ["Direct3D 9", "DirectX 7"], (int)model.Renderer,
            async value => await ChangeAsync(Current with { WindowsRenderer = (WindowsRenderMode)value }));
        Row("Resolution", "Every loadout keeps its own resolution.", OpenConfigs());
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
            Fields.Children.Add(Buttons(restore, Action("Undo", async () =>
            {
                await ChangeAsync(Current with { Fullscreen = previous.Mode, WindowsRenderer = previous.Renderer });
                previousDisplay = null;
                Render();
            })));
        else
            Fields.Children.Add(restore);
    }

    private Button OpenConfigs()
    {
        var button = Action("Open Configs", () => shell.NavigateAsync(LauncherPlace.Configs));
        button.Style = StyleFor("SubtleLinkAction");
        return button;
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
            var use = Action("Use this folder", async () =>
            {
                await model.SaveDataAsync();
                session.Record(new(UsageKind.SettingChanged, (int)UsageSetting.GameDataFolder));
                Render();
            });
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
        var versions = new StackPanel { Spacing = 4 };
        AddEdition(GameEdition.Cod2x, "CoD2x 1.4.6.8");
        AddEdition(GameEdition.Original, "Original 1.3");
        if (versions.Children.Count > 0)
            Row("Game versions installed", null, versions);

        void AddEdition(GameEdition edition, string name)
        {
            if (!session.HasClient(edition))
                return;
            var check = Fluent("Check", 14);
            check.Style = StyleFor("PositiveIcon");
            versions.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { check, new TextBlock { Text = name } } });
        }
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
        Fields.Children.Add(new InfoBar
        {
            Message = "CoD2 Unbound has no account and sends nothing about you unless you turn it on here.",
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational
        });
        var reports = session.Reports;
        if (reports.CrashAvailable || reports.Usage.Available)
            Group("Sharing");
        if (reports.CrashAvailable)
        {
            var crash = PrivacySwitch(PrivacyPresentation.CrashSwitch, Current.SendCrashReports, "H", session.SetCrashAsync);
            Row(PrivacyPresentation.CrashSwitch,
                "If the game crashes: what failed, your Windows version and PC model, and the game’s report. Never your CD key or your Windows user name.",
                Buttons(PrivacyPresentation.PreviewButton(true, "F", shell, reports), crash));
            const string cod2xName = "Send CoD2x crash reports to the CoD2x project";
            var cod2x = new ToggleSwitch { OffContent = "Off", IsEnabled = false, MinWidth = 0 };
            AutomationProperties.SetName(cod2x, cod2xName);
            Row(cod2xName, "CoD2x’s own crash service is separate. This Windows launcher keeps those diagnostics on this PC.", cod2x);
        }
        if (reports.Usage.Available)
        {
            var usage = PrivacySwitch(PrivacyPresentation.UsageSwitch, reports.Usage.InstallNumber is not null, "U",
                value => session.SetUsageAsync(value, UsageSource.Settings));
            Row(PrivacyPresentation.UsageSwitch,
                "Which screens and features you use, with your PC’s model, versions and country, to PostHog in the EU. Never your name, CD key, servers or configs.",
                Buttons(PrivacyPresentation.PreviewButton(false, "W", shell, reports), usage));
            if (reports.Usage.InstallNumber is { } id)
            {
                string full = id.ToString("D");
                var number = new TextBlock { Text = full[..8] + "…", Style = StyleFor("AddressText") };
                number.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetName(number, "Install number " + full);
                ToolTipService.SetToolTip(number, full);
                var reset = Action("Reset number", async () => { await session.ResetUsageAsync(); Render(); });
                reset.Style = StyleFor("SubtleAction");
                reset.AccessKey = "R";
                AutomationProperties.SetName(reset, "Reset number");
                ToolTipService.SetToolTip(reset, "Make a new number, so later sessions don’t link to earlier ones");
                Row("Install number", PrivacyPresentation.NumberDetail, Buttons(number, reset));
            }
        }
        Group("On this PC");
        Switch("Match summaries", "Notes on how each match ran, so Check-up can advise you. Kept on this PC, the last 200 matches.", Current.MatchSummariesEnabled,
            value => ChangeAsync(Current with { MatchSummariesEnabled = value }));
        var delete = Action("Delete summaries…", async () =>
        {
            if (session.Game.State.Running || !await ConfirmAsync("Delete match summaries?", "Delete the match summaries on this PC?", "Delete summaries"))
                return;
            await session.DeleteMatchHistoryAsync();
            Render();
            ShowFeedback("Match summaries deleted.", InfoBarSeverity.Success);
        });
        delete.Style = StyleFor("SubtleAction");
        delete.IsEnabled = !session.Game.State.Running && !session.ReadMatches().IsEmpty;
        Row("Saved match summaries", null, delete);

        Group("What CoD2 Unbound contacts");
        var contacts = new StackPanel { Spacing = 12 };
        foreach (var (who, when) in new (string, string)[]
        {
            ("Master servers", "Activision’s and the CoD2x project’s, when the server list refreshes. The game also asks them and the key server, as the original does."),
            ("Game servers", "Those in the list when it refreshes, and the one you join. CoD2x servers also get a hash of CoD2x’s ID for this PC."),
            ("The CoD2x project", "With CoD2x, its update check each time the game’s menu opens: your CoD2x version, a hash of your CD key, CoD2x’s IDs for this PC and a short description of it. Mod files a server needs come from its site."),
            ("GitHub", "When you check for or install updates, which also fetches the news on Home; when Configs loads player setups; when you download zPAM for a server you host. Nothing about you."),
            ("The certificate’s issuer", "When an update’s signature is verified, Windows asks whether it is still valid. Nothing about you."),
            ("Servers you host", "Only while one runs. Set to The Internet, it lists itself on the master servers. A CoD2x server asks the CoD2x project about new versions.")
        })
            contacts.Children.Add(PrivacyPresentation.TextRow(who, when, 190));
        if (reports.CrashAvailable)
            contacts.Children.Add(PrivacyPresentation.TextRow("Crash reports", "Only with the crash reports switch above on, once for each crash.", 190));
        if (reports.Usage.Available)
            contacts.Children.Add(PrivacyPresentation.TextRow("PostHog, in the EU", "While you use the launcher, only if usage data is on. What you open and use, never who you are.", 190));
        Fields.Children.Add(new Border { Style = StyleFor("FieldCard"), Child = contacts });
    }

    private ToggleSwitch PrivacySwitch(string name, bool value, string key, Func<bool, Task> save)
    {
        var control = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsOn = value, MinWidth = 0, AccessKey = key };
        AutomationProperties.SetName(control, name);
        control.Toggled += async (_, _) =>
        {
            if (!control.IsEnabled)
                return;
            if (shell.IsBusy)
            {
                control.IsEnabled = false;
                control.IsOn = value;
                control.IsEnabled = true;
                return;
            }
            control.IsEnabled = false;
            await shell.RunAsync(async () =>
            {
                try { await save(control.IsOn); }
                finally { if (Panes.SelectedItem == Panes.Items[3]) Render(); }
            });
        };
        return control;
    }

    private void Updates()
    {
        updateCard = new Border { Style = StyleFor("FieldCard"), Padding = new Thickness(20, 16, 20, 16) };
        Fields.Children.Add(updateCard);
        RenderUpdate(session.Updates.State);
        Switch("Check for updates automatically", "Once a day, when the launcher is open.", Current.CheckUpdatesAutomatically,
            value => ChangeAsync(Current with { CheckUpdatesAutomatically = value }));
    }

    private void RenderUpdate(LauncherUpdateState state)
    {
        renderedUpdate = state;
        updateProgress = null;
        string current = session.Version;
        string version = state.AvailableVersion ?? current;
        string installed = $"CoD2 Unbound {current} is installed";
        string available = $"CoD2 Unbound {version} is available";
        string? checkedAt = LastChecked(state.CheckedAt ?? session.Library.DispatchesCheckedAt);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        string title = available;
        string? message = null;
        bool showProgress = false;

        if (session.SetupUpdating || state.Phase == UpdatePhase.Installing)
        {
            title = session.SetupUpdating ? "Installing the update…" : $"Installing {version}…";
            message = "The launcher reopens when it’s done.";
            showProgress = true;
        }
        else if (state.Issue == UpdateIssue.Refused)
        {
            title = $"CoD2 Unbound {version} wasn’t installed";
            message = $"This update didn’t pass CoD2 Unbound’s safety checks, so nothing was installed. You can get {version} from the website.";
            AddLink("Open download page", WindowsReleaseAssets.ReleasesPage);
        }
        else if (state.Issue == UpdateIssue.ProtectedLocation)
        {
            message = $"This copy can’t update itself, because Windows protects the folder it’s installed in. Download {version} from the website and run it.";
            AddWhatsNew(version);
            AddLink("Open download page", WindowsReleaseAssets.ReleasesPage);
        }
        else if (state.Issue == UpdateIssue.SetupFailed)
        {
            title = "The update didn’t finish.";
            message = session.InstallNeedsRepair ? "Run Setup again to repair CoD2 Unbound." :
                $"{current} is still installed and works.";
            AddAction("Try again");
            AddLink("Open download page", WindowsReleaseAssets.ReleasesPage);
        }
        else if (state.Issue == UpdateIssue.Interrupted)
        {
            message = "The download stopped before it finished.";
            AddAction("Try again", install: true);
        }
        else if (state.Issue == UpdateIssue.Unexpected)
        {
            title = state.Offer is null ? installed : available;
            message = state.Message ?? "The update could not be completed.";
            AddAction("Try again", install: state.Offer is not null);
            if (state.Offer is not null)
                AddLink("Open download page", WindowsReleaseAssets.ReleasesPage);
        }
        else
        {
            switch (state.Phase)
            {
                case UpdatePhase.Checking:
                    title = installed;
                    message = "Checking for updates…";
                    showProgress = true;
                    AddAction("Check now", enabled: false);
                    break;
                case UpdatePhase.Downloading:
                    message = DownloadMessage(state);
                    showProgress = true;
                    AddAction("Cancel");
                    break;
                case UpdatePhase.Verifying:
                    message = "Checking the download is genuine…";
                    showProgress = true;
                    AddAction("Cancel");
                    break;
                case UpdatePhase.Ready:
                    title = $"CoD2 Unbound {version} is ready to install";
                    message = state.Hold is not null
                        ? "Close Call of Duty 2 to install it. The update waits until then."
                        : $"Downloaded, {Megabytes(state.Offer!.Archive.Size)} MB. Installing takes a few seconds and reopens the launcher.";
                    AddWhatsNew(version);
                    AddAction("Install and relaunch", install: true, enabled: state.Hold is null, primary: state.Hold is null);
                    break;
                case UpdatePhase.Offered:
                    message = state.Message ?? $"You have {current}. Installing takes a few seconds and reopens the launcher.";
                    AddWhatsNew(version);
                    AddAction("Install and relaunch", install: true, primary: true);
                    break;
                case UpdatePhase.Installed:
                    title = $"CoD2 Unbound is now {current}.";
                    message = checkedAt;
                    AddWhatsNew(current);
                    break;
                default:
                    title = state.WasChecked || checkedAt is not null ? $"CoD2 Unbound {current} is up to date" : installed;
                    message = checkedAt;
                    AddAction("Check now");
                    break;
            }
        }

        var layout = Columns(16, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(layout, 0, new Image
        {
            Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "mark-48.png"))),
            Width = 48, Height = 48, VerticalAlignment = VerticalAlignment.Center
        });
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, Style = StyleFor("CardTitle"), FontSize = 16, TextWrapping = TextWrapping.Wrap });
        updateMessage = message is null ? null : Body(message);
        if (updateMessage is not null)
            words.Children.Add(updateMessage);
        if (showProgress)
        {
            updateProgress = new ProgressBar { Height = 3, Margin = new Thickness(0, 8, 0, 0),
                IsIndeterminate = state.ProgressPercent is null, Value = state.ProgressPercent ?? 0 };
            AutomationProperties.SetName(updateProgress, "Update progress");
            UpdateProgressTheme();
            words.Children.Add(updateProgress);
        }
        Put(layout, 1, words);
        if (actions.Children.Count != 0)
            Put(layout, 2, actions);
        updateCard!.Child = layout;

        void AddAction(string label, bool install = false, bool enabled = true, bool primary = false)
        {
            var action = Action(label, () => shell.RunUpdateAsync(install), exclusive: false);
            action.IsEnabled = enabled && session.CanCheckUpdates;
            if (primary)
                action.Style = StyleFor("LauncherAccentButton");
            actions.Children.Add(action);
        }

        void AddLink(string label, Uri target) => actions.Children.Add(Action(label, () => OpenAsync(target.AbsoluteUri)));
        void AddWhatsNew(string release) => AddLink("What’s new", WindowsReleaseAssets.NotesPage(release));
    }

    private static string? LastChecked(DateTimeOffset? time)
    {
        if (time is null)
            return null;
        var local = time.Value.ToLocalTime();
        return local.Date == DateTime.Today ? $"Last checked today at {local:t}." : $"Last checked {local:d} at {local:t}.";
    }

    private static string Megabytes(long size) => Math.Round(size / 1048576d).ToString(CultureInfo.CurrentCulture);
    private static string DownloadMessage(LauncherUpdateState state) =>
        $"Downloading {state.AvailableVersion}… {Megabytes(state.DownloadedBytes)} of {Megabytes(state.Offer!.Archive.Size)} MB";

    private void UpdateProgressTheme()
    {
        if (updateProgress is null)
            return;
        if (shell.IsHighContrast)
            updateProgress.ClearValue(ProgressBar.ForegroundProperty);
        else
            updateProgress.Foreground = ThemeBrush("LauncherSelectionPillBrush", ActualTheme, false);
    }

    internal void UpdateContrast() => UpdateProgressTheme();

    internal void RefreshUpdates(LauncherUpdateState state)
    {
        if (updateCard is null || state == renderedUpdate)
            return;
        if (state.Phase == UpdatePhase.Downloading && renderedUpdate?.Phase == UpdatePhase.Downloading && updateProgress is not null)
        {
            renderedUpdate = state;
            updateProgress.Value = state.ProgressPercent ?? 0;
            if (updateMessage is not null)
                updateMessage.Text = DownloadMessage(state);
            return;
        }
        RenderUpdate(state);
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
            return shell.RunUpdateAsync(install: false);
        }, exclusive: false));
        Row("Call of Duty", "Call of Duty is a registered trademark of Activision Publishing, Inc. CoD2 Unbound is a community project. Bring your own game data to play.");
    }

    private async Task ChangeAsync(GameSettings settings)
    {
        var before = model.Settings.Saved;
        model.Settings.Replace(settings);
        shell.ApplyAppearance(settings.Appearance);
        saveDelay?.Cancel();
        using var delay = new CancellationTokenSource();
        saveDelay = delay;
        try
        {
            await shell.ShowProblemAsync(async () =>
            {
                try { await model.Settings.SaveAfterQuietAsync(delay.Token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    session.ShowError(UsageProblem.SaveSettings);
                    throw;
                }
                foreach (var item in UsageEvent.Changed(before, model.Settings.Saved))
                    session.Record(item);
            });
        }
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
