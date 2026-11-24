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
    private GameSettings? previousDisplay;
    private string? advancedDraft;
    private string? advancedProblem;
    private int lastPane = -1;

    internal const string GameDataPane = "Game Data", UpdatesPane = "Updates";

    /// The chosen pane's name; the Settings window takes it as its title, as the Mac's does.
    internal event Action<string>? PaneChanged;
    internal string PaneTitle => (Panes.SelectedItem ?? Panes.Items[0]).Text;
    internal event Action? ContentResized;
    internal double HeaderHeight => Panes.ActualHeight + Panes.Margin.Top + Panes.Margin.Bottom + 1;
    internal double PaneContentHeight => PaneBody.ActualHeight;

    internal SettingsPage(MainWindow shell, WindowsLauncherSession session, string? openPane = null)
    {
        this.shell = shell;
        this.session = session;
        model = new(session);
        InitializeComponent();
        PaneBody.SizeChanged += (_, _) => ContentResized?.Invoke();
        ActualThemeChanged += (_, _) => UpdateProgressTheme();
        model.RefreshData();
        var initial = Panes.Items.FirstOrDefault(item => item.Text == openPane)
            ?? Panes.Items[Math.Clamp(Current.LastSettingsPane, 0, Panes.Items.Count - 1)];
        Panes.SelectedItem = initial;
        Loaded += (_, _) =>
        {
            if (Panes.SelectedItem is null)
                Panes.SelectedItem = initial;
        };
    }

    private GameSettings Current => model.Settings.Working;

    private async void OnPaneChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        Feedback.IsOpen = false;
        PaneScroll.ChangeView(null, 0, null);
        Render();
        PaneChanged?.Invoke(PaneTitle);
        int pane = Panes.Items.IndexOf(Panes.SelectedItem);
        if (pane >= 0 && Current.LastSettingsPane != pane)
            await ChangeAsync(Current with { LastSettingsPane = pane }, display: pane == 1);
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
        Choice("Appearance", null, ["Match System", "Light", "Dark"], (int)Current.Appearance,
            async value => await ChangeAsync(Current with { Appearance = (LauncherAppearance)value }));
        Switch("Refresh the server list when Servers opens", "Off, nothing is contacted until you press Refresh.",
            Current.RefreshServersOnOpen, value => ChangeAsync(Current with { RefreshServersOnOpen = value }));
        var hint = new InfoBar
        {
            Title = "Looking for mouse, brightness, network, HUD or sound?",
            Message = "They live in your loadouts, so every loadout keeps its own.",
            IsOpen = true, IsClosable = false,
            Style = StyleFor("SettingsNote"),
            ActionButton = Action("Open Configs", () => shell.NavigateAsync(LauncherPlace.Configs))
        };
        hint.Margin = new Thickness(0, 16, 0, 0);
        Fields.Children.Add(hint);
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
            var use = Action("Use This Folder", async () =>
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
        var changeKey = Action(session.Setup.HasStoredKey ? "Replace…" : "Enter key…", ReplaceKeyAsync);
        changeKey.IsEnabled = !session.ClientIsRunning;
        Row("CD key", session.Setup.HasStoredKey ? "Verified · Stored only on this PC" : "Not entered.", changeKey);
        Group("Your files");
        Row("Loadouts, demos and screenshots", session.HomePath, Action("Show in File Explorer", () => OpenAsync(session.HomePath)));
        var versions = new StackPanel { Spacing = 4 };
        AddEdition(GameEdition.Cod2x, "CoD2x 1.4.6.8");
        AddEdition(GameEdition.Original, "Original 1.3");
        if (versions.Children.Count > 0)
            Row("Game versions installed", null, versions);
        var setupAgain = Action("Set Up Again…", ConfirmSetupAgainAsync);
        setupAgain.IsEnabled = !session.ClientIsRunning && !session.SetupUpdating;
        Row("Set up again", "Walks through game data and CD key once more. Your loadouts, demos and screenshots stay. You can cancel at any step.",
            setupAgain);

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
        if (await shell.EditCdKeyAsync())
        {
            Render();
            ShowFeedback("CD key saved.", InfoBarSeverity.Success);
        }
    }

    private async Task ConfirmSetupAgainAsync()
    {
        var dialog = Dialog("Run setup again?",
            Body("You’ll go through game data and CD key. Your loadouts, demos and screenshots stay, and you can cancel at any step."),
            "Set up again");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await shell.OpenSetupAgainAsync(LauncherPlace.GameDataSettings);
    }

    private void Privacy()
    {
        Fields.Children.Add(new InfoBar
        {
            Message = "CoD2 Unbound has no account. Its crash reports and usage data stay off unless you turn them on. The game and the launcher also contact the services listed below.",
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational
        });
        var reports = session.Reports;
        Group("Sharing");
        if (reports.CrashAvailable)
        {
            var crash = PrivacySwitch(PrivacyPresentation.CrashSwitch, Current.SendCrashReports, "H", session.SetCrashAsync);
            Row(PrivacyPresentation.CrashSwitch,
                "If the game crashes: what failed and when, the game version, your Windows version and PC model, and the game’s report. Never your CD key or your Windows user name.",
                Buttons(PrivacyPresentation.PreviewButton(true, "F", shell, reports), crash));
        }
        Row(PrivacyPresentation.Cod2xSwitch,
            "Errors, crashes and freezes in CoD2x, sent by CoD2x to the people who make it. A report includes recent console text and CoD2x’s IDs for this PC. A crash or freeze report can add selected game settings including your player name, a list of files and folders in the game’s main folder, and samples of the game’s memory. Changes apply the next time CoD2x starts.",
            PrivacySwitch(PrivacyPresentation.Cod2xSwitch, Current.SendCoD2xReports, "X", session.SetCoD2xReportsAsync));
        if (reports.Usage.Available)
        {
            var usage = PrivacySwitch(PrivacyPresentation.UsageSwitch, reports.Usage.InstallNumber is not null, "U",
                value => session.SetUsageAsync(value, UsageSource.Settings));
            Row(PrivacyPresentation.UsageSwitch,
                "Which screens and features you use, how long games ran and the choices you pick, with your PC’s model and hardware, versions and country, to PostHog in the EU. Never your name, CD key, servers or configs.",
                Buttons(PrivacyPresentation.PreviewButton(false, "W", shell, reports), usage));
            if (reports.Usage.InstallNumber is { } id)
            {
                string full = id.ToString("D");
                var number = new TextBlock { Text = full[..8] + "…", Style = StyleFor("AddressText") };
                number.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetName(number, "Install number " + full);
                ToolTipService.SetToolTip(number, full);
                var reset = Action("Reset Number", async () => { await session.ResetUsageAsync(); Render(); });
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
        var delete = Action("Delete Summaries…", async () =>
        {
            int count = session.ReadMatches().Length;
            if (session.ClientIsRunning || count == 0 ||
                !await ConfirmAsync("Delete match summaries?",
                    count == 1 ? "Delete the one match summary on this PC?" : $"Delete all {count} match summaries on this PC?",
                    "Delete Summaries"))
                return;
            if (session.ClientIsRunning)
                return;
            await session.DeleteMatchHistoryAsync();
            Render();
            ShowFeedback("Match summaries deleted.", InfoBarSeverity.Success);
        });
        delete.Style = StyleFor("SubtleAction");
        delete.IsEnabled = !session.Game.State.Running && !session.ReadMatches().IsEmpty;
        Row("Saved match summaries", null, delete);

        Group("What CoD2 Unbound contacts");
        var contacts = new StackPanel();
        foreach (var (who, when) in new (string, string)[]
        {
            ("Master servers", "Activision’s and the CoD2x project’s, when the internet server list refreshes. The original game also asks Activision for its server list."),
            ("Activision’s key server", "When the game checks your key, as the original game does: your CD key, a hash of it, and whether the key may be checked anonymously."),
            ("Game servers", "The launcher asks listed servers, favorites and recent servers for their status when the list refreshes. The one you join gets your player name and game settings, and any server password you supplied. When you play with CoD2x, the server also gets a hash of your CD key, the CoD2x protocol version and CoD2x’s IDs for this PC."),
            ("Addresses a server names", "A server can name a web address for the game to download maps and mods from, and with CoD2x, one to upload the match recording to."),
            ("The CoD2x project", "CoD2x checks for updates when the game’s menu opens. It sends your CoD2x version, a hash of your CD key if available, CoD2x’s own install number and IDs for this PC, and a short description of the system: its version, edition and build, its compatibility-mode settings, the country in its region settings, and whether it runs in a virtual machine or as administrator. It also sends how many times the hardware has changed and, after a change, the previous hardware ID and the old and new hardware details, serial numbers included. This is how CoD2x’s own update check works. CoD2x can also download missing zPAM files a server needs from the project’s site."),
            ("GitHub", "When you check for or install updates, which also fetches the news on Home; when Configs loads player setups; when you download zPAM for a server you host. Nothing about you."),
            ("cod2unbound.com", "For updates and the news on Home when GitHub can’t be reached; nothing about you. When you choose Share setup: the cleaned config, the name you type, the game and app versions, and how many lines were left out and why. The setup is posted publicly on GitHub. Stop sharing sends the request number and its private token."),
            ("The certificate’s issuer", "When an update’s signature is verified, Windows asks whether it is still valid. Nothing about you."),
            ("Servers you host", "Only while one runs. Set to The Internet, it lists itself on the master servers, and its replies to status requests can include server details and the names, scores and pings of its players. When a player joins, it asks Activision’s key server to check their key, sending the player’s network address, the mod it runs, a one-time number for the check, whether anonymous checks are allowed, and the player’s key hash if it has one. A CoD2x server, even one for your network only, can ask the CoD2x project about new versions, with its CoD2x version and platform; one of these checks also lists any zPAM versions installed.")
        })
            PrivacyPresentation.AddContact(contacts, who, when);
        if (reports.CrashAvailable)
            PrivacyPresentation.AddContact(contacts, "Crash reports", "Only with the CoD2 Unbound crash reports switch on when you start the game and when it crashes. The launcher keeps track of reports Sentry has accepted to avoid sending them again.");
        PrivacyPresentation.AddContact(contacts, "CoD2x crash reports",
            "Only if the CoD2x crash reports switch was on when you started CoD2x. Error reports include the CoD2x version, what went wrong, where in CoD2x’s code, CoD2x’s install and hardware IDs for this PC, the system description, and recent log and console text. Crash and freeze reports can also include the time, the game’s threads and samples of its memory, the program’s state, executable and folder paths, loaded modules and their paths, selected game settings including your player name, and a list of files and folders in the game’s main folder with file sizes when available. CoD2x tries sending reports waiting from earlier runs the next time it starts with the switch on.");
        if (reports.Usage.Available)
            PrivacyPresentation.AddContact(contacts, "PostHog, in the EU", "While you use the launcher, only if usage data is on. Usage events and a random install number.");
        Fields.Children.Add(PrivacyPresentation.GroupCard(contacts));
        Fields.Children.Add(new HyperlinkButton { Content = "Read the full privacy notice",
            NavigateUri = new Uri("https://cod2unbound.com/privacy") });
    }

    private ToggleSwitch PrivacySwitch(string name, bool value, string key, Func<bool, Task> save)
    {
        var control = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsOn = value, MinWidth = 0, AccessKey = key };
        AutomationProperties.SetName(control, name);
        bool restoring = false;
        control.Toggled += async (_, _) =>
        {
            if (restoring || !control.IsEnabled)
                return;
            if (shell.IsBusy)
            {
                restoring = true;
                control.IsOn = value;
                restoring = false;
                return;
            }
            await shell.RunAsync(async () =>
            {
                control.IsEnabled = false;
                try { await save(control.IsOn); }
                finally
                {
                    control.IsEnabled = true;
                    if (Panes.SelectedItem == Panes.Items[3])
                        Render();
                }
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
        else if (state.Issue == UpdateIssue.CheckUnavailable)
        {
            title = "Couldn’t check for updates";
            message = state.Message;
            AddAction("Try Again");
        }
        else if (state.Issue == UpdateIssue.Refused)
        {
            title = $"CoD2 Unbound {version} wasn’t installed";
            message = $"This update didn’t pass CoD2 Unbound’s safety checks, so nothing was installed. You can get {version} from the website.";
            AddDownloadPage();
        }
        else if (state.Issue == UpdateIssue.ProtectedLocation)
        {
            message = $"This copy can’t update itself, because Windows protects the folder it’s installed in. Download {version} from the website and run it.";
            AddWhatsNew(version);
            AddDownloadPage();
        }
        else if (state.Issue == UpdateIssue.SetupFailed)
        {
            title = "The update didn’t finish.";
            message = session.InstallNeedsRepair ? "Run Setup again to repair CoD2 Unbound." :
                $"{current} is still installed and works.";
            AddAction("Try Again");
            AddDownloadPage();
        }
        else if (state.Issue == UpdateIssue.Interrupted)
        {
            message = "The download stopped before it finished.";
            AddAction("Try Again", install: true);
        }
        else if (state.Issue == UpdateIssue.Unexpected)
        {
            title = state.Offer is null ? installed : available;
            message = state.Message ?? "The update could not be completed.";
            AddAction("Try Again", install: state.Offer is not null);
            if (state.Offer is not null)
                AddDownloadPage();
        }
        else
        {
            switch (state.Phase)
            {
                case UpdatePhase.Checking:
                    title = installed;
                    message = "Checking for updates…";
                    showProgress = true;
                    AddAction("Check Now", enabled: false);
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
                    AddAction("Install and Relaunch", install: true, enabled: state.Hold is null, primary: state.Hold is null);
                    break;
                case UpdatePhase.Offered:
                    message = state.Message ?? $"You have {current}. Installing takes a few seconds and reopens the launcher.";
                    AddWhatsNew(version);
                    AddAction("Install and Relaunch", install: true, primary: true);
                    break;
                case UpdatePhase.Installed:
                    title = $"CoD2 Unbound is now {current}.";
                    message = checkedAt;
                    AddWhatsNew(current);
                    break;
                default:
                    title = state.WasChecked || checkedAt is not null ? $"CoD2 Unbound {current} is up to date" : installed;
                    message = checkedAt;
                    AddAction("Check Now");
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
        void AddDownloadPage() => AddLink("Open download page", state.ReleaseUrl ?? WindowsReleaseAssets.ReleasesPage);
        void AddWhatsNew(string release) =>
            actions.Children.Add(Action("What’s New", () => shell.ShowReleaseNotesAsync(release)));
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
        var copyLog = Action("Copy Last Game Log", async () =>
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
        var warnings = Action("Reset Warnings", async () =>
        {
            await ChangeAsync(Current with { DismissedWarnings = [] });
            Render();
        });
        warnings.IsEnabled = !Current.DismissedWarnings.IsEmpty;
        Row("Warnings", null, warnings);
        Row("Launcher settings", null, Action("Reset Settings…", async () =>
        {
            if (!await ConfirmAsync("Reset launcher settings?",
                "Restore the original settings and keep your loadouts, demos, screenshots and CD key.", "Reset Settings"))
                return;
            advancedDraft = advancedProblem = null;
            previousDisplay = null;
            model.Settings.Reset(new GameSettings());
            await ChangeAsync(Current);
            Render();
            ShowFeedback("Settings are back to how they started.", InfoBarSeverity.Success);
        }));
    }

    private async Task ChangeAsync(GameSettings settings, bool display = false)
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
                    if (!display)
                        throw;
                    RefreshDisplaySave(failed: true);
                    return;
                }
                if (display)
                    RefreshDisplaySave(failed: false);
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

    // The Mac draws a short set of choices as a segmented control (SettingsWin board).
    private void Choice(string label, string? description, string[] options, int selected, Func<int, Task> change)
    {
        var choice = Tabs(options, selected, index => _ = change(index));
        choice.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(choice, label);
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

    // A single decision is the Mac's alert, not a sheet (AllSheets, Alerts).
    private async Task<bool> ConfirmAsync(string title, string message, string primary) =>
        await Dialog(title, Caption(message), primary).AsAlert().ShowAsync() == ContentDialogResult.Primary;
}
