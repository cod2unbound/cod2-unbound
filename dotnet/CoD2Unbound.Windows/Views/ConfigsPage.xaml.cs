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
    private SelectorBar? studioModes;
    private bool wideGroupRail;
    private string? category;
    private string? displayedStudioProfile;
    private string query = "";
    private (string Id, ConfigProfileTrashHandle Handle)? removed;

    internal ConfigsPage(MainWindow shell, ConfigsViewModel model)
    {
        this.shell = shell;
        this.model = model;
        setupArt = new(model.ReadMapArtAsync);
        InitializeComponent();
        AttachConfigShortcuts();
        AttachImportDrop();
        model.SettingSaveChanged += OnSettingSaveChanged;
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
        AttachLoadoutMenu(row);
        TrackListSelection(row, _ => ApplyLoadoutRowTheme(row));
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
            var ink = ThemeBrush(selected ? "TextOnAccentFillColorPrimaryBrush" : "HeadingBrush", ActualTheme, true);
            tile.Background = ThemeBrush(selected ? "AccentFillColorDefaultBrush" : "PanelBrush", ActualTheme, true);
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
    }

    internal void OpenCheckup() => OpenCheckupForMatch(null);

    internal void OpenResolution()
    {
        if (model.Model.Jump("r_mode").FirstOrDefault(spec => spec.Name == "r_mode") is not { } resolution)
            return;
        studioTab = 0;
        expert = false;
        category = resolution.Setting.Group;
        query = "";
        model.Studio.Focus(model.State.Profiles.FirstOrDefault(profile => profile.IsActive)?.Id);
        focusResolution = true;
        RenderLoadouts();
        ShowStudio();
    }

    private void RenderLoadouts()
    {
        var state = model.State;
        Loadouts.IsEnabled = state.Draft is null;
        syncing = true;
        var rows = state.Profiles.Select(profile => new LoadoutRow(profile)).ToArray();
        Loadouts.ItemsSource = rows;
        Loadouts.SelectedItem = DiscoverPlaces.SelectedItem is null
            ? rows.FirstOrDefault(row => row.Profile.Id == (state.Current?.Profile.Id ?? state.ReadProblem?.ProfileId)) : null;
        syncing = false;
        NewLoadout.IsEnabled = AddConfig.IsEnabled = !state.ReadOnly && state.Draft is null;
        if (lastImport is not null && Status.ActionButton is Button { Tag: "UndoImport" } undo)
            undo.IsEnabled = CanEdit;
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
        TrackGameState(dialog, Validate);
        name.TextChanged += (_, _) => Validate();
        dialog.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? name.Text.Trim() : null;
    }

    private async Task RefreshStudioAsync()
    {
        ShowStudio();
        RenderLoadouts();
        await shell.RefreshPlayerAsync();
    }

    internal void UpdateGameState(bool ended)
    {
        if (ended)
        {
            model.Studio.Refresh();
            checkupMatches = null;
        }
        RenderLoadouts();
        updateDialogGameState?.Invoke();
        RenderRecovery();
        if (DiscoverPlaces.SelectedItem is not null)
        {
            UpdateCheckerAvailability();
            return;
        }
        if (ended)
            ShowStudio();
        else
        {
            UpdateSharingAvailability();
            if (studioBody.Parent is not null)
                RenderStudioBody();
        }
    }

    private void ShowStudio()
    {
        DiscoverPlaces.SelectedItem = null;
        Status.IsOpen = false;
        var state = model.State;
        RenderRecovery();
        if (state.ReadProblem is { ProfileId: { } failed } && failed == displayedStudioProfile &&
            studioBody.Parent == Detail && studioTab != 2)
        {
            studioBody.Visibility = Visibility.Collapsed;
            UpdateSharingAvailability();
            return;
        }

        ExportStatus.Visibility = Visibility.Visible;
        Detail.Children.Clear();
        studioBody.Children.Clear();
        studioBody.Visibility = Visibility.Visible;
        refreshSharing = null;
        if (state.Current is not { } profile)
        {
            displayedStudioProfile = null;
            Detail.Children.Add(Heading(state.Profiles.FirstOrDefault(item => item.Id == state.ReadProblem?.ProfileId)?.DisplayName ?? "My loadouts"));
            if (studioTab == 2 && state.ReadProblem is { } problem)
                Detail.Children.Add(CheckNotice(InfoBarSeverity.Error, "Check-up isn't available", problem.Message));
            else if (state.ReadProblem is null)
                Detail.Children.Add(Caption(state.Profiles.IsEmpty ? "Create a loadout or add a config to get started." : "Choose a loadout to edit."));
            return;
        }

        displayedStudioProfile = profile.Profile.Id;
        var shareNotice = new InfoBar { IsClosable = true };
        shareNotice.Closed += (_, args) =>
        {
            if (args.Reason == InfoBarCloseReason.CloseButton)
                model.Share.DismissStopProblem(profile.Profile.Id);
        };
        ShowShareNotice(shareNotice, model.Share.StopProblem(profile.Profile.Id));
        Detail.Children.Add(shareNotice);
        var pages = new[] { UsagePage.Loadout, UsagePage.Binds, UsagePage.Checkup, UsagePage.History };
        model.Opened(pages[studioTab]);
        var tabs = Tabs(["Settings", "Binds", "Check-up", "History"], studioTab, index =>
        {
            studioTab = index;
            model.Opened(pages[index]);
            if (studioModes is not null)
                studioModes.Visibility = IsVisible(index == 0);
            UpdateHistorySnapshotButton();
            RenderStudioBody();
        });
        AutomationProperties.SetAutomationId(tabs.Items[0], "StudioSettings");
        AddStudioTabs(tabs, profile);
        var title = new StackPanel { Spacing = 2, Children = { Heading(profile.Profile.DisplayName) } };
        var subtitle = new List<string>();
        if (profile.Profile.Id.Equals("main", StringComparison.OrdinalIgnoreCase))
            subtitle.Add("Your everyday setup");
        if (state.History.FirstOrDefault() is { } edit)
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
        var readOnlyNotice = Caption("Configs are read-only while Call of Duty 2 is running.");
        Detail.Children.Add(readOnlyNotice);
        Detail.Children.Add(LoadoutShareActions(profile.Profile, readOnlyNotice, shareNotice));
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
        studioFind = null;
        studioBody.Children.Clear();
        UpdateHistorySnapshotButton();
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
        if (expert && ShowExpertTextIfOpen(profile))
            return;
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
            studioFind = search;
            search.TextChanged += (_, _) =>
            {
                jumpSetting = null;
                query = search.Text;
                RenderRows();
            };
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
            AddExpertTools(profile, RenderRows);
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
                    AddGroup(section.Group, jumpSetting == query
                        ? section.Rows.Where(spec => spec.Name == jumpSetting) : section.Rows);
            }
            else
                AddGroup(group, model.Model.Guided(group));
            if (fields.Children.Count == 0)
                fields.Children.Add(Caption("No settings found."));
        }

        void AddGroup(string group, IEnumerable<ConfigSettingSpec> settings)
        {
            var specs = settings.Where(spec => !IsHiddenFromExpert(spec) || IsCheckupTimingSetting(spec)).ToArray();
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
                    .Sum(section => section.Rows.Count(spec => !IsHiddenFromExpert(spec)));
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
                    fields.Children.Add(ExpertSetting(profile, IsCheckupTimingSetting(spec) ? CheckupTimingSpec(spec) : spec));
                return;
            }

            var rows = new StackPanel();
            foreach (var spec in specs)
            {
                if (rows.Children.Count > 0)
                    rows.Children.Add(new Border { Height = 1, Style = StyleFor("CardDivider") });
                if (spec.Name == "sensitivity")
                {
                    rows.Children.Add(MouseDpiRow(state, profile));
                    rows.Children.Add(new Border { Height = 1, Style = StyleFor("CardDivider") });
                }
                rows.Children.Add(Setting(state, profile.Profile.Id, spec));
            }
            fields.Children.Add(new Border { Style = StyleFor("FieldCard"), Padding = new Thickness(0), Child = rows });
        }
    }

    private Border Setting(ConfigStudioState state, string profileId, ConfigSettingSpec spec)
    {
        var item = ConfigStudioModel.State(spec, state.Current?.Document, state.Current?.Metadata);
        bool mouseDirection = !expert && spec.Name.Equals("m_pitch", StringComparison.OrdinalIgnoreCase);
        string title = mouseDirection ? "Invert up and down" : spec.Title;
        string? block = model.Model.TakeTheirsBlock(spec, state, null, [], "");
        if (spec.Name == "r_mode")
            return DisplayResolutionRow(state, profileId, spec, item.Value, block);
        if (block is not null)
            return SettingRow(title, block, Caption(item.Display), grouped: !expert);

        if (mouseDirection)
        {
            double scale = ConfigValues.Number(item.Value ?? "", "float") ?? 0.022;
            var invert = new ToggleSwitch { IsOn = scale < 0, IsEnabled = scale != 0,
                OnContent = "On", OffContent = "Off", MinWidth = 0 };
            AutomationProperties.SetName(invert, title);
            invert.Toggled += async (_, _) => await Apply(ConfigValues.MousePitch(item.Value, invert.IsOn));
            return SettingRow(title, "Moving the mouse forward looks down.", invert, grouped: true);
        }

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
            spec.SliderRange is { Min: > 0 } range && ConfigMouseDpi.DistancePerSensitivity(state.Current) is { } reference &&
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
            return SettingRow("Mouse speed", $"Worked out from {state.Current?.Metadata.Dpi:G} DPI.", layout, grouped: true);
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
        return !expert && spec.Name == "sensitivity"
            ? SettingRow("Mouse speed", "Enter your mouse’s DPI to see how far you turn.", control, grouped: true)
            : SettingRow(spec.Title, spec.Setting.Summary, control, grouped: !expert);

        Task Apply(string value) => shell.RunAsync(() =>
        {
            if (model.State.Current is { } current && current.Profile.Id == profileId)
                model.Set(current, spec, value);
            return Task.CompletedTask;
        });
    }

    private static string Distance(double cm) => $"{cm:F1} cm per full turn";

    private async void OnDiscoverPlaceChanged(object sender, SelectionChangedEventArgs args)
    {
        int place = DiscoverPlaces.SelectedIndex;
        if (syncing || place < 0)
            return;
        RecoveryStatus.IsOpen = false;
        ExportStatus.Visibility = Visibility.Collapsed;
        await shell.RunAsync(() => place == 2 ? ShowCheckerAsync() : ShowCatalogueAsync(place == 1));
    }

    private void UpdateSavedCount() => SavedCount.Text = model.Discover.State.Saved.Length is int count && count > 0
        ? count.ToString(CultureInfo.CurrentCulture) : "";

    private (string Title, string Detail) ReviewCopy(ConfigImportRow row, Func<double?>? setupDistance)
    {
        var candidate = row.Candidate;
        bool credential = candidate.Reasons.Contains(ConfigImportRisk.Credential);
        string? setting = row.Title.StartsWith("Set ", StringComparison.Ordinal) ? row.Title[4..] : null;
        var spec = setting is null ? null : model.Model.Specs.FirstOrDefault(spec => spec.Name.Equals(setting, StringComparison.OrdinalIgnoreCase));
        string title = credential ? "Password or key" : spec?.Name.EndsWith("fps", StringComparison.OrdinalIgnoreCase) == true
            ? "Game timing setting" : spec?.Title ?? (row.Group is "Settings" or "Bindings"
                ? row.Title : RiskSummary(candidate.Reasons.FirstOrDefault(), candidate.Reasons.IsEmpty));
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
        ShowStatus(InfoBarSeverity.Success, "", message, action);
    }
}


internal sealed record LoadoutRow(ConfigProfile Profile)
{
    public string Name => Profile.DisplayName;
    public string Initial => StringInfo.GetNextTextElement(Name).ToUpper(CultureInfo.CurrentCulture);
    public string Status => Profile.IsActive ? "In use" : "Ready to use";
    public Visibility Checkmark => IsVisible(Profile.IsActive);
}
