using System.Text;
using CoD2Unbound.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        var choices = new ComboBox { Header = "Compare with", DisplayMemberPath = "Name", ItemsSource = sources,
            SelectedItem = sources.FirstOrDefault(item => item == comparison?.Source) ?? nobody };
        var marker = Caption("");
        var open = SmallButton("Loadout as text…", () =>
        {
            loadoutText = OpenText(model.Studio.ReadProfile(profile.Profile.Id));
            RenderStudioBody();
        });
        studioBody.Children.Add(Buttons(choices, open));
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
                    new ExpertComparison(requested, null, await model.ReadSavedSettingsAsync(requested.SetupId!));
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

        void UpdateMarker()
        {
            marker.Text = comparison is { } current ? $"◆ Compare markers show {current.Source.Name}’s values" : "";
            marker.Visibility = IsVisible(comparison is not null);
        }
    }

    private FrameworkElement ExpertSetting(ConfigProfileRead profile, ConfigSettingSpec spec)
    {
        var state = model.State;
        var current = state.Current ?? profile;
        var setting = ConfigStudioModel.State(spec, current.Document, current.Metadata);
        var card = Setting(state, profile.Profile.Id, spec);
        var content = (Grid)card.Child;
        var details = (StackPanel)content.Children[0];
        if (details.Children[0] is TextBlock heading && (setting.Locked || setting.Changed))
        {
            var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            details.Children.RemoveAt(0);
            if (setting.Locked)
            {
                var icon = new FontIcon { Glyph = "\uE72E", FontSize = 12 };
                AutomationProperties.SetName(icon, "Locked");
                identity.Children.Add(icon);
            }
            if (setting.Changed)
            {
                var dot = new TextBlock { Text = "●", FontSize = 9, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeBrush("AccentFillColorDefaultBrush", ActualTheme, shell.IsHighContrast) };
                AutomationProperties.SetName(dot, "Changed from default");
                identity.Children.Add(dot);
            }
            identity.Children.Add(heading);
            details.Children.Insert(0, identity);
        }
        if (setting.Note is { } note)
        {
            var text = Caption(note);
            text.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
            details.Children.Add(text);
        }

        var actions = new StackPanel { Spacing = 6 };
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
                    model.Model.TakeTheirs(model.Studio, spec, latest.Version, value, source.Source.Name, null, [], "");
                    RenderStudioBody();
                    return Task.CompletedTask;
                });
                take.IsEnabled = model.Model.TakeTheirsBlock(spec, state, null, [], "") is null;
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
        var noteButton = new Button { Content = new FontIcon { Glyph = "\uE70F", FontSize = 14 } };
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
        Put(header, 1, Buttons(revert, SmallButton("Show controls", () => { loadoutText = null; RenderStudioBody(); })));
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
        var gutter = new TextBlock { FontFamily = editor.FontFamily, FontSize = editor.FontSize, TextAlignment = TextAlignment.Right,
            Foreground = ThemeBrush("CaptionBrush", ActualTheme, shell.IsHighContrast) };
        AutomationProperties.SetAccessibilityView(gutter, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var translate = new TranslateTransform();
        gutter.RenderTransform = translate;
        var numbers = new Border { Width = 44, Padding = new Thickness(4, 7, 7, 0), Height = 266, Child = gutter };
        numbers.SizeChanged += (_, args) => numbers.Clip = new RectangleGeometry
            { Rect = new global::Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        var textGrid = Columns(0, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        Put(textGrid, 0, numbers);
        Put(textGrid, 1, editor);
        body.Children.Add(textGrid);

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
                string lineEnding = Encoding.Latin1.GetString(edit.Source.Document.Serialized.AsSpan()).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                ConfigExpertEdits.ApplyText(model.Studio, edit.Source, edit.Text.ReplaceLineEndings(lineEnding), model.Catalog, model.Model.Mode);
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
            if (DescendantScroll(editor) is { } scroll)
                scroll.ViewChanged += (_, _) => translate.Y = -scroll.VerticalOffset;
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
            gutter.Text = string.Join("\n", Enumerable.Range(1, lines));
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

    private static ScrollViewer? DescendantScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll)
                return scroll;
            if (DescendantScroll(child) is { } nested)
                return nested;
        }
        return null;
    }
}
