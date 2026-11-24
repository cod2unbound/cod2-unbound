using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private sealed record CheckupResources(ConfigRules Rules, ConfigCheckup Engine);
    private sealed record CheckupMatches(ImmutableArray<MatchSummary> Records, string? Problem = null);
    private sealed record AppliedAdvice(string Title, ConfigCheckupReceipt Receipt);
    private readonly Dictionary<(string Profile, ConfigCatalogMode Mode, string Rules, ConfigTuneFor Tune), HashSet<string>> setAsideAdvice = [];
    private readonly Dictionary<string, List<AppliedAdvice>> appliedAdvice = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedAdvice = [];
    private Task<CheckupResources>? checkupResources;
    private Task<CheckupMatches>? checkupMatches;
    private ConfigCatalogMode? checkupMode;
    private ImmutableArray<string> checkupRules = [];
    private ConfigTuneFor checkupTune = ConfigTuneFor.Competitive;
    private string? checkupMatchId;
    private bool checkupShowRules;
    private readonly HashSet<ConfigCheckupArea> checkupDetails = [];
    private string? checkupFocus;
    private string? checkupExpertSetting;

    internal void OpenCheckupForMatch(string? matchId)
    {
#if UPDATE_FIXTURES
        matchId ??= Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_CHECKUP_MATCH");
#endif
        checkupMatchId = matchId;
        checkupMatches = null;
        studioTab = 2;
        ShowStudio();
        RenderLoadouts();
    }

    private void ShowCheckup(ConfigStudioState state, ConfigProfileRead profile)
    {
        var choices = Enum.GetValues<ConfigTuneFor>();
        var tune = new ComboBox { ItemsSource = choices.Select(ConfigCheckupStanding.TuneTitle).ToArray(),
            SelectedIndex = Array.IndexOf(choices, checkupTune), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(tune, "Tune for");
        var tuneLabel = CheckStrong("Tune for");
        tuneLabel.VerticalAlignment = VerticalAlignment.Center;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tuneLabel, tune } };
        var body = new StackPanel { Spacing = 16 };
        var mode = model.Dialect == ConfigDialect.Cod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock;
        int request = 0;
        tune.SelectionChanged += async (_, _) =>
        {
            if (tune.SelectedIndex < 0)
                return;
            checkupTune = choices[tune.SelectedIndex];
            expandedAdvice.Clear();
            await DrawAsync(showProgress: false);
        };
        body.Loaded += ReadOnLoad;
        if (studioHeaderExtra is not null)
            studioHeaderExtra.Content = header;
        studioBody.Children.Add(body);

        async void ReadOnLoad(object sender, RoutedEventArgs args)
        {
            body.Loaded -= ReadOnLoad;
            await DrawAsync(showProgress: true);
        }

        async Task DrawAsync(bool showProgress = false)
        {
            int generation = ++request;
            if (showProgress)
            {
                body.Children.Clear();
                body.Children.Add(Progress("Reviewing this loadout…"));
            }

#if UPDATE_FIXTURES
            string? fixtureState = Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_CHECKUP_STATE");
            if (fixtureState == "reading")
                return;
#endif
            try
            {
#if UPDATE_FIXTURES
                if (fixtureState == "unavailable")
                    throw new InvalidDataException();
#endif
                var resourcesTask = checkupResources ??= Task.Run(LoadCheckupResources);
                var matchesTask = checkupMatches ??= Task.Run(ReadCheckupMatches);
                var resources = await resourcesTask;
                var matches = await matchesTask;
                if (!StillCurrent())
                    return;

                var policies = resources.Rules.Rulesets;
                InitializeCheckupRules(mode, policies);
                string[] titles = policies.Where(item => checkupRules.Contains(item.Id)).Select(item => item.Title).ToArray();
                string rulesTitle = titles.Length == 0 ? "No rules" : string.Join(" + ", titles);
                var scope = (profile.Profile.Id, mode, string.Join(",", checkupRules.Order(StringComparer.Ordinal)), checkupTune);
                var dismissed = setAsideAdvice.GetValueOrDefault(scope);
                var match = ConfigCheckupMatch.Select(matches.Records, checkupMatchId);
                var rulesetIds = checkupRules;
                var tuning = checkupTune;
                var excluded = dismissed?.ToHashSet();
                var report = await Task.Run(() => resources.Engine.Evaluate(profile, rulesetIds, tuning, mode, match.Measurements, excluded));
                if (!StillCurrent())
                    return;

                body.Children.Clear();
                DrawRules();
                DrawMatch();
                DrawAdvice();

                bool StillCurrent() => generation == request && body.Parent == studioBody && studioTab == 2;

                void DrawRules()
                {
                    var panel = new StackPanel { Spacing = 8 };
                    var menu = new MenuFlyout();
                    var rules = new DropDownButton { Content = rulesTitle, Flyout = menu, MaxWidth = 350 };
                    AutomationProperties.SetName(rules, $"Rules to check against: {rulesTitle}");
                    foreach (var policy in policies)
                    {
                        var item = new ToggleMenuFlyoutItem
                        {
                            Text = policy.Title, IsChecked = rulesetIds.Contains(policy.Id), IsEnabled = policy.Mode == mode
                        };
                        item.Click += async (_, _) =>
                        {
                            checkupRules = item.IsChecked ? checkupRules.Add(policy.Id) : checkupRules.Remove(policy.Id);
                            expandedAdvice.Clear();
                            checkupFocus = "rules";
                            await DrawAsync();
                        };
                        menu.Items.Add(item);
                    }
                    menu.Items.Add(new MenuFlyoutSeparator());
                    menu.Items.Add(MenuItem("No rules", async () =>
                    {
                        checkupRules = [];
                        checkupShowRules = false;
                        expandedAdvice.Clear();
                        checkupFocus = "rules";
                        await DrawAsync();
                    }, !rulesetIds.IsEmpty));
                    var label = CheckStrong("Rules");
                    label.VerticalAlignment = VerticalAlignment.Center;
                    var pick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { label, rules } };
                    var standing = ConfigCheckupStanding.For(report, rulesTitle);
                    if (standing.Status is { } status)
                    {
                        var show = new HyperlinkButton { Content = checkupShowRules ? "Hide rules" : "Show rules" };
                        var explanations = CheckCard(CheckupPolicyRows(report, resources.Rules));
                        explanations.Visibility = IsVisible(checkupShowRules);
                        show.Click += (_, _) =>
                        {
                            checkupShowRules = !checkupShowRules;
                            explanations.Visibility = IsVisible(checkupShowRules);
                            show.Content = checkupShowRules ? "Hide rules" : "Show rules";
                        };
                        panel.Children.Add(CheckPair(pick, Buttons(CheckTone(status, standing.Tone), show), 725));
                        panel.Children.Add(explanations);
                    }
                    else
                        panel.Children.Add(pick);
                    body.Children.Add(panel);
                    if (checkupFocus == "rules")
                    {
                        rules.Focus(FocusState.Programmatic);
                        checkupFocus = null;
                    }
                }

                void DrawMatch()
                {
                    var words = new StackPanel { Spacing = 6 };
                    if (matches.Problem is { } problem)
                        words.Children.Add(CheckNotice(InfoBarSeverity.Error, "Couldn't read your match summaries", problem));
                    else if (match.Shown is not { } shown)
                    {
                        words.Children.Add(CheckStrong(model.MatchSummariesEnabled ? "No matches recorded yet" : "Get advice from your own games"));
                        if (model.MatchSummariesEnabled)
                            words.Children.Add(Caption("After your next match, the game saves a short summary on this PC and Check-up shows it here."));
                        else
                        {
                            words.Children.Add(Caption("Turn on local match summaries to see measurements from your next game."));
                            words.Children.Add(CheckupLink("Settings › Privacy", () => shell.OpenSettingsPaneAsync("Privacy")));
                        }
                    }
                    else
                    {
                        words.Children.Add(CheckStrong(match.Title!));
                        words.Children.Add(Caption("Measured by the game on this PC. It may have used a different loadout than the one shown here."));
                        if (match.RequestedMissing)
                            words.Children.Add(Caption("The match you opened is no longer recorded on this PC. This is your last match."));
                        if (!model.MatchSummariesEnabled)
                            words.Children.Add(Caption("Match summaries are off now, so new games aren't added."));
                        if (shown.FreezeSummary is { } freezes)
                            words.Children.Add(CheckTone(freezes, ConfigCheckupTone.Caution));
                        if (shown.FreezeTimes is { } hitches)
                            words.Children.Add(Caption(hitches));
                        string ended = DateTimeOffset.TryParse(shown.EndedAt, CultureInfo.InvariantCulture, out var date)
                            ? HistoryDate(date) : shown.EndedAt;
                        words.Children.Add(MonoCaption($"Ended {ended} · {ConfigCheckupMatch.Duration(shown.DurationSeconds)}"));
                        words.Children.Add(MonoCaption(match.Count == 1 ? "1 match recorded on this PC" : $"{match.Count} matches recorded on this PC"));
                    }
                    var refresh = Action("Refresh", async () =>
                    {
                        checkupMatches = null;
                        checkupFocus = "refresh";
                        await DrawAsync(showProgress: true);
                    });
                    refresh.Content = IconLabel("Refresh", "Refresh", 16);
                    AutomationProperties.SetName(refresh, "Refresh match summaries");
                    ToolTipService.SetToolTip(refresh, "Read the match summaries saved on this PC again.");
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh } };
                    if (checkupMatchId is not null)
                        buttons.Children.Insert(0, Action("Show last match", async () =>
                        {
                            checkupMatchId = null;
                            checkupFocus = "refresh";
                            await DrawAsync();
                        }));
                    buttons.HorizontalAlignment = HorizontalAlignment.Right;
                    body.Children.Add(CheckCard(CheckPair(words, buttons, 560, rightAuto: true)));
                    body.Children.Add(CheckupGauges([
                        match.Movement(), ConfigCheckupPresentation.Aim(profile, model.Catalog, mode),
                        match.Connection(), ConfigCheckupPresentation.Visibility(report, model.Catalog, rulesTitle)]));
                    if (checkupFocus == "refresh")
                    {
                        refresh.Focus(FocusState.Programmatic);
                        checkupFocus = null;
                    }
                }

                void DrawAdvice()
                {
                    if (report.UnresolvedScripts)
                    {
                        body.Children.Add(CheckNotice(InfoBarSeverity.Warning, "Check-up can't suggest changes for this loadout",
                            "Some lines run other files or commands that only the game can follow, or can't be read. Edit as Text shows them. Settings that clearly break the chosen rules are still shown."));
                    }
                    else
                    {
                        body.Children.Add(CheckNotice(InfoBarSeverity.Informational, "Advice from your settings and the rules you choose",
                            match.Shown is null
                                ? "No match has been measured yet, so nothing here claims how smooth your game ran or how steady your connection was."
                                : "The cards above are measured from one match. Suggestions come from your settings and the rules you choose."));
                    }

                    var receipts = appliedAdvice.GetValueOrDefault(profile.Profile.Id);
                    if (receipts is not null)
                    {
                        foreach (var item in receipts)
                        {
                            var undo = Action("Undo", async () =>
                            {
                                if (!CanEdit)
                                    return;
                                try { model.UndoCheckup(resources.Engine, item.Receipt, report); }
                                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                                {
                                    ShowStatus(InfoBarSeverity.Error, "Couldn't undo this suggestion", error.Message);
                                    return;
                                }
                                receipts.Remove(item);
                                await RefreshStudioAsync();
                                Announce($"Undid {item.Title}.");
                            });
                            undo.IsEnabled = CanEdit && item.Receipt.AfterVersion.Matches(profile.Version)
                                && state.History.Any(entry => entry.Id == item.Receipt.HistoryEntry.Id);
                            var applied = CheckNotice(InfoBarSeverity.Success, $"Applied: {item.Title}", "Saved to History.");
                            applied.ActionButton = undo;
                            body.Children.Add(applied);
                            if (checkupFocus == "undo" && undo.IsEnabled)
                            {
                                undo.Focus(FocusState.Programmatic);
                                checkupFocus = null;
                            }
                        }
                    }
                    for (int index = 0; index < report.Suggestions.Length; index++)
                    {
                        var suggestion = report.Suggestions[index];
                        body.Children.Add(CheckupSuggestion(suggestion, index == 0, async () =>
                        {
                            if (!CanEdit)
                                return;
                            try
                            {
                                var receipt = model.ApplyCheckup(resources.Engine, report, suggestion.Id);
                                if (!appliedAdvice.TryGetValue(receipt.ProfileId, out var local))
                                    appliedAdvice.Add(receipt.ProfileId, local = []);
                                local.Insert(0, new(CheckTimingCopy(suggestion.Title), receipt));
                            }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                            {
                                ShowStatus(InfoBarSeverity.Error, "Couldn't apply this suggestion", error.Message);
                                return;
                            }
                            expandedAdvice.Remove(suggestion.Id);
                            checkupFocus = "undo";
                            await RefreshStudioAsync();
                            Announce($"Applied {CheckTimingCopy(suggestion.Title)}. Saved to History.");
                        }, async () =>
                        {
                            if (!setAsideAdvice.TryGetValue(scope, out var hidden))
                                setAsideAdvice.Add(scope, hidden = []);
                            hidden.Add(suggestion.Id);
                            expandedAdvice.Remove(suggestion.Id);
                            checkupFocus = "showAgain";
                            await DrawAsync();
                        }));
                    }
                    if (report.Suggestions.IsEmpty && receipts is not { Count: > 0 } && !report.UnresolvedScripts)
                    {
                        var standing = ConfigCheckupStanding.For(report, rulesTitle);
                        body.Children.Add(CheckCard(new StackPanel { Spacing = 4, Children =
                        {
                            new TextBlock { Text = standing.EmptyTitle, Style = StyleFor("EmptyHeading") },
                            Caption(standing.EmptyDetail)
                        } }));
                    }
                    if (dismissed is { Count: > 0 })
                    {
                        var showAgain = CheckupLink("Show again", async () =>
                        {
                            setAsideAdvice.Remove(scope);
                            await DrawAsync();
                            tune.Focus(FocusState.Programmatic);
                        });
                        body.Children.Add(Buttons(Caption(dismissed.Count == 1 ? "1 suggestion set aside for now."
                            : $"{dismissed.Count} suggestions set aside for now."), showAgain));
                        if (checkupFocus == "showAgain")
                        {
                            showAgain.Focus(FocusState.Programmatic);
                            checkupFocus = null;
                        }
                    }
                    foreach (string limit in report.Limitations)
                        body.Children.Add(Caption(CheckTimingCopy(limit)));
                    body.Children.Add(Caption("Locked settings are never suggested."));
                    body.Children.Add(CheckupLink("Every change can be undone in History", () =>
                    {
                        studioTab = 3;
                        ShowStudio();
                        return Task.CompletedTask;
                    }));
                    body.Children.Add(CheckupLink("Want the exact settings? Open Expert", () => OpenCheckupExpertAsync(null)));
                }
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or JsonException)
            {
                if (generation == request && body.Parent == studioBody)
                {
                    body.Children.Clear();
                    body.Children.Add(CheckNotice(InfoBarSeverity.Error, "Check-up isn't available",
                        "Check-up's rules or advice data are missing or damaged. Reinstall CoD2 Unbound to restore them."));
                }
            }
        }
    }

    private void InitializeCheckupRules(ConfigCatalogMode mode, IEnumerable<ConfigRuleset> policies)
    {
        if (checkupMode == mode)
            return;

        var initial = policies.FirstOrDefault(item => item.Id == "public-cod2x" && item.Mode == mode) ??
            policies.FirstOrDefault(item => item.Mode == mode);
        checkupRules = initial is null ? [] : [initial.Id];
        checkupMode = mode;
        checkupShowRules = false;
        expandedAdvice.Clear();
    }

    private CheckupResources LoadCheckupResources()
    {
        var rules = ConfigRules.LoadBundled();
        return new(rules, new ConfigCheckup(model.Catalog, rules, ConfigOptimizer.LoadBundled()));
    }

    private CheckupMatches ReadCheckupMatches()
    {
        try { return new(model.ReadCheckupMatches()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new([], error.Message);
        }
    }

    private FrameworkElement CheckupPolicyRows(ConfigCheckupReport report, ConfigRules rules)
    {
        var content = new StackPanel { Spacing = 12 };
        foreach (var policy in report.Policy)
        {
            string title = CheckupTitle(policy.Setting);
            var line = new StackPanel { Spacing = 4, Children =
            {
                CheckStrong($"{title} · {policy.Value ?? "Not set"}"),
                CheckTone(ConfigCheckupPresentation.RuleStatus(policy.Status), ConfigCheckupPresentation.RuleTone(policy.Status)),
                Caption(CheckTimingCopy(policy.Reason))
            } };
            foreach (var source in policy.Sources.DistinctBy(item => item.SourceURL))
                line.Children.Add(CheckupSource(source));
            content.Children.Add(line);
        }
        foreach (var conflict in rules.Conflicts.Where(item => item.RulesetIDs.Any(report.RulesetIds.Contains)
                     && !item.RulesetIDs.All(report.RulesetIds.Contains)))
            content.Children.Add(CheckTone($"Elsewhere: {CheckTimingCopy(conflict.Reason)}", ConfigCheckupTone.Information));
        return content;
    }

    private FrameworkElement CheckupGauges(ImmutableArray<ConfigCheckupGauge> gauges)
    {
        var layout = Columns(12, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star),
            new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
        layout.RowSpacing = 12;
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int index = 0; index < gauges.Length; index++)
        {
            var gauge = gauges[index];
            var value = new TextBlock { Text = CheckTimingCopy(gauge.Value), Style = StyleFor("SectionTitle"), TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 6, Children =
            {
                CheckStrong(gauge.Area.ToString()), value, CheckTone(gauge.Status, gauge.Tone), Caption(CheckTimingCopy(gauge.Detail))
            } };
            if (gauge.Figures is { } figures)
            {
                var detail = Caption(CheckTimingCopy(figures));
                bool expanded = checkupDetails.Contains(gauge.Area);
                detail.Visibility = IsVisible(expanded);
                var show = new HyperlinkButton { Content = expanded ? "Hide Details" : "Show Details" };
                void NameLink() => AutomationProperties.SetName(show, $"{show.Content} for {gauge.Area}");
                NameLink();
                show.Click += (_, _) =>
                {
                    bool open = checkupDetails.Add(gauge.Area);
                    if (!open)
                        checkupDetails.Remove(gauge.Area);
                    detail.Visibility = IsVisible(open);
                    show.Content = open ? "Hide Details" : "Show Details";
                    NameLink();
                };
                content.Children.Add(show);
                content.Children.Add(detail);
            }
            var card = CheckCard(content);
            card.MinHeight = 132;
            Put(layout, index, card);
        }

        layout.SizeChanged += (_, args) =>
        {
            int columns = Math.Clamp((int)((args.NewSize.Width + 12) / 202), 1, gauges.Length);
            for (int index = 0; index < layout.ColumnDefinitions.Count; index++)
            {
                layout.ColumnDefinitions[index].Width = index < columns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                var card = (FrameworkElement)layout.Children[index];
                Grid.SetColumn(card, index % columns);
                Grid.SetRow(card, index / columns);
            }
        };
        return layout;
    }

    private FrameworkElement CheckupSuggestion(ConfigCheckupSuggestion suggestion, bool top, Func<Task> apply, Func<Task> dismiss)
    {
        string title = CheckTimingCopy(suggestion.Title);
        var card = new StackPanel { Spacing = 8 };
        if (top)
            card.Children.Add(CheckTone("Top suggestion", ConfigCheckupTone.Information));
        card.Children.Add(CheckStrong(title));
        card.Children.Add(Body(CheckTimingCopy(suggestion.Why)));
        card.Children.Add(Body("You'll notice: " + CheckTimingCopy(suggestion.Notice)));
        card.Children.Add(Caption(CheckTimingCopy(suggestion.Evidence.FirstOrDefault(item => item.Kind == ConfigEvidenceKind.Inference)?.Detail
            ?? "Based on your settings")));
        foreach (var source in suggestion.Sources.DistinctBy(item => item.SourceURL))
            card.Children.Add(CheckupSource(source));
        var exact = new StackPanel { Spacing = 4, Visibility = IsVisible(expandedAdvice.Contains(suggestion.Id)) };
        foreach (var change in suggestion.Changes)
        {
            string label = CheckupTitle(change.Setting);
            var open = CheckupLink("Open in Expert", () => OpenCheckupExpertAsync(change.Setting));
            AutomationProperties.SetName(open, $"Open {label} in Expert");
            exact.Children.Add(SettingRow(label, $"{change.OldValue ?? "Not set"} → {change.NewValue}", open, grouped: true));
        }
        var show = new HyperlinkButton { Content = expandedAdvice.Contains(suggestion.Id) ? "Hide" : "Show me" };
        AutomationProperties.SetName(show, $"Show or hide changes for {title}");
        show.Click += (_, _) =>
        {
            bool open = expandedAdvice.Add(suggestion.Id);
            if (!open)
                expandedAdvice.Remove(suggestion.Id);
            exact.Visibility = IsVisible(open);
            show.Content = open ? "Hide" : "Show me";
        };
        var later = CheckupLink("Not now", dismiss);
        AutomationProperties.SetName(later, $"Set aside {title}");
        var button = Action("Apply", apply);
        AutomationProperties.SetName(button, $"Apply {title}");
        button.IsEnabled = CanEdit;
        if (top)
            button.Style = StyleFor("LauncherAccentButton");
        card.Children.Add(Buttons(later, show, button));
        card.Children.Add(exact);
        return CheckCard(card);
    }

    private HyperlinkButton CheckupLink(string label, Func<Task> action)
    {
        var link = new HyperlinkButton { Content = label };
        link.Click += async (_, _) => await shell.RunAsync(action);
        return link;
    }

    private static FrameworkElement CheckupSource(ConfigRuleSource source)
    {
        if (Uri.TryCreate(source.SourceURL, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            var link = new HyperlinkButton { Content = $"{uri.Host} · checked {source.SourceDate}", NavigateUri = uri, Padding = new Thickness(0) };
            ToolTipService.SetToolTip(link, CheckTimingCopy(source.QuoteReference));
            return link;
        }
        return Caption($"CoD2 Unbound source · {CheckTimingCopy(source.QuoteReference)}");
    }

    private string CheckupTitle(string name) => name.Equals("com_maxfps", StringComparison.OrdinalIgnoreCase)
        ? "Game timing setting" : model.Catalog.Setting(name)?.Title ?? name;

    private Task OpenCheckupExpertAsync(string? setting)
    {
        studioTab = 0;
        expert = true;
        loadoutText = null;
        jumpSetting = null;
        checkupExpertSetting = setting;
        query = setting ?? "";
        if (setting is not null && model.Catalog.Setting(setting) is { } spec)
            category = spec.Group;
        ShowStudio();
        return Task.CompletedTask;
    }

    private bool IsCheckupTimingSetting(ConfigSettingSpec spec) =>
        expert && checkupExpertSetting == spec.Name && query == spec.Name &&
        spec.Name.Equals("com_maxfps", StringComparison.OrdinalIgnoreCase);

    private static ConfigSettingSpec CheckupTimingSpec(ConfigSettingSpec spec) => spec with
    {
        Setting = spec.Setting with
        {
            Title = "Game timing setting",
            Summary = "The timing limit saved in this loadout.",
            Control = spec.Setting.Control with { Unit = null }
        }
    };
}
