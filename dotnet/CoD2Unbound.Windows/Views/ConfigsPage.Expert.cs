using System.Text;
using CoD2Unbound.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using Windows.System;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private sealed record ExpertSource(string Name, string? LoadoutId = null, string? SetupId = null);
    private sealed record ExpertComparison(ExpertSource Source, ConfigProfileRead? Loadout,
        IReadOnlyDictionary<string, string>? Player);
    private ExpertComparison? comparison;

    private sealed class TextEdit(ConfigProfileRead source, string text)
    {
        internal ConfigProfileRead Source { get; } = source;
        internal string Original { get; } = text;
        internal string Text { get; set; } = text;
    }
    private TextEdit? loadoutText;
    private ConfigExpertFilter expertFilter;
    private AutoSuggestBox? settingSearch;
    private ConfigRules? expertRules;

    private void ClearSettingSearch()
    {
        query = "";
        jumpSetting = null;
        if (settingSearch is not null)
            settingSearch.Text = "";
    }

    private void AddExpertTools(ConfigProfileRead profile, Action renderRows)
    {
        var nobody = new ExpertSource("Nobody");
        var sources = new List<ExpertSource> { nobody };
        sources.AddRange(model.State.Profiles.Where(item => item.Id != profile.Profile.Id)
            .Select(item => new ExpertSource(item.DisplayName, LoadoutId: item.Id)));
        if (comparison?.Source.LoadoutId == profile.Profile.Id)
            comparison = null;
        if (comparison?.Source.SetupId is not null)
            sources.Add(comparison.Source);
        var choices = new ComboBox { DisplayMemberPath = "Name", ItemsSource = sources,
            SelectedItem = sources.FirstOrDefault(item => item == comparison?.Source) ?? nobody };
        AutomationProperties.SetName(choices, "Compare with");
        var marker = Caption("");
        bool textMode = loadoutText is { } text && text.Source.Profile.Id == profile.Profile.Id;
        var open = Action(textMode ? "Show Controls" : "Edit as Text", async () =>
        {
            if (textMode)
            {
                if (!await ConfirmLeaveTextAsync())
                    return;
                loadoutText = null;
            }
            else
                loadoutText = OpenText(model.Studio.ReadProfile(profile.Profile.Id));
            RenderStudioBody();
        });
        AutomationProperties.SetName(open, textMode ? "Show controls" : "Loadout as text…");
        var search = new AutoSuggestBox
        {
            PlaceholderText = "Find a setting", Text = query, QueryIcon = Fluent("Search", 18), MinWidth = 184,
            HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !textMode
        };
        settingSearch = search;
        studioFind = search;
        AutomationProperties.SetName(search, "Find a setting");
        search.TextChanged += (_, _) =>
        {
            if (query != search.Text)
                jumpSetting = null;
            query = search.Text;
            renderRows();
        };

        var comparisonTools = Buttons(Label("Compare with"), choices);
        var searchTools = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(searchTools, 0, search);
        Put(searchTools, 1, open);
        var ruleTools = ExpertRuleTools(profile, renderRows);
        var first = CheckPair(ruleTools, comparisonTools, 528, rightAuto: true);
        var tools = CheckPair(first, searchTools, 885, rightAuto: true);
        AutomationProperties.SetName(tools, "Expert tools");
        studioBody.Children.Add(tools);
        studioBody.Children.Add(marker);
        UpdateMarker();
        bool rebuilding = false;
        choices.Loaded += async (_, _) =>
        {
            try
            {
                var saved = await model.ReadSavedSetupsAsync();
                if (!choices.IsLoaded)
                    return;
                rebuilding = true;
                sources.RemoveAll(item => item.SetupId is not null);
                sources.AddRange(saved.Where(item => item.Entry is not null)
                    .Select(item => new ExpertSource(item.Entry!.Player, SetupId: item.Id)));
                if (comparison?.Source is { SetupId: not null } player && !sources.Contains(player))
                    sources.Add(player);
                choices.ItemsSource = sources.ToArray();
                choices.SelectedItem = sources.FirstOrDefault(item => item == comparison?.Source) ?? nobody;
                rebuilding = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ConfigDirectoryException)
            {
                marker.Text = "Saved player setups are unavailable.";
                marker.Visibility = Visibility.Visible;
            }
        };
        choices.SelectionChanged += async (_, _) =>
        {
            if (rebuilding || choices.SelectedItem is not ExpertSource requested || !choices.IsLoaded)
                return;
            comparison = null;
            UpdateMarker();
            renderRows();
            if (requested == nobody)
                return;
            try
            {
                var candidate = requested.LoadoutId is { } id ? new ExpertComparison(requested, model.Studio.ReadProfile(id), null) :
                    new ExpertComparison(requested, null, await model.ReadSetupSettingsAsync(requested.SetupId!));
                if (choices.IsLoaded && Equals(choices.SelectedItem, requested))
                {
                    comparison = candidate;
                    UpdateMarker();
                    renderRows();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ConfigDirectoryException)
            {
                if (Equals(choices.SelectedItem, requested))
                {
                    marker.Text = "This setup couldn’t be opened for comparison.";
                    marker.Visibility = Visibility.Visible;
                }
            }
        };

        static TextBlock Label(string text) => new()
        {
            Text = text, Style = StyleFor("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center
        };

        void UpdateMarker()
        {
            marker.Text = comparison is { } current ? $"◆ Compare markers show {current.Source.Name}’s values" : "";
            marker.Visibility = IsVisible(comparison is not null);
        }
    }

    private FrameworkElement ExpertRuleTools(ConfigProfileRead profile, Action renderRows)
    {
        var label = CheckStrong("Rules");
        label.VerticalAlignment = VerticalAlignment.Center;
        var rules = new Button { Content = "Loading…", IsEnabled = false };
        AutomationProperties.SetName(rules, "Rules to check against");
        var status = Caption("");
        status.VerticalAlignment = VerticalAlignment.Center;
        var controls = Buttons(label, rules, status);
        controls.Loaded += async (_, _) =>
        {
            try
            {
                expertRules = (await (checkupResources ??= Task.Run(LoadCheckupResources))).Rules;
                if (!controls.IsLoaded)
                    return;
                var policies = expertRules.Rulesets.Where(item => item.Mode == model.Model.Mode).ToArray();
                InitializeCheckupRules(model.Model.Mode, policies);
                var menu = new MenuFlyout();
                menu.Opening += (_, _) =>
                {
                    menu.Items.Clear();
                    menu.Items.Add(MenuItem("No Rules", () =>
                    {
                        checkupRules = [];
                        Update();
                        return Task.CompletedTask;
                    }));
                    foreach (var policy in policies)
                    {
                        var item = new ToggleMenuFlyoutItem { Text = policy.Title, IsChecked = checkupRules.Contains(policy.Id) };
                        item.Click += (_, _) =>
                        {
                            checkupRules = item.IsChecked ? checkupRules.Add(policy.Id) : checkupRules.Remove(policy.Id);
                            Update();
                        };
                        menu.Items.Add(item);
                    }
                };
                rules.Flyout = menu;
                rules.IsEnabled = true;
                Update();

                void Update()
                {
                    rules.Content = checkupRules.IsEmpty ? "No rules" :
                        string.Join(" + ", policies.Where(policy => checkupRules.Contains(policy.Id)).Select(policy => policy.Title));
                    int count = model.Model.Specs.Count(spec => RuleFor(profile, spec)?.Status == ConfigRuleStatus.Violation);
                    status.Text = checkupRules.IsEmpty ? "" : count == 0 ? "Follows these rules" :
                        count == 1 ? "1 setting breaks them" : $"{count} settings break them";
                    ColorCheckTone(status, count == 0 ? ConfigCheckupTone.Positive : ConfigCheckupTone.Negative);
                    renderRows();
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                if (controls.IsLoaded)
                    rules.Content = "Rules unavailable";
            }
        };
        return controls;
    }

    private ConfigRuleEvaluation? RuleFor(ConfigProfileRead profile, ConfigSettingSpec spec) =>
        checkupRules.IsEmpty ? null : expertRules?.Evaluate(spec.Name,
            ConfigStudioModel.State(spec, profile.Document, profile.Metadata).Value, checkupRules, model.Model.Mode);

    private FrameworkElement ExpertSetting(ConfigProfileRead profile, ConfigSettingSpec spec)
    {
        var state = model.State;
        var current = state.Current ?? profile;
        var setting = ConfigStudioModel.State(spec, current.Document, current.Metadata);
        var card = Setting(state, profile.Profile.Id, spec);
        var content = (Grid)card.Child;
        var details = (StackPanel)content.Children[0];
        if (details.Children[0] is TextBlock heading)
        {
            if (setting.Locked || setting.Changed)
            {
                string icon = setting.Locked ? "LockClosed" : "Check";
                var identity = IconLabel(icon, heading.Text, 13);
                AutomationProperties.SetName(identity, heading.Text +
                    (setting.Locked ? ", locked" : "") + (setting.Changed ? ", changed from default" : ""));
                details.Children.RemoveAt(0);
                details.Children.Insert(0, identity);
            }
            details.Children.Insert(1, MonoCaption(spec.Name));
        }
        if (RuleFor(current, spec) is { Status: ConfigRuleStatus.Violation or ConfigRuleStatus.Conflict } rule)
            details.Children.Add(CheckTone(CheckTimingCopy(rule.Reason), ConfigCheckupTone.Negative));
        if (setting.Note is { } note)
        {
            var text = Caption(note);
            text.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
            details.Children.Add(text);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (comparison is { } source)
        {
            var other = ConfigStudioModel.Compare(spec, setting, source.Loadout, source.Player);
            var compared = new StackPanel { Spacing = 4 };
            if (other?.Value is { } value)
            {
                compared.Children.Add(Caption($"◆ {other.Display}"));
                var take = Action("Take theirs", () =>
                {
                    var latest = ForBindingEdit(profile.Profile.Id);
                    model.Model.TakeTheirs(model.Studio, spec, latest.Version, value, source.Source.Name, expertRules, checkupRules, "the selected rules");
                    RenderStudioBody();
                    return Task.CompletedTask;
                });
                take.IsEnabled = model.Model.TakeTheirsBlock(spec, state, expertRules, checkupRules, "the selected rules") is null;
                AutomationProperties.SetName(take, $"Take {source.Source.Name}’s {spec.Title}, {other.Display}");
                compared.Children.Add(take);
            }
            else if (source.Player is null || source.Player.ContainsKey(spec.Name))
                compared.Children.Add(Caption("Same"));
            details.Children.Add(compared);
        }

        var reset = ExpertIcon("\uE72C", $"Reset {spec.Title} to default", () =>
        {
            ConfigExpertEdits.Reset(model.Model, model.Studio, ForBindingEdit(profile.Profile.Id), spec);
            RenderStudioBody();
            return Task.CompletedTask;
        });
        reset.IsEnabled = setting.Changed && model.Model.TakeTheirsBlock(spec, state, null, [], "") is null;
        var changeLock = ExpertIcon(setting.Locked ? "\uE785" : "\uE72E", $"{(setting.Locked ? "Unlock" : "Lock")} {spec.Title}",
            () => SaveMetadata(!setting.Locked, null));
        var noteButton = new Button { Style = StyleFor("IconAction"), Content = new FontIcon { Glyph = "\uE70F", FontSize = 14 } };
        string noteAction = $"{(setting.Note is null ? "Add" : "Edit")} the note on {spec.Title}";
        ToolTipService.SetToolTip(noteButton, noteAction);
        AutomationProperties.SetName(noteButton, noteAction);
        var field = new TextBox { Text = setting.Note ?? "", PlaceholderText = "Why it’s set this way", Width = 260, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(field, $"Note on {spec.Title}");
        var noteActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var notePanel = new StackPanel { Spacing = 8, Children = { Body($"Note on {spec.Title}"), field, noteActions } };
        var flyout = new Flyout { Content = notePanel };
        flyout.Opened += (_, _) => field.Focus(FocusState.Programmatic);
        if (setting.Note is not null)
            noteActions.Children.Add(Action("Remove note", () => { flyout.Hide(); return SaveMetadata(null, ""); }));
        var save = Action("Save note", () => { flyout.Hide(); return SaveMetadata(null, field.Text); });
        save.Style = StyleFor("LauncherAccentButton");
        noteActions.Children.Add(save);
        changeLock.IsEnabled = noteButton.IsEnabled = !state.ReadOnly;
        noteButton.Flyout = flyout;
        actions.Children.Add(Buttons(reset, changeLock, noteButton));
        if (content.Children.Count > 1 && content.Children[1] is FrameworkElement control)
        {
            content.Children.Remove(control);
            actions.Children.Insert(0, control);
            control.HorizontalAlignment = HorizontalAlignment.Right;
        }
        if (content.ColumnDefinitions.Count == 1)
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Put(content, 1, actions);
        int actionRow = content.RowDefinitions.Count;
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.SizeChanged += (_, args) =>
        {
            bool stacked = args.NewSize.Width < 540;
            content.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumn(actions, stacked ? 0 : 1);
            Grid.SetRow(actions, stacked ? actionRow : 0);
        };
        return card;

        Task SaveMetadata(bool? locked, string? note)
        {
            ConfigExpertEdits.EditMetadata(model.Studio, ForBindingEdit(profile.Profile.Id), spec, locked, note);
            RenderStudioBody();
            return Task.CompletedTask;
        }
    }

    private Button ExpertIcon(string glyph, string title, Func<Task> call)
    {
        var button = Action(title, call);
        ToolTipService.SetToolTip(button, title);
        AutomationProperties.SetName(button, title);
        button.Style = StyleFor("IconAction");
        button.Content = new FontIcon { Glyph = glyph, FontSize = 14 };
        return button;
    }

    private static TextEdit OpenText(ConfigProfileRead profile) =>
        new(profile, Encoding.Latin1.GetString(profile.Document.Serialized.AsSpan()).ReplaceLineEndings("\n"));

    private bool ShowExpertTextIfOpen(ConfigProfileRead profile)
    {
        if (loadoutText is not { } edit)
            return false;
        if (edit.Source.Profile.Id != profile.Profile.Id || edit.Source.Document.Dialect != profile.Document.Dialect)
        {
            loadoutText = null;
            return false;
        }

        var body = new StackPanel { Spacing = 8 };
        var count = Caption("");
        var revert = Action("Revert", () =>
        {
            model.Studio.Refresh();
            loadoutText = OpenText(model.Studio.ReadProfile(profile.Profile.Id));
            RenderStudioBody();
            return Task.CompletedTask;
        });
        var header = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        var heading = new StackPanel { Children = { new TextBlock { Text = "Loadout as text", Style = StyleFor("SectionTitle") }, count } };
        Put(header, 0, heading);
        Put(header, 1, revert);
        body.Children.Add(header);

        var stale = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false,
            Message = "This loadout changed on disk while you were editing. Revert to see the new version. Your text edits will be lost." };
        body.Children.Add(stale);
        var editor = new RichEditBox
        {
            Height = 266, FontFamily = new FontFamily("Consolas"), FontSize = 13,
            TextWrapping = TextWrapping.NoWrap, IsReadOnly = model.Studio.IsReadOnly, IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false, AcceptsReturn = true
        };
        AutomationProperties.SetName(editor, "Loadout as text");
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        editor.Document.SetText(TextSetOptions.None, edit.Text.ReplaceLineEndings("\r"));
        body.Children.Add(editor);

        var issues = new StackPanel { Spacing = 3 };
        var issueList = new ScrollViewer { Content = issues, MaxHeight = 132, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(issueList);
        var hint = Caption("");
        body.Children.Add(hint);
        bool onDisk = false;
        bool decorating = false;
        bool canApply = false;
        var apply = new Button { Content = "Apply text edits", Style = StyleFor("LauncherAccentButton") };
        apply.Click += async (_, _) => await shell.RunAsync(ApplyText);
        var saveKey = new KeyboardAccelerator { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control };
        saveKey.Invoked += async (_, args) =>
        {
            if (apply.IsEnabled)
            {
                args.Handled = true;
                await shell.RunAsync(ApplyText);
            }
        };
        body.KeyboardAccelerators.Add(saveKey);
        body.Children.Add(apply);
        studioBody.Children.Add(body);

        Task ApplyText()
        {
            if (DiskChanged())
            {
                RefreshStale();
                return Task.CompletedTask;
            }
            try
            {
                CommitExpertText(edit);
                loadoutText = OpenText(model.State.Current!);
                Feedback("Saved. Edited as text.");
                RenderStudioBody();
            }
            catch (ConfigProfileException error) when (error.Failure == ConfigProfileFailure.Conflict)
            {
                onDisk = true;
                stale.IsOpen = true;
                UpdateActions();
            }
            return Task.CompletedTask;
        }

        var update = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        update.Tick += (_, _) => { update.Stop(); Review(); };
        var diskCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        diskCheck.Tick += (_, _) => RefreshStale();
        editor.TextChanged += (_, _) =>
        {
            if (decorating)
                return;
            editor.Document.GetText(TextGetOptions.AllowFinalEop, out string raw);
            string text = raw.EndsWith('\r') ? raw[..^1] : raw;
            text = text.ReplaceLineEndings("\n");
            if (text == edit.Text)
                return;
            edit.Text = text;
            update.Stop();
            update.Start();
            canApply = false;
            UpdateActions();
        };
        editor.Loaded += (_, _) =>
        {
            editor.Focus(FocusState.Programmatic);
            RefreshStale();
            diskCheck.Start();
        };
        editor.Unloaded += (_, _) => { update.Stop(); diskCheck.Stop(); };
        Review();
        return true;

        void Review()
        {
            var review = ConfigTextCheck.Inspect(edit.Text, model.Dialect, model.Catalog, model.Model.Mode);
            int lines = edit.Text.Count(c => c == '\n') + 1;
            count.Text = lines == 1 ? "1 line" : $"{lines} lines";
            issueList.Visibility = IsVisible(!review.Issues.IsEmpty);
            issues.Children.Clear();
            decorating = true;
            editor.Document.BatchDisplayUpdates();
            try
            {
                var all = editor.Document.GetRange(0, edit.Text.Length);
                all.CharacterFormat.Underline = UnderlineType.None;
                all.CharacterFormat.ForegroundColor = (ThemeBrush("BodyBrush", ActualTheme, shell.IsHighContrast) as SolidColorBrush)?.Color
                    ?? Microsoft.UI.Colors.Black;
                foreach (var issue in review.Issues.OrderBy(issue => issue.Blocking))
                {
                    var range = editor.Document.GetRange(issue.Range.Start, Math.Min(issue.Range.End, edit.Text.Length));
                    range.CharacterFormat.Underline = UnderlineType.Wave;
                    if (ThemeBrush(issue.Blocking ? "NegativeBrush" : "GameYellowBrush", ActualTheme, shell.IsHighContrast) is SolidColorBrush ink)
                        range.CharacterFormat.ForegroundColor = ink.Color;
                }
                foreach (var issue in review.Issues)
                {
                    var link = new HyperlinkButton { Content = $"Line {issue.Line}, column {issue.Column}" };
                    AutomationProperties.SetName(link, $"Line {issue.Line}, column {issue.Column}. {issue.Message}");
                    link.Click += (_, _) =>
                    {
                        editor.Focus(FocusState.Programmatic);
                        editor.Document.Selection.SetRange(issue.Range.Start, Math.Min(issue.Range.End, edit.Text.Length));
                        editor.Document.Selection.ScrollIntoView(PointOptions.Start);
                    };
                    issues.Children.Add(new StackPanel { Spacing = 2, Children = { link, Caption(issue.Message) } });
                }
            }
            finally
            {
                editor.Document.ApplyDisplayUpdates();
                decorating = false;
            }
            canApply = review.CanApply;
            hint.Text = canApply ? "Edits apply to this loadout when you choose Apply text edits." :
                "Fix the underlined problems to apply your edits.";
            UpdateActions();
        }

        bool DiskChanged()
        {
            try { return !model.Studio.ReadProfile(profile.Profile.Id).Version.Matches(edit.Source.Version); }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        void RefreshStale()
        {
            onDisk = DiskChanged();
            stale.IsOpen = onDisk;
            UpdateActions();
        }

        void UpdateActions()
        {
            bool edited = edit.Text != edit.Original;
            revert.IsEnabled = edited || onDisk;
            apply.IsEnabled = edited && canApply && !onDisk && !model.Studio.IsReadOnly;
        }
    }

    private async Task<bool> ConfirmLeaveTextAsync()
    {
        if (loadoutText is not { } edit || edit.Text == edit.Original)
            return true;

        var review = ConfigTextCheck.Inspect(edit.Text, model.Dialect, model.Catalog, model.Model.Mode);
        var dialog = Dialog("Apply your text edits?", Body("Your text edits aren’t saved to this loadout yet."), "Apply Edits");
        dialog.SecondaryButtonText = "Discard Edits";
        dialog.CloseButtonText = "Keep Editing";
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.IsPrimaryButtonEnabled = review.CanApply && CanEdit;
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return false;
        if (result == ContentDialogResult.Primary)
        {
            try
            {
                CommitExpertText(edit);
            }
            catch (ConfigProfileException error) when (error.Failure == ConfigProfileFailure.Conflict)
            {
                ShowStatus(InfoBarSeverity.Warning, "This loadout changed on disk",
                    "Revert to see the new version. Your text edits are still open.");
                return false;
            }
        }
        loadoutText = null;
        return true;
    }

    private void CommitExpertText(TextEdit edit)
    {
        string lineEnding = Encoding.Latin1.GetString(edit.Source.Document.Serialized.AsSpan()).Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n" : "\n";
        ConfigExpertEdits.ApplyText(model.Studio, edit.Source, edit.Text.ReplaceLineEndings(lineEnding), model.Catalog, model.Model.Mode);
    }
}
