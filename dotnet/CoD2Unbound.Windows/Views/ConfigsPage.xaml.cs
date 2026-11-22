using System.Collections.Immutable;
using System.Globalization;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Color = global::Windows.UI.Color;
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
    private SelectorBar? studioModes;
    private bool wideGroupRail;
    private string? category;
    private string query = "";
    private (string Id, ConfigProfileTrashHandle Handle)? removed;

    internal ConfigsPage(MainWindow shell, ConfigsViewModel model)
    {
        this.shell = shell;
        this.model = model;
        setupArt = new(model.ReadMapArtAsync);
        InitializeComponent();
        SizeChanged += (_, args) =>
        {
            bool wide = args.NewSize.Width >= 1100;
            if (wideGroupRail == wide)
                return;
            wideGroupRail = wide;
            if (studioTab == 0 && studioBody.Parent is not null)
                RenderStudioBody();
        };
        RenderLoadouts();
        ShowStudio();
    }

    internal void UpdateContrast()
    {
        foreach (var item in Loadouts.Items)
            if (Loadouts.ContainerFromItem(item) is ListViewItem { ContentTemplateRoot: Grid row })
                ApplyLoadoutRowTheme(row);
    }

    private void OnSelectionPillLoaded(object sender, RoutedEventArgs args) => TrackSelectionPill((Border)sender);

    private void OnLoadoutRowLoaded(object sender, RoutedEventArgs args)
    {
        var row = (Grid)sender;
        ApplyLoadoutRowTheme(row);
        if (ContainingListItem(row) is not { } item)
            return;
        long token = item.RegisterPropertyChangedCallback(ListViewItem.IsSelectedProperty, (_, _) => ApplyLoadoutRowTheme(row));
        row.Unloaded += Unloaded;

        void Unloaded(object sender, RoutedEventArgs args)
        {
            item.UnregisterPropertyChangedCallback(ListViewItem.IsSelectedProperty, token);
            row.Unloaded -= Unloaded;
        }
    }

    private void OnLoadoutRowThemeChanged(FrameworkElement sender, object args) => ApplyLoadoutRowTheme((Grid)sender);

    private void ApplyLoadoutRowTheme(Grid row)
    {
        if (row.FindName("LoadoutTile") is not Border tile)
            return;
        bool contrast = shell.IsHighContrast;
        var initial = (TextBlock)tile.Child;
        if (contrast)
        {
            bool selected = ContainingListItem(row)?.IsSelected == true;
            var ink = (SolidColorBrush)ThemeBrush(selected ? "LauncherSelectionPillBrush" : "HeadingBrush", ActualTheme, true);
            var window = (SolidColorBrush)ThemeBrush("PanelBrush", ActualTheme, true);
            tile.Background = selected && ContrastRatio(ink.Color, window.Color) < 4.5
                ? ThemeBrush("AccentFillColorDefaultBrush", ActualTheme, true) : window;
            tile.BorderBrush = initial.Foreground = ink;
        }
        else
        {
            tile.ClearValue(Border.BackgroundProperty);
            tile.ClearValue(Border.BorderBrushProperty);
            initial.ClearValue(TextBlock.ForegroundProperty);
        }
        SetRowTextBrush((TextBlock)row.FindName("LoadoutStatus")!, "CaptionBrush", contrast);
        var check = (FontIcon)row.FindName("LoadoutCheck")!;
        if (contrast)
            check.ClearValue(IconElement.ForegroundProperty);
        else
            check.Foreground = ThemeBrush("PositiveBrush", ActualTheme, false);

        static double ContrastRatio(Color ink, Color background)
        {
            double a = Luminance(ink) + 0.05;
            double b = Luminance(background) + 0.05;
            return Math.Max(a, b) / Math.Min(a, b);

            static double Luminance(Color color) =>
                0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

            static double Linear(byte channel)
            {
                double srgb = channel / 255.0;
                return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
            }
        }
    }

    internal void OpenCheckup()
    {
        studioTab = 2;
        ShowStudio();
    }

    private void RenderLoadouts()
    {
        var state = model.State;
        syncing = true;
        var rows = state.Profiles.Select(profile => new LoadoutRow(profile)).ToArray();
        Loadouts.ItemsSource = rows;
        Loadouts.SelectedItem = rows.FirstOrDefault(row => row.Profile.Id == state.Current?.Profile.Id);
        syncing = false;
        NewLoadout.IsEnabled = AddConfig.IsEnabled = !state.ReadOnly && state.Draft is null;
    }

    private async void OnSelectLoadout(object sender, SelectionChangedEventArgs args)
    {
        if (syncing || Loadouts.SelectedItem is not LoadoutRow loadout)
            return;
        await shell.RunAsync(() =>
        {
            model.Studio.Focus(loadout.Profile.Id);
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
        DiscoverPlaces.SelectedItem = null;
        Status.IsOpen = false;
        Detail.Children.Clear();
        studioBody.Children.Clear();
        var state = model.State;
        if (state.Current is not { } profile)
        {
            Detail.Children.Add(Heading("My loadouts"));
            Detail.Children.Add(Caption(state.Profiles.IsEmpty ? "Create a loadout or add a config to get started." : "Choose a loadout to edit."));
            return;
        }

        var pages = new[] { UsagePage.Loadout, UsagePage.Binds, UsagePage.Checkup, UsagePage.History };
        model.Opened(pages[studioTab]);
        var tabs = Tabs(["Settings", "Binds", "Check-up", "History"], studioTab, index =>
        {
            studioTab = index;
            model.Opened(pages[index]);
            if (studioModes is not null)
                studioModes.Visibility = IsVisible(index == 0);
            RenderStudioBody();
        });
        AutomationProperties.SetAutomationId(tabs.Items[0], "StudioSettings");
        Detail.Children.Add(tabs);
        var title = new StackPanel { Spacing = 2, Children = { Heading(profile.Profile.DisplayName) } };
        var subtitle = new List<string>();
        if (profile.Profile.Id.Equals("main", StringComparison.OrdinalIgnoreCase))
            subtitle.Add("Your everyday setup");
        if (state.History.LastOrDefault() is { } edit)
            subtitle.Add($"Edited {edit.Date.LocalDateTime:d}");
        if (subtitle.Count != 0)
            title.Children.Add(Caption(string.Join(" · ", subtitle)));

        var right = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        if (profile.Profile.IsActive)
        {
            var badge = IconLabel("Check", "In use for your next game", 14);
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            var icon = (FontIcon)badge.Children[0];
            var label = (TextBlock)badge.Children[1];
            icon.Style = StyleFor("PositiveIcon");
            label.Style = StyleFor("PositiveCaption");
            right.Children.Add(badge);
        }
        studioModes = Tabs(["Guided", "Expert"], expert ? 1 : 0, index =>
        {
            expert = index == 1;
            query = "";
            RenderStudioBody();
        });
        studioModes.Margin = new Thickness(0);
        studioModes.HorizontalAlignment = HorizontalAlignment.Right;
        studioModes.Visibility = IsVisible(studioTab == 0);
        right.Children.Add(studioModes);
        var header = Columns(12, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(header, 0, title);
        Put(header, 1, right);
        Detail.Children.Add(header);
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
        var dialog = new SafeRemovalDialog($"Delete the loadout “{profile.DisplayName}”?",
            "Its settings, binds and history go with it. Your other loadouts aren’t affected. You can put it back from the Recycle Bin.",
            "Delete loadout")
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme
        };
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
        var groups = model.Catalog.Groups;
        category ??= groups.FirstOrDefault(group => model.Model.Guided(group.Id).Any(spec => spec.Name == "sensitivity"))?.Id ?? groups[0].Id;
        var fields = new StackPanel { Spacing = 4 };
        var filter = ConfigExpertFilter.Group;
        var groupChoice = new ComboBox { Header = "Group", ItemsSource = groups, DisplayMemberPath = "Title",
            SelectedItem = groups.FirstOrDefault(group => group.Id == category) };
        groupChoice.SelectionChanged += (_, _) =>
        {
            if (groupChoice.SelectedItem is ConfigCatalogGroup group)
                SelectGroup(group.Id);
        };
        if (!wideGroupRail)
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
        if (wideGroupRail)
        {
            var rail = new ListView
            {
                ItemsSource = groups, DisplayMemberPath = "Title", SelectionMode = ListViewSelectionMode.Single,
                ItemContainerStyle = StyleFor("SidebarItem"), SelectedItem = groups.FirstOrDefault(group => group.Id == category)
            };
            AutomationProperties.SetName(rail, "Groups");
            rail.SelectionChanged += (_, _) =>
            {
                if (rail.SelectedItem is ConfigCatalogGroup group)
                    SelectGroup(group.Id);
            };
            var columns = Columns(12, new GridLength(200), new GridLength(1, GridUnitType.Star));
            Put(columns, 0, rail);
            Put(columns, 1, fields);
            studioBody.Children.Add(columns);
        }
        else
            studioBody.Children.Add(fields);
        studioBody.Children.Add(Caption("Changes save to this loadout as you make them. Undo anytime from History."));
        RenderRows();

        void SelectGroup(string group)
        {
            category = group;
            RenderRows();
        }

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
            var groupTitle = groups.First(item => item.Id == group).Title;
            var groupHeader = new StackPanel { Spacing = 2 };
            groupHeader.Children.Add(new TextBlock { Text = groupTitle, Style = StyleFor("SectionTitle") });
            if (!expert && specs.Any(spec => spec.Name == "sensitivity"))
                groupHeader.Children.Add(Caption("How the game responds to your mouse or touchpad."));
            if (!expert)
            {
                int total = model.Model.Expert(group, ConfigExpertFilter.Group, "", profile.Document, profile.Metadata)
                    .Sum(section => section.Rows.Count(spec => !spec.Name.EndsWith("fps", StringComparison.OrdinalIgnoreCase)));
                var link = new HyperlinkButton { Content = $"Show all {total} in Expert ›", VerticalAlignment = VerticalAlignment.Top };
                link.Click += (_, _) =>
                {
                    if (studioModes is not null)
                    {
                        studioModes.SelectedItem = studioModes.Items[1];
                        studioModes.SelectedItem.Focus(FocusState.Programmatic);
                    }
                };
                var heading = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
                Put(heading, 0, groupHeader);
                Put(heading, 1, link);
                fields.Children.Add(heading);
            }
            else
                fields.Children.Add(groupHeader);
            if (expert)
            {
                foreach (var spec in specs)
                    fields.Children.Add(Setting(state, profile.Profile.Id, spec));
                return;
            }

            var rows = new StackPanel();
            foreach (var spec in specs)
            {
                if (rows.Children.Count > 0)
                    rows.Children.Add(new Border { Height = 1, Style = StyleFor("CardDivider") });
                rows.Children.Add(Setting(state, profile.Profile.Id, spec));
            }
            fields.Children.Add(new Border { Style = StyleFor("FieldCard"), Padding = new Thickness(0), Child = rows });
        }
    }

    private Border Setting(ConfigStudioState state, string profileId, ConfigSettingSpec spec)
    {
        var item = ConfigStudioModel.State(spec, state.Current?.Document, state.Current?.Metadata);
        string? block = model.Model.TakeTheirsBlock(spec, state, null, [], "");
        if (block is not null)
            return SettingRow(spec.Title, block, Caption(item.Display), grouped: !expert);

        Control control;
        if (!expert && spec.Name.Equals("cl_mouseAccel", StringComparison.OrdinalIgnoreCase))
        {
            double current = ConfigValues.Number(item.Value ?? "", spec.Type) ?? 0;
            string on = current > 0 ? ConfigValues.Clamped(current, spec) : "1";
            var toggle = new ToggleSwitch { IsOn = current > 0, OnContent = "On", OffContent = "Off", MinWidth = 0 };
            AutomationProperties.SetName(toggle, "Speed up on fast movements");
            toggle.Toggled += async (_, _) => await Apply(toggle.IsOn ? on : "0");
            return SettingRow("Speed up on fast movements", "Off keeps every turn the same size, however fast your hand moves.", toggle, grouped: true);
        }
        if (!expert && spec.Name.Equals("sensitivity", StringComparison.OrdinalIgnoreCase) &&
            spec.SliderRange is { Min: > 0 } range && MouseDistance.Centimetres(model.MouseDpi, 1) is { } reference &&
            ConfigValues.Number(item.Value ?? "", spec.Type) is > 0 and var sensitivity)
        {
            double current = reference / sensitivity;
            double minimum = Math.Min(current, reference / range.Max);
            double maximum = Math.Max(current, reference / range.Min);
            var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = current,
                Width = 148, StepFrequency = 0.1, IsThumbToolTipEnabled = false };
            var distance = new TextBlock { Text = Distance(current), Width = 148, VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right, Style = StyleFor("BodyStrongTextBlockStyle") };
            var layout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { slider, distance } };
            AutomationProperties.SetName(slider, "Mouse speed, centimeters per full turn");
            double lastSaved = current;
            slider.ValueChanged += async (_, args) =>
            {
                distance.Text = Distance(args.NewValue);
                if (!slider.IsLoaded || Math.Abs(args.NewValue - lastSaved) < 0.05)
                    return;
                lastSaved = args.NewValue;
                await Apply(ConfigValues.Clamped(reference / args.NewValue, spec));
            };
            return SettingRow("Mouse speed", $"Worked out from {model.MouseDpi:G} DPI.", layout, grouped: true);
        }
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
        return SettingRow(spec.Title, spec.Setting.Summary, control, grouped: !expert);

        Task Apply(string value) => shell.RunAsync(() =>
        {
            if (model.State.Current is { } current && current.Profile.Id == profileId)
                model.Set(current, spec, value);
            return Task.CompletedTask;
        });
    }

    private static string Distance(double cm) => $"{cm:F1} cm per full turn";

    private async Task EditTextAsync(ConfigProfileRead profile)
    {
        var code = new TextBox { Header = "Config text", Text = System.Text.Encoding.Latin1.GetString(profile.Document.Serialized.AsSpan()),
            AcceptsReturn = true, Height = 240, MinWidth = 350, Style = StyleFor("MonoField"), TextWrapping = TextWrapping.Wrap };
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
        foreach (var group in binds.Actions.GroupBy(action => action.Group).OrderBy(group => group.Key))
        {
            studioBody.Children.Add(Caption(group.Key == ConfigBindingGroup.GameUI ? "Game interface" : group.Key.ToString()));
            foreach (var row in group.Select(action => binds.Row(action.Name)!).OrderByDescending(row => row.Primary.HasValue))
            {
                var action = row.Action;
                var primary = KeyButton(row.Primary, ConfigBindingSlot.Primary);
                var secondary = KeyButton(row.Secondary, ConfigBindingSlot.Secondary);
                string? help = action.Summary.Trim().TrimEnd('.').Equals(action.Title, StringComparison.OrdinalIgnoreCase)
                    ? null : action.Summary;
                studioBody.Children.Add(SettingRow(action.Title, help, Buttons(primary, secondary)));

                Button KeyButton(EngineKey? key, ConfigBindingSlot slot)
                {
                    string? label = key is { } assigned ? KeyLabel(assigned) : null;
                    var button = Action(label ?? "Add key", () => BindAsync(action, slot));
                    button.Style = StyleFor(label is null ? "AddKeycap" : "Keycap");
                    AutomationProperties.SetName(button, $"{action.Title}, {slot.ToString().ToLowerInvariant()} key, {label ?? "unassigned"}");
                    button.IsEnabled = !state.ReadOnly;
                    return button;
                }
            }
        }
    }

    private static string KeyLabel(EngineKey key) => key.Spelling.ToUpperInvariant() switch
    {
        "SPACE" => "Space", "ENTER" => "Enter", "TAB" => "Tab", "CTRL" => "Ctrl", "SHIFT" => "Shift",
        "ALT" => "Alt", "BACKSPACE" => "Backspace", "ESCAPE" => "Esc",
        "UPARROW" => "↑", "DOWNARROW" => "↓", "LEFTARROW" => "←", "RIGHTARROW" => "→",
        _ => key.Spelling.ToUpperInvariant()
    };

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
        MatchSummary? match;
        try { match = model.LastMatch; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { match = null; }
        studioBody.Children.Add(new TextBlock { Text = "Check-up", Style = StyleFor("SectionTitle") });
        if (match is null)
        {
            AddStudioEmpty("HeartPulse", "No matches recorded yet",
                "After your next match, its summary appears here if match summaries are on.");
            return;
        }
        var report = checkup.Evaluate(profile, [], ConfigTuneFor.Balanced,
            measurements: new ConfigCheckupMeasurements(PingMS: match.PingAverageMS, PacketLossPercent: match.PacketLossPercent));
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
            AddStudioEmpty("History", "No changes recorded yet",
                "Changes to this loadout appear here. Undo anytime from History.");
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

    private void AddStudioEmpty(string icon, string heading, string detail)
    {
        var state = EmptyState(icon, heading, detail);
        state.HorizontalAlignment = HorizontalAlignment.Center;
        state.VerticalAlignment = VerticalAlignment.Center;
        foreach (UIElement child in state.Children)
        {
            if (child is FrameworkElement element)
                element.HorizontalAlignment = HorizontalAlignment.Center;
            if (child is TextBlock text)
                text.TextAlignment = TextAlignment.Center;
        }
        studioBody.Children.Add(new Grid { MinHeight = 214, Children = { state } });
    }

    private async void OnDiscoverPlaceChanged(object sender, SelectionChangedEventArgs args)
    {
        int place = DiscoverPlaces.SelectedIndex;
        if (place < 0)
            return;
        await shell.RunAsync(() => place == 2 ? ShowCheckerAsync() : ShowCatalogueAsync(place == 1));
    }

    private Task ShowCheckerAsync()
    {
        model.Opened(UsagePage.Checker);
        Status.IsOpen = false;
        Loadouts.SelectedItem = null;
        Detail.Children.Clear();
        Detail.Children.Add(Heading("Check a config"));
        var text = new TextBox
        {
            Header = "Paste config text", AcceptsReturn = true, Height = 150,
            Style = StyleFor("MonoField"), TextWrapping = TextWrapping.Wrap
        };
        var results = new StackPanel { Spacing = 4 };
        var check = Action("Check config", async () =>
        {
            model.Record(new(UsageKind.CheckRun));
            ConfigCheckReport report;
            try
            {
                report = await ConfigChecker.CheckAsync([.. ConfigDocument.Encode(text.Text.ReplaceLineEndings("\n"))],
                    model.Dialect, null, model.Catalog, null);
            }
            catch (Exception error) when (error is IOException or ArgumentException)
            {
                model.Error(UsageProblem.Check);
                throw;
            }
            results.Children.Clear();
            if (report.Findings.IsEmpty)
                results.Children.Add(Body("No problems found."));
            foreach (var finding in report.Findings)
                results.Children.Add(SettingRow(finding.Title, finding.Detail));
        });
        check.IsEnabled = false;
        text.TextChanged += (_, _) => check.IsEnabled = !string.IsNullOrWhiteSpace(text.Text);
        Detail.Children.Add(text);
        Detail.Children.Add(check);
        Detail.Children.Add(results);
        return Task.CompletedTask;
    }

    private void UpdateSavedCount() => SavedCount.Text = model.Discover.State.Saved.Length is int count && count > 0
        ? count.ToString(CultureInfo.CurrentCulture) : "";

    private async void OnAddConfig(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        var input = new TextBox { Header = "Paste config text", AcceptsReturn = true, Width = 360, Height = 180, TextWrapping = TextWrapping.Wrap };
        var dialog = Dialog("Add a config", input, "Review");
        dialog.SecondaryButtonText = "Open file…";
        dialog.IsPrimaryButtonEnabled = false;
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary)
        {
            var picker = new FileOpenPicker(shell.AppWindow.Id) { Title = "Open a config file" };
            picker.FileTypeFilter.Add(".cfg");
            if (await picker.PickSingleFileAsync() is { } file)
                await ReviewFileAsync(file.Path);
        }
        else if (choice == ContentDialogResult.Primary)
        {
            await ReviewInputAsync(new ConfigImportInput.Paste([.. ConfigDocument.Encode(input.Text.ReplaceLineEndings("\n"))]),
                "Imported config");
        }
    });

    internal Task ReviewFileAsync(string path)
    {
        if (!Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a .cfg config file.");
        return ReviewInputAsync(new ConfigImportInput.File(new Uri(path)), Path.GetFileNameWithoutExtension(path));
    }

    private async Task ReviewInputAsync(ConfigImportInput input, string name)
    {
        var catalog = new ConfigImportCatalogAdapter(model.Catalog);
        var bundle = await new ConfigImportLoader(catalog, new WindowsConfigImportFiles()).LoadAsync(input, model.Dialect);
        var review = new ConfigImportSession(bundle, model.Dialect, catalog, name, ConfigImportPlatform.Windows, ConfigImportPlatform.Windows);
        if (input is ConfigImportInput.File && !review.Rows.Any(row => IsConfigCommand(row.Candidate.Command, catalog)))
            throw new IOException("This file contains no Call of Duty 2 config commands.");
        await ReviewAsync(review, name, newOnly: false);
    }

    private bool IsConfigCommand(ConfigCommand command, IConfigImportCatalog catalog) =>
        command.Kind switch
        {
            ConfigCommandKind.Empty or ConfigCommandKind.Comment => false,
            ConfigCommandKind.Unknown => catalog.Command(command.Tokens[0].Text, model.Dialect) is not null,
            _ => true
        };

    private async Task ReviewAsync(ConfigImportSession review, string initialName, bool newOnly, Func<double?>? setupDistance = null, string? catalogueId = null)
    {
        var name = new TextBox { Header = "Loadout name", Text = initialName, MaxLength = 80 };
        var profiles = model.State.Profiles;
        var target = new ComboBox { Header = "Import into", ItemsSource = new[] { "New loadout" }
            .Concat(profiles.Select(profile => profile.DisplayName)).ToArray(), SelectedIndex = 0, Visibility = IsVisible(!newOnly) };
        var rows = new StackPanel { Spacing = 4, Padding = new Thickness(0, 0, 0, 8) };
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
                model.Error(UsageProblem.Import);
                problem.Text = error.Message;
            }
            finally { deferral.Complete(); }
        };
        Fill();
        if (await modal.ShowAsync() == ContentDialogResult.Primary && receipt is not null)
        {
            if (catalogueId is not null)
                model.CatalogueEvent(UsageKind.SetupImported, catalogueId);
            if (target.SelectedIndex == 0)
                model.Record(new(UsageKind.LoadoutMade));
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
                var (title, detail) = ReviewCopy(row, setupDistance);
                var select = new CheckBox { Content = title, IsEnabled = row.CanSelect, IsChecked = review.SelectedIds.Contains(row.Id) };
                select.Checked += (_, _) => { review.Select(row.Id, true); Validate(); };
                select.Unchecked += (_, _) => { review.Select(row.Id, false); Validate(); };
                var line = new StackPanel { Spacing = 2 };
                line.Children.Add(select);
                if (detail.Length > 0)
                    line.Children.Add(Caption(detail));
                rows.Children.Add(new Border { Style = StyleFor("ReviewRow"), Child = line });
            }
            Validate();
        }
    }

    private (string Title, string Detail) ReviewCopy(ConfigImportRow row, Func<double?>? setupDistance)
    {
        var candidate = row.Candidate;
        bool credential = candidate.Reasons.Contains(ConfigImportRisk.Credential);
        string? setting = row.Title.StartsWith("Set ", StringComparison.Ordinal) ? row.Title[4..] : null;
        var spec = setting is null ? null : model.Model.Specs.FirstOrDefault(spec => spec.Name.Equals(setting, StringComparison.OrdinalIgnoreCase));
        string title = credential ? "Password or key" : spec?.Name.EndsWith("fps", StringComparison.OrdinalIgnoreCase) == true
            ? "Game timing setting" : spec?.Title ?? (row.Group == "Settings" ? "Config setting" :
                row.Group == "Bindings" ? "Key binding" : RiskSummary(candidate.Reasons.FirstOrDefault(), candidate.Reasons.IsEmpty));
        if (!credential && spec is null && candidate.Classification == ConfigImportClassification.Blocked &&
            candidate.Command.Tokens.Length > 0 && candidate.Command.Tokens[0].Text is { Length: > 0 and <= 32 } command &&
            command.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            title = command;
        string status = candidate.Classification switch
        {
            ConfigImportClassification.NeedsOk => "Needs your OK",
            ConfigImportClassification.Blocked => "Blocked",
            _ => ""
        };
        string? reason = row.PlatformNote;
        if (reason is null && credential)
            reason = ConfigImportRisk.Credential.Summary();
        if (reason is null && !candidate.Reasons.IsEmpty && (candidate.Classification != ConfigImportClassification.Safe || !row.CanSelect))
            reason = candidate.Reasons.Select(risk => RiskSummary(risk, false)).FirstOrDefault(summary => summary != title);
        if (reason is null && !row.LockedNames.IsEmpty)
            reason = "Locked in this loadout";
        if (reason is null && spec is not null && row.NewValue is { } value)
        {
            if (spec.Name.Equals("sensitivity", StringComparison.OrdinalIgnoreCase) && setupDistance is not null)
                reason = setupDistance() is { } distance ? Distance(distance) : "Distance unavailable";
            else if (spec.Name.Equals("sensitivity", StringComparison.OrdinalIgnoreCase) &&
                ConfigValues.Number(value, spec.Type) is { } number && MouseDistance.Centimetres(model.MouseDpi, number) is { } cm)
                reason = Distance(cm);
            else
                reason = "New value: " + ConfigValues.Display(spec, value);
        }
        return (title, string.Join(" · ", new[] { status, reason }.Where(value => !string.IsNullOrEmpty(value))));

        static string RiskSummary(ConfigImportRisk risk, bool empty) =>
            empty ? "Config command" : risk == ConfigImportRisk.FileWrite ? "Writes files on this PC" : risk.Summary();
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
        XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme, Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary, HighContrastAdjustment = ElementHighContrastAdjustment.None
    };

    private void Feedback(string message, Button? action = null)
    {
        Status.Message = message;
        Status.ActionButton = action;
        Status.Severity = InfoBarSeverity.Success;
        Status.IsOpen = true;
    }
}


internal sealed record LoadoutRow(ConfigProfile Profile)
{
    public string Name => Profile.DisplayName;
    public string Initial => StringInfo.GetNextTextElement(Name).ToUpper(CultureInfo.CurrentCulture);
    public string Status => Profile.IsActive ? "In use" : "Ready to use";
    public Visibility Checkmark => IsVisible(Profile.IsActive);
}
