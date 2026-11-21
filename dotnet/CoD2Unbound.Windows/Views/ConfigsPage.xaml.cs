using System.Collections.Immutable;
using System.Globalization;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage : Page
{
    private readonly MainWindow shell;
    private readonly ConfigsViewModel model;
    private readonly StackPanel studioBody = new() { Spacing = 8 };
    private bool syncing;
    private int studioTab;
    private bool expert;
    private string? category;
    private string query = "";
    private (string Id, ConfigProfileTrashHandle Handle)? removed;

    internal ConfigsPage(MainWindow shell, ConfigsViewModel model)
    {
        this.shell = shell;
        this.model = model;
        InitializeComponent();
        RenderLoadouts();
        ShowStudio();
    }

    private void RenderLoadouts()
    {
        var state = model.State;
        syncing = true;
        Loadouts.ItemsSource = state.Profiles;
        Loadouts.SelectedItem = state.Current?.Profile;
        syncing = false;
        NewLoadout.IsEnabled = AddConfig.IsEnabled = !state.ReadOnly && state.Draft is null;
    }

    private async void OnSelectLoadout(object sender, SelectionChangedEventArgs args)
    {
        if (syncing || Loadouts.SelectedItem is not ConfigProfile loadout)
            return;
        await shell.RunAsync(() =>
        {
            model.Studio.Focus(loadout.Id);
            ShowStudio();
            return Task.CompletedTask;
        });
    }

    private async void OnNewLoadout(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        if (await AskNameAsync("New loadout", "Create loadout", "") is { } name)
        {
            model.Create(name);
            await RefreshStudioAsync();
        }
    });

    private async Task<string?> AskNameAsync(string title, string action, string initial, string? except = null)
    {
        var name = new TextBox { Header = "Name", Text = initial, MaxLength = 80 };
        var problem = Caption("");
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(name);
        content.Children.Add(problem);
        var dialog = Dialog(title, content, action);
        void Validate()
        {
            problem.Text = model.NameProblem(name.Text, except) ?? "";
            dialog.IsPrimaryButtonEnabled = problem.Text.Length == 0;
        }
        Validate();
        name.TextChanged += (_, _) => Validate();
        dialog.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? name.Text.Trim() : null;
    }

    private async Task RefreshStudioAsync()
    {
        RenderLoadouts();
        ShowStudio();
        await shell.RefreshPlayerAsync();
    }

    private void ShowStudio()
    {
        Detail.Children.Clear();
        studioBody.Children.Clear();
        var state = model.State;
        if (state.Current is not { } profile)
        {
            Detail.Children.Add(Heading("My loadouts"));
            Detail.Children.Add(Caption(state.Profiles.IsEmpty ? "Create a loadout or add a config to get started." : "Choose a loadout to edit."));
            return;
        }

        var tabs = Tabs(["Settings", "Binds", "Check-up", "History"], studioTab, index =>
        {
            studioTab = index;
            RenderStudioBody();
        });
        AutomationProperties.SetAutomationId(tabs.Items[0], "StudioSettings");
        Detail.Children.Add(tabs);
        Detail.Children.Add(Heading(profile.Profile.DisplayName));
        var next = Action("Use for next game", async () =>
        {
            model.Studio.Select(profile.Profile.Id);
            await RefreshStudioAsync();
        });
        var rename = Action("Rename…", async () =>
        {
            if (await AskNameAsync("Rename loadout", "Rename", profile.Profile.DisplayName, profile.Profile.Id) is { } name)
            {
                model.Rename(profile, name);
                await RefreshStudioAsync();
            }
        });
        var delete = Action("Delete loadout…", () => DeleteAsync(profile.Profile));
        var actions = Buttons(next, rename, delete);
        next.IsEnabled = !state.ReadOnly && !profile.Profile.IsActive;
        rename.IsEnabled = !state.ReadOnly;
        delete.IsEnabled = ConfigStudioModel.DeleteBlock(state, profile.Profile.Id) is null;
        if (profile.Profile.IsActive)
            Detail.Children.Add(Caption("In use for your next game"));
        if (state.ReadOnly)
            Detail.Children.Add(Caption("Configs are read-only while Call of Duty 2 is running."));
        Detail.Children.Add(actions);
        if (studioBody.Parent is Panel old)
            old.Children.Remove(studioBody);
        Detail.Children.Add(studioBody);
        RenderStudioBody();
    }

    private async Task DeleteAsync(ConfigProfile profile)
    {
        var dialog = Dialog($"Delete the loadout “{profile.DisplayName}”?",
            Caption("Its settings, binds and history go with it. Your other loadouts aren’t affected."), "Delete loadout");
        dialog.DefaultButton = ContentDialogButton.None;
        dialog.PrimaryButtonStyle = StyleFor("NegativeButton");
        // Keep Cancel as the only available action during ContentDialog's initial focus pass.
        dialog.IsPrimaryButtonEnabled = false;
        dialog.Opened += (sender, _) => sender.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            dialog.IsPrimaryButtonEnabled = true;
            FindButton(sender, "Cancel")?.Focus(FocusState.Keyboard);
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        removed = (profile.Id, model.Delete(profile.Id));
        await RefreshStudioAsync();
        Feedback("Loadout deleted.", Action("Undo", async () =>
        {
            if (removed is not { } entry)
                return;
            model.Restore(entry.Id, entry.Handle);
            removed = null;
            await RefreshStudioAsync();
            Feedback("Loadout restored.");
        }));
    }

    private static Button? FindButton(DependencyObject root, string content)
    {
        if (root is Button button && Equals(button.Content, content))
            return button;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindButton(VisualTreeHelper.GetChild(root, index), content) is { } match)
                return match;
        return null;
    }

    private void RenderStudioBody()
    {
        studioBody.Children.Clear();
        var state = model.State;
        if (state.Current is not { } profile)
            return;
        switch (studioTab)
        {
            case 0: ShowSettings(state, profile); break;
            case 1: ShowBinds(state, profile); break;
            case 2: ShowCheckup(state, profile); break;
            case 3: ShowHistory(state, profile); break;
        }
    }

    private void ShowSettings(ConfigStudioState state, ConfigProfileRead profile)
    {
        var modes = Tabs(["Guided", "Expert"], expert ? 1 : 0, index =>
        {
            expert = index == 1;
            query = "";
            RenderStudioBody();
        });
        studioBody.Children.Add(modes);
        var groups = model.Catalog.Groups;
        category ??= groups.FirstOrDefault(group => model.Model.Guided(group.Id).Any(spec => spec.Name == "sensitivity"))?.Id ?? groups[0].Id;
        var groupChoice = new ComboBox { Header = "Group", ItemsSource = groups, DisplayMemberPath = "Title",
            SelectedItem = groups.FirstOrDefault(group => group.Id == category) };
        var fields = new StackPanel { Spacing = 4 };
        var filter = ConfigExpertFilter.Group;
        groupChoice.SelectionChanged += (_, _) =>
        {
            if (groupChoice.SelectedItem is ConfigCatalogGroup group)
            {
                category = group.Id;
                RenderRows();
            }
        };
        studioBody.Children.Add(groupChoice);
        if (expert)
        {
            var search = new TextBox { Header = "Find a setting", Text = query };
            search.TextChanged += (_, _) => { query = search.Text; RenderRows(); };
            studioBody.Children.Add(search);
            var filters = new ComboBox { Header = "Show", ItemsSource = new[] { "Selected group", "Changed", "Locked" }, SelectedIndex = 0 };
            filters.SelectionChanged += (_, _) =>
            {
                if (filters.SelectedIndex >= 0)
                {
                    filter = (ConfigExpertFilter)filters.SelectedIndex;
                    RenderRows();
                }
            };
            studioBody.Children.Add(filters);
            var text = Action("Edit as text…", () => EditTextAsync(profile));
            text.IsEnabled = !state.ReadOnly;
            studioBody.Children.Add(text);
        }
        studioBody.Children.Add(fields);
        studioBody.Children.Add(Caption("Changes save to this loadout as you make them. Undo anytime from History."));
        RenderRows();

        void RenderRows()
        {
            fields.Children.Clear();
            if (category is not { } group)
                return;
            if (expert)
            {
                foreach (var section in model.Model.Expert(group, filter, query, model.State.Current?.Document, model.State.Current?.Metadata))
                    AddGroup(section.Group, section.Rows);
            }
            else
                AddGroup(group, model.Model.Guided(group));
            if (fields.Children.Count == 0)
                fields.Children.Add(Caption("No settings found."));
        }

        void AddGroup(string group, IEnumerable<ConfigSettingSpec> settings)
        {
            var specs = settings.Where(spec => !spec.Name.EndsWith("fps", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (specs.Length == 0)
                return;
            fields.Children.Add(new TextBlock { Text = groups.First(item => item.Id == group).Title, Style = StyleFor("SectionTitle") });
            foreach (var spec in specs)
                fields.Children.Add(Setting(state, profile.Profile.Id, spec));
        }
    }

    private Border Setting(ConfigStudioState state, string profileId, ConfigSettingSpec spec)
    {
        var item = ConfigStudioModel.State(spec, state.Current?.Document, state.Current?.Metadata);
        string? block = model.Model.TakeTheirsBlock(spec, state, null, [], "");
        if (block is not null)
            return SettingRow(spec.Title, block, Caption(item.Display));

        Control control;
        if (spec.Type == "bool")
        {
            var toggle = new ToggleSwitch { IsOn = ConfigValues.Integer(item.Value ?? "") is not (null or 0),
                OnContent = "On", OffContent = "Off", MinWidth = 0 };
            toggle.Toggled += async (_, _) => await Apply(toggle.IsOn ? "1" : "0");
            control = toggle;
        }
        else if (spec.Type == "enum" && !spec.Choices.IsEmpty)
        {
            var choice = new ComboBox { ItemsSource = spec.Choices, SelectedItem = item.Display, MinWidth = 120 };
            choice.SelectionChanged += async (_, _) => { if (choice.SelectedItem is string selected) await Apply(selected); };
            control = choice;
        }
        else if (spec.Type is "int" or "float")
        {
            var number = new NumberBox { Value = ConfigValues.Number(item.Value ?? "", spec.Type) ?? double.NaN, Width = 120,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = spec.Step > 0 ? spec.Step : 0.1 };
            number.ValueChanged += async (_, args) =>
            {
                if (!double.IsFinite(args.NewValue))
                    return;
                string value = ConfigValues.Clamped(args.NewValue, spec);
                await Apply(value);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double normalized))
                    number.Value = normalized;
            };
            control = number;
        }
        else
        {
            var text = new TextBox { Text = item.Value ?? "", Width = 160 };
            text.LostFocus += async (_, _) => { if (text.Text != (item.Value ?? "")) await Apply(text.Text); };
            control = text;
        }
        AutomationProperties.SetName(control, spec.Title);
        return SettingRow(spec.Title, spec.Setting.Summary, control);

        Task Apply(string value) => shell.RunAsync(() =>
        {
            if (model.State.Current is { } current && current.Profile.Id == profileId)
                model.Set(current, spec, value);
            return Task.CompletedTask;
        });
    }

    private async Task EditTextAsync(ConfigProfileRead profile)
    {
        var code = new TextBox { Header = "Config text", Text = System.Text.Encoding.Latin1.GetString(profile.Document.Serialized.AsSpan()),
            AcceptsReturn = true, Height = 240, MinWidth = 350, FontFamily = new("Consolas"), TextWrapping = TextWrapping.Wrap };
        if (await Dialog("Edit as text", code, "Save changes").ShowAsync() == ContentDialogResult.Primary)
        {
            model.Model.ApplyText(model.Studio, profile.Version, code.Text.ReplaceLineEndings("\n"));
            RenderStudioBody();
        }
    }

    private void ShowBinds(ConfigStudioState state, ConfigProfileRead profile)
    {
        var binds = new ConfigBindings(profile.Document, model.Catalog, model.Model.Mode);
        studioBody.Children.Add(new TextBlock { Text = "Binds", Style = StyleFor("SectionTitle") });
        foreach (var group in binds.Actions.GroupBy(action => action.Group))
        {
            studioBody.Children.Add(Caption(group.Key == ConfigBindingGroup.GameUI ? "Game interface" : group.Key.ToString()));
            foreach (var action in group)
            {
                if (binds.Row(action.Name) is not { } row)
                    continue;
                var primary = KeyButton(row.Primary, ConfigBindingSlot.Primary);
                var secondary = KeyButton(row.Secondary, ConfigBindingSlot.Secondary);
                studioBody.Children.Add(SettingRow(action.Title, action.Summary, Buttons(primary, secondary)));

                Button KeyButton(EngineKey? key, ConfigBindingSlot slot)
                {
                    var button = Action(key?.Spelling ?? "Set key", () => BindAsync(action, slot));
                    AutomationProperties.SetName(button, $"{action.Title}, {slot.ToString().ToLowerInvariant()} key, {key?.Spelling ?? "unassigned"}");
                    button.IsEnabled = !state.ReadOnly;
                    return button;
                }
            }
        }
    }

    private async Task BindAsync(ConfigBindingAction action, ConfigBindingSlot slot)
    {
        if (model.State.Current is not { } profile)
            return;
        var binds = new ConfigBindings(profile.Document, model.Catalog, model.Model.Mode);
        var row = binds.Row(action.Name)!;
        var existing = slot == ConfigBindingSlot.Primary ? row.Primary : row.Secondary;
        var key = new TextBox { Header = "Key name", Text = existing?.Spelling ?? "", PlaceholderText = "Leave blank to clear" };
        var dialog = Dialog($"Change {action.Title} binding", key, "Save binding");
        key.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = key.Text.Trim().Length == 0 || EngineKey.Parse(key.Text.Trim()) is not null;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var edit = binds.Prepare(profile, action.Name, slot, EngineKey.Parse(key.Text.Trim()));
        if (edit.Conflict is not null && await Dialog("Replace existing binding?",
            Caption($"{edit.Key?.Spelling} is already bound."), "Replace binding").ShowAsync() != ContentDialogResult.Primary)
            return;
        model.Bind(edit, replace: edit.Conflict is not null);
        RenderStudioBody();
    }

    private void ShowCheckup(ConfigStudioState state, ConfigProfileRead profile)
    {
        var checkup = new ConfigCheckup(model.Catalog, ConfigRules.LoadBundled(), ConfigOptimizer.LoadBundled());
        var report = checkup.Evaluate(profile, [], ConfigTuneFor.Balanced);
        studioBody.Children.Add(new TextBlock { Text = "Check-up", Style = StyleFor("SectionTitle") });
        foreach (var dial in report.Dials)
            studioBody.Children.Add(SettingRow(dial.Area.ToString(), dial.Source, Caption(dial.StatusText)));
        foreach (var suggestion in report.Suggestions.Where(item => item.Area != ConfigCheckupArea.Movement))
        {
            var button = Action("Apply", async () =>
            {
                var details = string.Join("\n", suggestion.Changes.Select(change => $"{change.Setting}: {change.OldValue} → {change.NewValue}"));
                if (await Dialog(suggestion.Title, Caption(details), "Apply change").ShowAsync() != ContentDialogResult.Primary)
                    return;
                model.Studio.Mutate(profile.Profile.Id, store => checkup.Apply(report, new HashSet<string> { suggestion.Id }, store));
                RenderStudioBody();
            });
            button.IsEnabled = !state.ReadOnly;
            studioBody.Children.Add(SettingRow(suggestion.Title, suggestion.Why, button));
        }
    }

    private void ShowHistory(ConfigStudioState state, ConfigProfileRead profile)
    {
        studioBody.Children.Add(new TextBlock { Text = "History", Style = StyleFor("SectionTitle") });
        if (state.History.IsEmpty)
            studioBody.Children.Add(Caption("No changes recorded yet."));
        foreach (var entry in state.History.Reverse())
        {
            var restore = Action("Restore…", async () =>
            {
                if (await Dialog("Restore this loadout?", Caption($"Restore the version before “{entry.Summary}”?"), "Restore")
                    .ShowAsync() != ContentDialogResult.Primary)
                    return;
                model.RestoreHistory(profile, entry);
                await RefreshStudioAsync();
            });
            restore.IsEnabled = !state.ReadOnly;
            studioBody.Children.Add(SettingRow(entry.Summary, entry.Date.LocalDateTime.ToString("g", CultureInfo.CurrentCulture), restore));
        }
    }

    private async void OnPlayerSetups(object sender, RoutedEventArgs args) => await shell.RunAsync(() => ShowCatalogueAsync(saved: false));
    private async void OnSaved(object sender, RoutedEventArgs args) => await shell.RunAsync(() => ShowCatalogueAsync(saved: true));

    private async Task ShowCatalogueAsync(bool saved, bool retry = false)
    {
        Detail.Children.Clear();
        Loadouts.SelectedItem = null;
        Detail.Children.Add(Heading(saved ? "Saved" : "Player Setups"));
        Detail.Children.Add(Caption("Loading player setups…"));
        await model.Discover.LoadAsync(retry);
        Detail.Children.RemoveAt(1);
        if (model.Discover.State.Snapshot is null)
        {
            Detail.Children.Add(SettingRow(model.Discover.State.Problem ?? "Player Setups is unavailable.", null,
                Action("Try again", () => ShowCatalogueAsync(saved, retry: true))));
            return;
        }

        var search = new TextBox { PlaceholderText = "Find a player setup", AccessKey = "F" };
        AutomationProperties.SetName(search, "Find a player setup");
        var results = new StackPanel { Spacing = 4 };
        Detail.Children.Add(search);
        Detail.Children.Add(results);
        search.TextChanged += (_, _) => Fill();
        Fill();

        void Fill()
        {
            results.Children.Clear();
            var entries = saved ? model.Discover.State.Saved.Select(bookmark => bookmark.Entry).OfType<ConfigDirectoryEntry>().ToImmutableArray()
                : model.Discover.Entries;
            foreach (var entry in ConfigDiscover.Setups(entries, ConfigSetupFilter.All, search.Text, CultureInfo.CurrentCulture))
                results.Children.Add(SettingRow(entry.Player, string.Join(" · ", new[] { entry.Team, entry.Era, entry.Feel }.OfType<string>()),
                    Action("View setup", () => ShowSetupAsync(entry, saved))));
            if (results.Children.Count == 0)
                results.Children.Add(Caption(saved && entries.IsEmpty ? "No saved player setups." : "No matching player setups."));
        }
    }

    private Task ShowSetupAsync(ConfigDirectoryEntry entry, bool fromSaved)
    {
        bool saved = model.Discover.State.Saved.Any(bookmark => bookmark.Id == entry.Id);
        Detail.Children.Clear();
        Detail.Children.Add(Action(fromSaved ? "Back to Saved" : "Back to Player Setups", () => ShowCatalogueAsync(fromSaved)));
        Detail.Children.Add(Heading(entry.Player));
        Detail.Children.Add(Caption(string.Join(" · ", new[] { entry.Team, entry.Era, entry.Role }.OfType<string>())));
        Detail.Children.Add(Caption(entry.Feel));
        Detail.Children.Add(SettingRow("About this setup", entry.Story));
        Detail.Children.Add(SettingRow("Credit", $"{entry.Credit} · {entry.Provenance.PublishedBy}"));
        Button Review(string label, bool trySetup)
        {
            var button = Action(label, async () =>
            {
                var review = await model.Discover.ReviewAsync(entry.Id, model.Dialect, active: null);
                await ReviewAsync(review, entry.Player, newOnly: trySetup);
            });
            button.IsEnabled = !model.Studio.IsReadOnly;
            return button;
        }
        var attempt = Review("Try this setup", trySetup: true);
        attempt.Style = StyleFor("AccentButtonStyle");
        Detail.Children.Add(Buttons(attempt, Review("Import…", trySetup: false), Action(saved ? "Remove from Saved" : "Save for later", async () =>
        {
            string? message = await model.Discover.SetSavedAsync(entry.Id, !saved);
            await ShowSetupAsync(entry, fromSaved);
            if (message is not null)
                Feedback(message);
        })));
        return Task.CompletedTask;
    }

    private async void OnAddConfig(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        var input = new TextBox { Header = "Paste config text", AcceptsReturn = true, Width = 360, Height = 180, TextWrapping = TextWrapping.Wrap };
        var dialog = Dialog("Add a config", input, "Review");
        dialog.IsPrimaryButtonEnabled = false;
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var catalog = new ConfigImportCatalogAdapter(model.Catalog);
        var bundle = await new ConfigImportLoader(catalog).LoadAsync(
            new ConfigImportInput.Paste([.. ConfigDocument.Encode(input.Text.ReplaceLineEndings("\n"))]), model.Dialect);
        await ReviewAsync(new ConfigImportSession(bundle, model.Dialect, catalog, "Imported config",
            ConfigImportPlatform.Windows, ConfigImportPlatform.Windows), "Imported config", newOnly: false);
    });

    private async Task ReviewAsync(ConfigImportSession review, string initialName, bool newOnly)
    {
        var name = new TextBox { Header = "Loadout name", Text = initialName, MaxLength = 80 };
        var profiles = model.State.Profiles;
        var target = new ComboBox { Header = "Import into", ItemsSource = new[] { "New loadout" }
            .Concat(profiles.Select(profile => profile.DisplayName)).ToArray(), SelectedIndex = 0, Visibility = IsVisible(!newOnly) };
        var rows = new StackPanel { Spacing = 4 };
        var problem = Caption("");
        var content = new StackPanel { Spacing = 8, MinWidth = 360 };
        content.Children.Add(target);
        content.Children.Add(name);
        content.Children.Add(new ScrollViewer { MaxHeight = 236, Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(problem);
        var modal = Dialog("Review import", content, "Import");
        ConfigImportReceipt? receipt = null;
        target.SelectionChanged += (_, _) => { SetTarget(); Fill(); };
        name.TextChanged += (_, _) => Validate();
        modal.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (target.SelectedIndex == 0)
                {
                    var chosen = review.SelectedIds;
                    SetTarget();
                    review.ReplaceSelection(chosen);
                }
                receipt = await review.ApplyAsync(model);
            }
            catch (Exception error) when (error is IOException or ConfigImportSessionException or UnauthorizedAccessException)
            {
                args.Cancel = true;
                problem.Text = error.Message;
            }
            finally { deferral.Complete(); }
        };
        Fill();
        if (await modal.ShowAsync() == ContentDialogResult.Primary && receipt is not null)
        {
            model.Studio.Focus(receipt.ProfileId);
            await RefreshStudioAsync();
            Feedback("Config imported.");
        }

        void SetTarget()
        {
            name.Visibility = IsVisible(target.SelectedIndex == 0);
            review.SetTarget(target.SelectedIndex > 0
                ? new ConfigImportTarget.Existing(model.Studio.ReadImportTarget(profiles[target.SelectedIndex - 1].Id))
                : new ConfigImportTarget.NewLoadout(model.NewId(name.Text.Trim()), name.Text.Trim()));
        }

        void Validate()
        {
            string? reason = target.SelectedIndex == 0 ? model.NameProblem(name.Text) : null;
            modal.IsPrimaryButtonEnabled = reason is null && !review.SelectedIds.IsEmpty;
            problem.Text = reason ?? (review.SelectedIds.IsEmpty ? "Choose the settings and binds to import." : "");
        }

        void Fill()
        {
            rows.Children.Clear();
            foreach (var row in review.Rows)
            {
                if (row.Candidate.Command.Kind is ConfigCommandKind.Empty or ConfigCommandKind.Comment)
                    continue;
                var select = new CheckBox { Content = row.Title, IsEnabled = row.CanSelect, IsChecked = review.SelectedIds.Contains(row.Id) };
                select.Checked += (_, _) => { review.Select(row.Id, true); Validate(); };
                select.Unchecked += (_, _) => { review.Select(row.Id, false); Validate(); };
                string? detail = row.PlatformNote ?? (row.CanSelect ? row.NewValue : row.Candidate.Classification.ToString());
                var line = new StackPanel { Spacing = 2 };
                line.Children.Add(select);
                if (detail is not null)
                    line.Children.Add(Caption(detail));
                rows.Children.Add(new Border { Style = StyleFor("FieldCard"), Child = line });
            }
            Validate();
        }
    }

    private static TextBlock Heading(string title) => new() { Text = title, Style = StyleFor("PageTitle"), TextWrapping = TextWrapping.Wrap };

    private Button Action(string title, Func<Task> call)
    {
        var button = new Button { Content = title };
        button.Click += async (_, _) => await shell.RunAsync(call);
        return button;
    }

    private ContentDialog Dialog(string title, object content, string primary) => new()
    {
        XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary
    };

    private void Feedback(string message, Button? action = null)
    {
        Status.Message = message;
        Status.ActionButton = action;
        Status.Severity = InfoBarSeverity.Success;
        Status.IsOpen = true;
    }
}
