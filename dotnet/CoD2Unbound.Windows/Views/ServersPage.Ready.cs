using System.Collections.Immutable;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ServersPage
{
    private const string DiscardChanges = "Anything you change in the game during this connection is discarded when you leave.";
    private ServerReadySession? readyCheck;
    private GameServer? readyServer;
    private ConfigCatalogMode ReadyMode { get; set; }
    private bool readyExpanded;
    private bool readyRefreshing;
    private bool readyWasPlaying;
    private string? readyActiveName;
    private string? readyChosenName;

    private ServerReadySession? CurrentReadyCheck(string address) =>
        readyCheck?.State.Address == ServerBrowser.TryAddress(address) ? readyCheck : null;

    private void ClearReadyCheck()
    {
        readyServer = null;
        readyCheck = null;
        readyExpanded = false;
        readyActiveName = readyChosenName = null;
        DetailReady.Content = null;
    }

    internal void RefreshActiveLoadout()
    {
        ClearReadyCheck();
        RenderDetails();
    }

    private void UpdateReadyCheck(GameServer server)
    {
        bool playing = model.GameRunning;
        if (readyWasPlaying && !playing)
            ClearReadyCheck();
        readyWasPlaying = playing;

        if (!ReferenceEquals(server, readyServer))
        {
            bool changedServer = readyServer?.Address != server.Address;
            var edition = server.Fields.IsEmpty ? session.Settings.Working.Engine :
                server.IsCod2x ? GameEdition.Cod2x : GameEdition.Original;
            var mode = edition == GameEdition.Cod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock;
            if (changedServer || mode != ReadyMode)
                ClearReadyCheck();
            readyServer = server;
            ReadyMode = mode;
            try
            {
                readyCheck ??= session.OpenServerReadySession(edition);
                readyCheck.Evaluate(server.Address, server.Fields.IsEmpty ? null : server, mode);
                if (!playing || readyActiveName is null)
                    readyActiveName = readyCheck.ActiveProfile?.DisplayName;
                if (!playing || readyChosenName is null)
                    readyChosenName = readyCheck.ChosenProfile?.DisplayName;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                readyCheck = null;
            }
        }
        RenderReadyCheck();
    }

    private void UpdateJoinButton()
    {
        bool busy = refreshing is not null || readyRefreshing;
        JoinButton.IsEnabled = !busy;
        string label = refreshing is not null ? "Refreshing…" :
            readyRefreshing ? "Checking…" :
            model.GameRunning ? "Launch" :
            readyCheck is { ProfileId: not null, Overrides.IsEmpty: true } && readyChosenName is { } chosen
                ? $"Launch with {chosen}"
                : readyCheck?.State.Report?.Status == ConfigReadyStatus.NeedsChanges && readyCheck.Overrides.IsEmpty
                    ? "Launch Anyway" : "Launch";
        JoinButtonLabel.Text = label;
        string? name = (ServerList.SelectedItem as ServerRow)?.Name;
        AutomationProperties.SetName(JoinButton, name is null ? label : $"{label} on {name}");
    }

    private void RenderReadyCheck(string? restoreFocus = null)
    {
        if (readyServer is null)
            return;

        var panel = new StackPanel { Spacing = 6 };
        string? tone = null;
        Button? focus = null;
        string active = readyActiveName ?? "Your active loadout";
        string chosen = readyChosenName ?? "this";
        ConfigProfile? alternative = null;
        var report = readyCheck?.State.Report;
        if (readyCheck is { } check)
        {
            try
            {
                alternative = check.CompatibleAlternative(ReadyMode);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report = null;
            }
        }

        if (readyCheck is { Overrides.IsEmpty: false } fixedCheck && report is not null)
        {
            tone = "PositiveBrush";
            panel.Children.Add(ReadyHeading("Fixed for this server only", tone));
            foreach (string line in FixValues(fixedCheck.Overrides))
                panel.Children.Add(MonoCaption(line));
            panel.Children.Add(Caption($"Your “{chosen}” loadout is unchanged. {DiscardChanges}"));
            foreach (var item in report.Details.Where(item => IsProblem(item) && !fixedCheck.Overrides.ContainsKey(item.Setting)))
                panel.Children.Add(Caption(RuleProblem(report, item)));
            focus = ReadyAction("Undo", UndoReadyChoiceAsync, "Undo the fix for this server");
            panel.Children.Add(focus);
        }
        else if (readyCheck?.ProfileId is not null && report is not null)
        {
            tone = "PositiveBrush";
            panel.Children.Add(ReadyHeading($"Using your “{chosen}” loadout for this game. {active} stays as it is.", tone));
            focus = ReadyAction("Undo", UndoReadyChoiceAsync, "Go back to your active loadout for this game");
            panel.Children.Add(focus);
            if (report.Status == ConfigReadyStatus.NeedsChanges)
                AddNeedsChanges(panel, report, alternative, chosen);
            else if (report.Status == ConfigReadyStatus.Unknown)
                AddUnknown(panel);
            if (report.Status != ConfigReadyStatus.Unknown)
                AddDetails(panel, report);
        }
        else if (report is null)
        {
            panel.Children.Add(ReadyHeading("We can’t check this server"));
            panel.Children.Add(Caption("Configs aren’t available, so we can’t compare your loadout with this server’s rules."));
        }
        else if (report.Status == ConfigReadyStatus.Unknown)
            focus = AddUnknown(panel);
        else
        {
            tone = report.Status == ConfigReadyStatus.NeedsChanges ? "AddressBrush" : "PositiveBrush";
            if (report.Status == ConfigReadyStatus.NeedsChanges)
                AddNeedsChanges(panel, report, alternative, chosen);
            else
                panel.Children.Add(ReadyHeading($"Your “{chosen}” loadout is ready for this server", tone));
            if (report.Details.Any(item => item.ServerOwned))
                panel.Children.Add(Caption("This server sets some of your settings while you play here. Your loadout doesn’t change."));
            AddDetails(panel, report);
        }

        bool contrast = shell.IsHighContrast;
        DetailReady.Content = new Border
        {
            Background = ThemeBrush("FieldBrush", ActualTheme, contrast),
            BorderBrush = ThemeBrush(tone ?? "ControlStrokeBrush", ActualTheme, contrast),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["CrateRadius"],
            Padding = new Thickness(10, 8, 10, 8),
            Child = panel
        };
        if (restoreFocus == "Undo")
            focus?.Focus(FocusState.Programmatic);
        else if (restoreFocus == "Launch")
            JoinButton.Focus(FocusState.Programmatic);
    }

    private void AddNeedsChanges(StackPanel panel, ConfigReadyReport report, ConfigProfile? alternative, string chosen)
    {
        var problems = report.Details.Where(IsProblem).ToArray();
        string changes = problems.Length == 1 ? "1 change" : $"{problems.Length} changes";
        panel.Children.Add(ReadyHeading($"Your “{chosen}” loadout needs {changes} here", "AddressBrush"));
        foreach (var problem in problems)
            panel.Children.Add(Caption(RuleProblem(report, problem)));

        if (!report.Fixes.IsEmpty)
            panel.Children.Add(ReadyAction("Fix for This Server Only", ConfirmReadyFixAsync, tooltip:
                "Use the server’s values while you play here only. Your loadout doesn’t change."));
        if (alternative is { } profile)
            panel.Children.Add(ReadyAction($"Use “{profile.DisplayName}”", () => ChangeReadyLoadoutAsync(profile.Id), tooltip:
                $"Your “{profile.DisplayName}” loadout already suits this server. Use it for this game only."));
    }

    private static bool IsProblem(ConfigRuleEvaluation item) =>
        item.Status is ConfigRuleStatus.Violation or ConfigRuleStatus.Warning or ConfigRuleStatus.Conflict;

    private Button AddUnknown(StackPanel panel)
    {
        panel.Children.Add(ReadyHeading("We don’t know this server’s rules"));
        panel.Children.Add(Caption("You can still launch. Ask the server again if you want to check its rules."));
        var button = ReadyAction(readyRefreshing ? "Checking…" : "Check again", CheckReadyAgainAsync, requiresStopped: false);
        button.IsEnabled = !readyRefreshing;
        panel.Children.Add(button);
        return button;
    }

    private void AddDetails(StackPanel panel, ConfigReadyReport report)
    {
        var link = new HyperlinkButton { Content = readyExpanded ? "Hide details" : "Details", Padding = new Thickness(0, 4, 0, 4) };
        var details = new StackPanel { Spacing = 4, Visibility = IsVisible(readyExpanded) };
        link.Click += (_, _) =>
        {
            readyExpanded = !readyExpanded;
            link.Content = readyExpanded ? "Hide details" : "Details";
            details.Visibility = IsVisible(readyExpanded);
        };
        string name = QuakeColors.Plain(readyServer!.Name);
        details.Children.Add(Caption(report.Status == ConfigReadyStatus.MatchesKnownRules
            ? $"Your saved settings match the rules listed for {name}. Only those settings are checked."
            : $"These are the rules we can check for {name}. The server can apply other rules during play."));
        foreach (string id in report.RulesetIds)
            details.Children.Add(MonoCaption($"Rules: {session.ServerRules.RuleSet(id)?.Title ?? id} ({id})"));
        foreach (var setting in report.Details)
        {
            if (setting.ServerOwned)
                details.Children.Add(Caption(RuleProblem(report, setting)));
            if (setting.Value is { } value)
                details.Children.Add(MonoCaption(setting.Setting + " = " + value));
        }
        foreach (var (key, value) in report.Facts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            details.Children.Add(MonoCaption(key + " = " + value));
        panel.Children.Add(link);
        panel.Children.Add(details);
    }

    private TextBlock ReadyHeading(string title, string color = "BodyBrush")
    {
        var label = Caption(title);
        label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        label.Foreground = ThemeBrush(color, ActualTheme, shell.IsHighContrast);
        AutomationProperties.SetLiveSetting(label, AutomationLiveSetting.Polite);
        return label;
    }

    private Button ReadyAction(string title, Func<Task> action, string? name = null, string? tooltip = null,
        bool requiresStopped = true)
    {
        bool blocked = requiresStopped && model.GameRunning;
        var button = new Button { Content = title, Style = StyleFor("SmallAction"), IsEnabled = !blocked };
        if (name is not null)
            AutomationProperties.SetName(button, name);
        if (blocked)
        {
            AutomationProperties.SetHelpText(button, ConfigProfileException.ReadOnlyWhilePlaying);
            ToolTipService.SetToolTip(button, ConfigProfileException.ReadOnlyWhilePlaying);
        }
        else if (tooltip is not null)
            ToolTipService.SetToolTip(button, tooltip);
        button.Click += async (_, _) => await action();
        return button;
    }

    private string RuleProblem(ConfigReadyReport report, ConfigRuleEvaluation problem)
    {
        if (problem.Setting == "wait")
            return "Binds that pause between commands won’t work here because this server turns pauses off.";
        if (problem.Setting == "com_maxfps")
        {
            var bounds = report.RulesetIds.SelectMany(id => session.ServerRules.ResolvedRules(id))
                .FirstOrDefault(rule => rule.Setting == problem.Setting)?.Constraint;
            if (bounds is { Minimum: { } min, Maximum: { } max } && problem.Value is { } value)
                return $"This loadout uses a limit of {value}. This server allows {min:0}–{max:0}.";
            return "This loadout’s display limit differs from the server setting.";
        }
        string title = session.ServerCatalog.Setting(problem.Setting)?.Title ?? problem.Setting;
        return $"{title}. {problem.Reason}";
    }

    private static IEnumerable<string> FixValues(ImmutableDictionary<string, string> values) =>
        values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + " = " + pair.Value);

    private Task CheckReadyAgainAsync() => shell.RunAsync(async () =>
    {
        if (readyCheck is not { } check || readyServer is not { } server)
            return;
        readyRefreshing = true;
        RenderReadyCheck();
        UpdateJoinButton();
        try
        {
            var mode = ReadyMode;
            await check.RefreshAsync(server.Address, mode);
            if (check.Observation is { } observation)
            {
                await session.RememberServerAsync(observation);
                Render();
                shell.RefreshFavorites();
            }
        }
        finally
        {
            readyRefreshing = false;
            RenderReadyCheck();
            UpdateJoinButton();
        }
    });

    private Task ConfirmReadyFixAsync() => shell.RunAsync(async () =>
    {
        if (session.ClientIsRunning || readyCheck is not { State.Report: { } report } check)
            return;
        var details = new StackPanel { Spacing = 8, Children = { Body("The game will use these values while you play here.") } };
        foreach (string line in FixValues(report.Fixes))
            details.Children.Add(MonoCaption(line));
        details.Children.Add(Body($"Your “{check.ChosenProfile?.DisplayName}” loadout is unchanged. {DiscardChanges}"));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "Fix for this server only?",
            PrimaryButtonText = "Fix for This Server Only", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, Content = details
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && ReferenceEquals(check, readyCheck) && !session.ClientIsRunning)
        {
            check.Fix();
            RenderReadyCheck("Undo");
            UpdateJoinButton();
        }
    });

    private Task UndoReadyChoiceAsync() => ChangeReadyLoadoutAsync(null);

    private Task ChangeReadyLoadoutAsync(string? id) => shell.RunAsync(() =>
    {
        if (session.ClientIsRunning || readyCheck is not { } check)
            return Task.CompletedTask;
        check.UseLoadout(id, ReadyMode);
        readyChosenName = check.ChosenProfile?.DisplayName;
        UpdateJoinButton();
        RenderReadyCheck(id is null ? "Launch" : "Undo");
        return Task.CompletedTask;
    });
}
