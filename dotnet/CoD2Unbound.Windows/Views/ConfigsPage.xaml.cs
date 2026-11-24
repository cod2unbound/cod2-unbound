using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage : Page
{
    private readonly MainWindow shell;
    private readonly ConfigsViewModel model;
    private readonly StackPanel studioBody = new() { Spacing = 14 };
    private bool syncing;
    private bool restoringStudioChoice;
    private int studioTab;
    private bool expert;
    private SelectorBar? studioModes;
    private bool wideGroupRail;
    private string? category;
    private string? displayedStudioProfile;
    private string query = "";
    private (string Id, ConfigProfileTrashHandle Handle)? removed;
    private readonly ObservableCollection<LoadoutRow> loadoutRows = [];

    internal ConfigsPage(MainWindow shell, ConfigsViewModel model)
    {
        this.shell = shell;
        this.model = model;
        setupArt = new(model.ReadMapArtAsync);
        InitializeComponent();
        Loadouts.ItemsSource = loadoutRows;
        AttachConfigShortcuts();
        AttachImportDrop();
        model.SettingSaveChanged += OnSettingSaveChanged;
        SizeChanged += (_, args) =>
        {
            PositionConfigToolbar();
            bool wide = args.NewSize.Width >= 1022; // 20 + 252 + 48 + 200 + 22 + 480
            if (wideGroupRail == wide)
                return;
            wideGroupRail = wide;
            if (studioTab == 0 && studioBody.Parent is not null)
                RenderStudioBody();
        };
        Loaded += (_, _) =>
        {
            PositionConfigToolbar();
            shell.SetTitleBarContent(configToolbar);
        };
        RenderLoadouts();
        ShowStudio();
    }

    private void OnLoadoutContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            args.ItemContainer.ContextFlyout = null;
            args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
        }
        else if (args.Item is LoadoutRow row)
        {
            args.ItemContainer.ContextFlyout = LoadoutMenu(row.Profile, includeNew: false);
            AutomationProperties.SetName(args.ItemContainer, row.Name);
            AutomationProperties.SetHelpText(args.ItemContainer, row.Status);
        }
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
        loadoutRows.Reconcile(state.Profiles.Select(profile => new LoadoutRow(profile, state.Current is { } current && current.Profile.Id == profile.Id
            ? LoadoutProvenance(current.Metadata.Origin) : null)).ToArray(), row => row.Profile.Id);
        var selected = DiscoverPlaces.SelectedItem is null
            ? loadoutRows.FirstOrDefault(row => row.Profile.Id == (state.Current?.Profile.Id ?? state.ReadProblem?.ProfileId)) : null;
        if (!ReferenceEquals(Loadouts.SelectedItem, selected))
            Loadouts.SelectedItem = selected;
        syncing = false;
        NewLoadout.IsEnabled = AddConfig.IsEnabled = !state.ReadOnly && state.Draft is null;
        if (lastImport is not null && Status.ActionButton is Button { Tag: "UndoImport" } undo)
            undo.IsEnabled = CanEdit;
    }

    private async void OnSelectLoadout(object sender, SelectionChangedEventArgs args)
    {
        if (syncing || Loadouts.SelectedItem is not LoadoutRow loadout)
            return;
        await shell.RunAsync(async () =>
        {
            if (loadout.Profile.Id != model.State.Current?.Profile.Id && !await ConfirmLeaveTextAsync())
            {
                RenderLoadouts();
                return;
            }
            model.Studio.Focus(loadout.Profile.Id);
            ShowStudio();
            RenderLoadouts();
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

    internal void RefreshActiveLoadout()
    {
        model.Studio.Refresh();
        RenderLoadouts();
        if (DiscoverPlaces.SelectedItem is null)
            ShowStudio();
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
        configToolbar.Children.Clear();
        studioBody.Children.Clear();
        studioBody.Visibility = Visibility.Visible;
        refreshSharing = null;
        if (state.Current is not { } profile)
        {
            displayedStudioProfile = null;
            if (state.Profiles.IsEmpty && state.ReadProblem is null)
            {
                var empty = EmptyState("Stack", "No loadouts",
                    "Your current game settings become your first loadout, Main, the first time CoD2 Unbound prepares a game. You can also start a new one.");
                empty.Children.Add(Action("New Loadout…", CreateFromShellAsync));
                Detail.Children.Add(empty);
                return;
            }
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
        SelectorBar tabs = null!;
        tabs = Tabs(["Settings", "Binds", "Check-up", "History"], studioTab, ChangeTab);
        async void ChangeTab(int index)
        {
            if (restoringStudioChoice || index == studioTab)
                return;
            await shell.RunAsync(async () =>
            {
                if (!await ConfirmLeaveTextAsync())
                {
                    restoringStudioChoice = true;
                    tabs.SelectedItem = tabs.Items[studioTab];
                    restoringStudioChoice = false;
                    return;
                }
                studioTab = index;
                model.Opened(pages[index]);
                RenderStudioBody();
                ResetDetailScroll();
            });
        }
        AutomationProperties.SetAutomationId(tabs.Items[0], "StudioSettings");
        ShowConfigStudioToolbar(tabs, profile);
        var title = Heading(profile.Profile.DisplayName);
        title.FontSize = 30;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        studioNext = null;
        var subtitle = LoadoutProvenance(profile.Metadata.Origin);
        if (state.History.FirstOrDefault() is { } edit)
        {
            var date = edit.Date.LocalDateTime;
            var verb = state.History.Length == 1 ? "Added" : "Edited";
            subtitle += $" · {verb} {date:d MMM} at {date:HH:mm}";
        }
        var identity = new StackPanel { Spacing = 4, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom,
            Children = { title, Body(subtitle) } };
        var studioHeaderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Bottom };
        studioChangedCount = Caption("");
        studioHeaderActions.Children.Add(studioChangedCount);
        if (profile.Profile.IsActive)
        {
            var badge = IconLabel("Check", "In use for your next game", 14);
            ((FontIcon)badge.Children[0]).Style = StyleFor("PositiveIcon");
            ((TextBlock)badge.Children[1]).Style = StyleFor("PositiveCaption");
            studioHeaderActions.Children.Add(badge);
        }
        else
        {
            studioNext = Action("Use for Next Game", () => UseNextAsync(profile.Profile));
            studioNext.IsEnabled = CanEdit;
            studioHeaderActions.Children.Add(studioNext);
        }
        studioModes = Tabs(["Guided", "Expert"], expert ? 1 : 0, ChangeLevel);
        async void ChangeLevel(int index)
        {
            if (restoringStudioChoice || expert == (index == 1))
                return;
            await shell.RunAsync(async () =>
            {
                if (!await ConfirmLeaveTextAsync())
                {
                    restoringStudioChoice = true;
                    studioModes!.SelectedItem = studioModes.Items[expert ? 1 : 0];
                    restoringStudioChoice = false;
                    return;
                }
                expert = index == 1;
                ClearSettingSearch();
                expertFilter = ConfigExpertFilter.Group;
                RenderStudioBody();
            });
        }
        studioModes.Style = StyleFor("SegmentedLarge");
        AutomationProperties.SetName(studioModes, "Detail level");
        studioHeaderActions.Children.Add(studioModes);
        Detail.Children.Add(CheckPair(identity, studioHeaderActions, 590, rightAuto: true));
        var readOnly = CheckNotice(InfoBarSeverity.Informational, "Read-only while playing", ConfigProfileException.ReadOnlyWhilePlaying);
        readOnly.IsOpen = state.ReadOnly;
        Detail.Children.Add(readOnly);
        Detail.Children.Add(LoadoutShareStatus(profile.Profile, readOnly));
        UpdateStudioHeader();
        if (studioBody.Parent is Panel old)
            old.Children.Remove(studioBody);
        Detail.Children.Add(studioBody);
        RenderStudioBody();
    }

    private void UpdateStudioHeader()
    {
        if (studioModes is not null)
            studioModes.Visibility = IsVisible(studioTab == 0);
        if (studioChangedCount is not null)
        {
            studioChangedCount.Visibility = IsVisible(studioTab == 0 && expert);
            if (studioChangedCount.Visibility == Visibility.Visible)
            {
                int count = model.Model.Expert("", ConfigExpertFilter.Changed, "", model.State.Current?.Document, model.State.Current?.Metadata)
                    .SelectMany(section => section.Rows).Count(spec => !IsHiddenFromExpert(spec));
                studioChangedCount.Text = $"{count} changed from the game’s defaults";
            }
        }
        if (studioNext is not null)
            studioNext.IsEnabled = CanEdit && model.State.ReadProblem is null;
        UpdateHistorySnapshotButton();
    }

    private static string LoadoutProvenance(ConfigOrigin origin) => origin.Kind switch
    {
        ConfigOriginKind.Migration => "Your earlier launcher settings",
        ConfigOriginKind.Copied => origin.Source is { } source ? $"Copied from {source}" : "A copy",
        ConfigOriginKind.Imported => origin.Credit is { } credit ? $"Imported from {credit}" : "Imported",
        _ => "Made in CoD2 Unbound"
    };

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
        settingSearch = null;
        studioBody.Children.Clear();
        UpdateStudioHeader();
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
        var fields = new StackPanel { Spacing = 14 };
        SelectorBar? smallFilters = null;
        var groupChoice = new ComboBox { ItemsSource = groups, DisplayMemberPath = "Title",
            SelectedItem = groups.FirstOrDefault(group => group.Id == category), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(groupChoice, "Group");
        groupChoice.SelectionChanged += (_, _) =>
        {
            if (groupChoice.SelectedItem is ConfigCatalogGroup group)
                SelectGroup(group.Id);
        };
        if (expert)
        {
            AddExpertTools(profile, RenderRows);
            if (ShowExpertTextIfOpen(profile))
                return;
        }

        if (!wideGroupRail)
        {
            var groupTools = Buttons(groupChoice);
            if (expert)
            {
                smallFilters = Tabs(["Selected group", "Changed", "Locked"], (int)expertFilter, index =>
                {
                    expertFilter = (ConfigExpertFilter)index;
                    RenderRows();
                });
                AutomationProperties.SetName(smallFilters, "Show");
                groupTools.Children.Add(smallFilters);
            }
            studioBody.Children.Add(groupTools);
        }
        if (wideGroupRail)
        {
            var rail = new ListView { SelectionMode = ListViewSelectionMode.Single, TabNavigation = KeyboardNavigationMode.Once };
            AutomationProperties.SetName(rail, "Groups");
            foreach (var group in groups)
            {
                var row = new ListViewItem
                {
                    Style = StyleFor("SidebarItem"), Content = IconLabel(GroupIcon(group.Id), group.Title, 18), Tag = group.Id
                };
                AutomationProperties.SetName(row, group.Title);
                rail.Items.Add(row);
                if (group.Id == category && (!expert || expertFilter == ConfigExpertFilter.Group && query.Length == 0))
                    rail.SelectedItem = row;
            }
            rail.SelectionChanged += (_, _) =>
            {
                if (rail.SelectedItem is ListViewItem { Tag: string group })
                    SelectGroup(group);
            };
            var side = new StackPanel { Spacing = 8, Children = { rail } };
            if (expert)
            {
                side.Children.Add(new Border { Height = 1, Style = StyleFor("CardDivider"), Margin = new Thickness(12, 4, 12, 0) });
                side.Children.Add(new TextBlock { Text = "Filters", Style = StyleFor("GroupLabelText"), Margin = new Thickness(12, 0, 0, 0) });
                var changed = FilterChoice(ConfigExpertFilter.Changed, "Changed");
                var locked = FilterChoice(ConfigExpertFilter.Locked, "Locked");
                side.Children.Add(changed);
                side.Children.Add(locked);
                changed.Click += (_, _) => SetFilter(changed, locked, ConfigExpertFilter.Changed);
                locked.Click += (_, _) => SetFilter(locked, changed, ConfigExpertFilter.Locked);
                rail.SelectionChanged += (_, _) =>
                {
                    if (rail.SelectedItem is not null)
                        changed.IsChecked = locked.IsChecked = false;
                };

                CheckBox FilterChoice(ConfigExpertFilter filter, string title)
                {
                    int count = model.Model.Expert("", filter, "", profile.Document, profile.Metadata)
                        .SelectMany(section => section.Rows).Count(VisibleSpec);
                    var check = new CheckBox { Content = $"{title}  {count}", IsChecked = filter == expertFilter };
                    AutomationProperties.SetName(check, title);
                    return check;
                }

                void SetFilter(CheckBox chosen, CheckBox other, ConfigExpertFilter filter)
                {
                    expertFilter = chosen.IsChecked == true ? filter : ConfigExpertFilter.Group;
                    other.IsChecked = false;
                    if (expertFilter != ConfigExpertFilter.Group)
                        rail.SelectedItem = null;
                    ClearSettingSearch();
                    RenderRows();
                }
            }
            var columns = Columns(22, new GridLength(200), new GridLength(1, GridUnitType.Star));
            Put(columns, 0, side);
            Put(columns, 1, fields);
            studioBody.Children.Add(columns);
        }
        else
            studioBody.Children.Add(fields);

        RenderRows();

        void SelectGroup(string group)
        {
            category = group;
            if (expert)
            {
                expertFilter = ConfigExpertFilter.Group;
                if (smallFilters is not null)
                    smallFilters.SelectedItem = smallFilters.Items[0];
                ClearSettingSearch();
            }
            RenderRows();
        }

        void RenderRows()
        {
            fields.Children.Clear();
            if (category is not { } group)
                return;
            if (expert)
            {
                foreach (var section in model.Model.Expert(group, expertFilter, query, model.State.Current?.Document, model.State.Current?.Metadata))
                    AddGroup(section.Group, jumpSetting == query
                        ? section.Rows.Where(spec => spec.Name == jumpSetting) : section.Rows);
            }
            else
                AddGroup(group, model.Model.Guided(group));
            if (fields.Children.Count == 0)
                fields.Children.Add(EmptyState("Search", "No settings found", "Try a different search or choose a setting group."));
        }

        void AddGroup(string group, IEnumerable<ConfigSettingSpec> settings)
        {
            var specs = settings.Where(VisibleSpec).ToArray();
            if (specs.Length == 0)
                return;
            var groupTitle = groups.First(item => item.Id == group).Title;
            var groupHeader = new StackPanel { Spacing = 2 };
            groupHeader.Children.Add(new TextBlock { Text = groupTitle, Style = StyleFor("SectionTitle") });
            if (!expert || query.Length == 0 && expertFilter == ConfigExpertFilter.Group)
                groupHeader.Children.Add(Body(GroupBlurb(group)));
            if (!expert)
            {
                int total = model.Model.Expert(group, ConfigExpertFilter.Group, "", profile.Document, profile.Metadata)
                    .Sum(section => section.Rows.Count(spec => !IsHiddenFromExpert(spec)));
                var link = new Button { Style = StyleFor("QuietButton"), Content = $"Show All {total} in Expert", VerticalAlignment = VerticalAlignment.Bottom };
                AutomationProperties.SetName(link, $"Show all {total} {groupTitle} settings in Expert");
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
            {
                int changed = specs.Count(spec => !spec.LauncherOwned && ConfigStudioModel.State(spec, profile.Document, profile.Metadata).Changed);
                var count = Caption((specs.Length == 1 ? "1 setting" : $"{specs.Length} settings") + (changed == 0 ? "" : $" · {changed} changed"));
                count.VerticalAlignment = VerticalAlignment.Bottom;
                fields.Children.Add(CheckPair(groupHeader, count, 0, rightAuto: true));
            }

            var rows = new StackPanel();
            foreach (var spec in specs)
            {
                if (rows.Children.Count > 0)
                    rows.Children.Add(new Border { Height = 1, Style = StyleFor("CardDivider") });
                var display = IsCheckupTimingSetting(spec) ? CheckupTimingSpec(spec) : spec;
                rows.Children.Add(expert ? ExpertSetting(profile, display) : Setting(state, profile.Profile.Id, display));
                if (!expert && spec.Name == "sensitivity")
                {
                    rows.Children.Add(new Border { Height = 1, Style = StyleFor("CardDivider") });
                    rows.Children.Add(MouseDpiRow(state, profile));
                }
            }
            fields.Children.Add(new Border { Style = StyleFor("StudioSettingsGroup"), Child = rows });
            if (!expert)
                fields.Children.Add(Caption("Changes save to this loadout as you make them. Undo anytime from History."));
        }

        bool VisibleSpec(ConfigSettingSpec spec) => !IsHiddenFromExpert(spec) || IsCheckupTimingSetting(spec);
    }

    private static string GroupIcon(string id) => id switch
    {
        "aim" => "Search", "net" => "Live", "vis" => "Eye", "hud" => "Desktop",
        "audio" => "Live", "gfx" => "Image", "frame" => "Gauge", _ => "More"
    };

    private static string GroupBlurb(string id) => id switch
    {
        "aim" => "How the game responds to your mouse or trackpad.",
        "net" => "How often you and the server talk.",
        "vis" => "How bright and clear the picture is.",
        "hud" => "What stays on screen while you play.",
        "audio" => "How loud the game is.",
        "gfx" => "How the world looks.",
        "frame" => "How smoothly the game runs, and how high and far you can jump.",
        _ => "Your name, and what the game may download."
    };

    private Border Setting(ConfigStudioState state, string profileId, ConfigSettingSpec spec)
    {
        var item = ConfigStudioModel.State(spec, state.Current?.Document, state.Current?.Metadata);
        bool mouseDirection = !expert && spec.Name.Equals("m_pitch", StringComparison.OrdinalIgnoreCase);
        string title = mouseDirection ? "Invert up and down" : spec.Title;
        string? block = model.Model.TakeTheirsBlock(spec, state, null, [], "");
        if (spec.Name == "r_mode")
            return DisplayResolutionRow(state, profileId, spec, item.Value, block);
        if (block is not null)
            return SettingRow(title, block, Caption(item.Display), grouped: true);

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
            var number = new NumberBox { Value = ConfigValues.Number(item.Value ?? "", spec.Type) ?? double.NaN, Width = 86,
                Style = StyleFor("CapsuleNumberBox"), SmallChange = spec.Step > 0 ? spec.Step : 0.1 };
            number.ValueChanged += async (_, args) =>
            {
                if (!double.IsFinite(args.NewValue))
                    return;
                string value = ConfigValues.Clamped(args.NewValue, spec);
                await Apply(value);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double normalized))
                    number.Value = normalized;
            };
            if (spec.SliderRange is { } bounds && double.IsFinite(number.Value))
            {
                var slider = new Slider { Minimum = bounds.Min, Maximum = bounds.Max,
                    Value = Math.Clamp(number.Value, bounds.Min, bounds.Max), Width = 134,
                    StepFrequency = number.SmallChange, IsThumbToolTipEnabled = false };
                AutomationProperties.SetName(slider, spec.Title);
                AutomationProperties.SetName(number, $"{spec.Title}, value");
                var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                settle.Tick += (_, _) => { settle.Stop(); number.Value = slider.Value; };
                slider.Unloaded += (_, _) => settle.Stop();
                slider.ValueChanged += (_, _) =>
                {
                    if (!slider.IsLoaded)
                        return;
                    settle.Stop();
                    settle.Start();
                };
                number.ValueChanged += (_, args) =>
                {
                    if (!double.IsFinite(args.NewValue))
                        return;
                    slider.Value = Math.Clamp(args.NewValue, bounds.Min, bounds.Max);
                    settle.Stop();
                };
                var controls = Buttons(slider, number);
                if (spec.Unit is { Length: > 0 } unit && !IsCheckupTimingSetting(spec))
                {
                    var caption = Caption(unit);
                    caption.VerticalAlignment = VerticalAlignment.Center;
                    controls.Children.Add(caption);
                }
                return SettingRow(spec.Title, spec.Setting.Summary, controls, grouped: true);
            }
            control = number;
        }
        else
        {
            var text = new TextBox { Text = item.Value ?? "", Width = 160, Style = StyleFor("CapsuleTextBox") };
            text.LostFocus += async (_, _) => { if (text.Text != (item.Value ?? "")) await Apply(text.Text); };
            control = text;
        }
        AutomationProperties.SetName(control, spec.Title);
        return !expert && spec.Name == "sensitivity"
            ? SettingRow("Mouse speed", "Enter your mouse’s DPI to see how far you turn.", control, grouped: true)
            : SettingRow(spec.Title, spec.Setting.Summary, control, grouped: true);

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
        await shell.RunAsync(async () =>
        {
            if (!await ConfirmLeaveTextAsync())
            {
                DiscoverPlaces.SelectedItem = null;
                return;
            }
            configToolbar.Children.Clear();
            RecoveryStatus.IsOpen = false;
            ExportStatus.Visibility = Visibility.Collapsed;
            if (place == 2)
                await ShowCheckerAsync();
            else
                await ShowCatalogueAsync(place == 1);
        });
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


internal sealed record LoadoutRow(ConfigProfile Profile, string? Provenance)
{
    public string Name => Profile.DisplayName;
    public string Initial => StringInfo.GetNextTextElement(Name).ToUpper(CultureInfo.CurrentCulture);
    public string Status => Profile.IsActive ? "In use" : Provenance ?? "Ready to use";
    public Visibility Checkmark => IsVisible(Profile.IsActive);
}
