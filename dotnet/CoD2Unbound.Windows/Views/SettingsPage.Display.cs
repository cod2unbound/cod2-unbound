using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SettingsPage
{
    private TextBlock? displaySaved;

    private void Display()
    {
        var container = new StackPanel { Spacing = 12, MaxWidth = 900 };
        Fields.Children.Add(container);

        var previewTitle = new TextBlock();
        var previewMode = new TextBlock { Style = StyleFor("DisplayPreviewNote") };
        var gamePicture = new Border
        {
            Style = StyleFor("DisplayPreviewPicture"),
            Child = new StackPanel { Style = StyleFor("DisplayPreviewAnnotation"), Children = { previewTitle, previewMode } }
        };
        var panel = new Border { Style = StyleFor("DisplayPreviewScreen"), Child = gamePicture };
        var monitorOutline = new Border
        {
            Style = StyleFor("DisplayPreviewFrame"), Child = panel
        };
        var monitor = new StackPanel
        {
            Children =
            {
                monitorOutline, new Microsoft.UI.Xaml.Shapes.Path { Style = StyleFor("DisplayPreviewStand") },
                new Border { Style = StyleFor("DisplayPreviewBase") }
            }
        };
        var previewName = new TextBlock { Style = StyleFor("DisplayPreviewDevice") };
        var screen = new Border
        {
            Style = StyleFor("DisplayPreviewCard"),
            Child = new StackPanel { Style = StyleFor("DisplayPreviewDiagram"), Children = { monitor, previewName } }
        };
        var rows = new StackPanel { Spacing = 4 };
        var layout = new Grid { Style = StyleFor("DisplayPreviewLayout") };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Put(layout, 0, screen);
        Put(layout, 1, rows);
        double stackBelow = (double)Application.Current.Resources["DisplayPreviewStackBelow"];
        double windowInset = (double)Application.Current.Resources["DisplayPreviewWindowInset"];
        layout.SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < stackBelow;
            layout.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            layout.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetRow(rows, compact ? 1 : 0);
            Grid.SetColumn(rows, compact ? 0 : 1);
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.Children.Add(layout);

        var displays = WindowsDisplayInventory.ReadForGame();
        var monitors = new ComboBox { MinWidth = 160, MaxWidth = 300, IsEnabled = !displays.IsEmpty };
        var selectedDisplay = WindowsDisplays.Selected(displays, Current.WindowsMonitor);
        foreach (var display in displays)
            monitors.Items.Add(new ComboBoxItem { Content = $"{display.Name} · {SizeText(display.Native ?? display.Current)}", Tag = display });
        if (selectedDisplay is not null)
            monitors.SelectedIndex = displays.IndexOf(selectedDisplay);
        else
            monitors.PlaceholderText = "Display unavailable";
        AutomationProperties.SetName(monitors, "Display");
        var monitorHint = Caption("");
        AddRow("Display", monitors, monitorHint);

        string[] modes = ["borderless", "exclusive", "windowed"];
        var mode = new ComboBox { ItemsSource = modes.Select(WindowsDisplays.ModeName).ToArray(), MinWidth = 160,
            SelectedIndex = Math.Max(0, Array.IndexOf(modes, Current.Fullscreen)) };
        AutomationProperties.SetName(mode, "Mode");
        AutomationProperties.SetAutomationId(mode, "SettingsMode");
        var modeHint = Caption("");
        AddRow("Mode", mode, modeHint);

        // The Mac's segmented control, with Windows' two renderers (SetDisplay board).
        Action<int> chooseGraphics = _ => { };
        var graphics = Tabs(["Direct3D 9", "DirectX 7"], (int)model.Renderer, index => chooseGraphics(index));
        graphics.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(graphics, "Graphics");
        AutomationProperties.SetAutomationId(graphics, "SettingsGraphics");
        rows.Children.Add(SettingRow("Graphics",
            "Direct3D 9 is how the game draws on Windows. DirectX 7 is the older way, kept in case something looks wrong.", graphics));

        var resolution = Body("");
        var choose = Action("Choose in Configs", shell.OpenResolutionAsync);
        choose.Style = StyleFor("SubtleLinkAction");
        var resolutionPicker = new ComboBox { MinWidth = 160, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(resolutionPicker, "Resolution");
        choose.Visibility = Visibility.Collapsed;
        AddRow("Resolution", new Grid { Children = { choose, resolutionPicker } }, resolution);

        var loadoutNote = new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Visibility = Visibility.Collapsed,
            Message = "Your loadouts keep their own resolution and picture settings.",
            ActionButton = Action("Open Configs", shell.OpenConfigsFromSettingsAsync)
        };
        container.Children.Add(loadoutNote);
        displaySaved = Caption("Saved. Applies the next time the game starts.");
        container.Children.Add(displaySaved);
        RefreshSaveFailure();
        ConfigProfile? loadout = null;
        bool loadoutRead = false;
        bool refreshingResolutions = false;
        WindowsDisplay? listedDisplay = null;
        var restore = Action("Restore Defaults", async () =>
        {
            previousDisplay = Current;
            var defaults = new GameSettings();
            await ChangeAsync(Current with { Fullscreen = defaults.Fullscreen, WindowsRenderer = null, WindowsMonitor = null,
                Resolution = loadoutRead && loadout is null ? defaults.Resolution : Current.Resolution });
            Render();
        });
        var undo = Action("Undo", async () =>
        {
            if (previousDisplay is not { } saved)
                return;
            previousDisplay = null;
            await ChangeAsync(Current with { Fullscreen = saved.Fullscreen, WindowsRenderer = saved.WindowsRenderer,
                WindowsMonitor = saved.WindowsMonitor, Resolution = saved.Resolution });
            Render();
        });
        undo.Visibility = IsVisible(previousDisplay is not null);
        container.Children.Add(Buttons(restore, undo));

        UpdatePreview();
        container.Loaded += async (_, _) => await shell.ShowProblemAsync(async () =>
        {
            loadout = await session.ReadLoadoutAsync();
            if (!container.IsLoaded)
                return;
            loadoutRead = true;
            UpdatePreview();
        });
        monitors.SelectionChanged += async (_, _) =>
        {
            if (monitors.SelectedItem is ComboBoxItem { Tag: WindowsDisplay selected })
            {
                await Save(Current with { WindowsMonitor = selected.Id }, monitors);
            }
        };
        mode.SelectionChanged += async (_, _) =>
        {
            if (mode.SelectedIndex >= 0)
                await Save(Current with { Fullscreen = modes[mode.SelectedIndex] }, mode);
        };
        resolutionPicker.SelectionChanged += async (_, _) =>
        {
            if (!refreshingResolutions && resolutionPicker.SelectedItem is ComboBoxItem { Tag: string size } &&
                size != Current.Resolution)
                await Save(Current with { Resolution = size }, resolutionPicker);
        };
        chooseGraphics = index => _ = Save(Current with { WindowsRenderer = (WindowsRenderMode)index }, graphics);

        void AddRow(string label, FrameworkElement action, TextBlock detail)
        {
            var row = SettingRow(label, null, action);
            ((StackPanel)((Grid)row.Child).Children[0]).Children.Add(detail);
            rows.Children.Add(row);
        }

        async Task Save(GameSettings settings, Control editor)
        {
            var pending = ChangeAsync(settings, editor);
            if (displaySaved is { } saved)
                saved.Visibility = Visibility.Collapsed;
            UpdatePreview();
            await pending;
        }

        void UpdatePreview()
        {
            var selected = WindowsDisplays.ForLoadout(displays, Current.WindowsMonitor, loadout?.RequestedMonitor);
            string modeText = WindowsDisplays.ModeName(Current.Fullscreen);
            bool legacy = loadoutRead && loadout is null;
            monitorHint.Text = loadout is not null ? "Your next game’s loadout can choose a different display." : "";
            modeHint.Text = Current.Fullscreen switch
            {
                "exclusive" => "Full screen shows the chosen size pixel for pixel. Switching apps can take a moment.",
                "windowed" => "A window at the chosen size, beside your other apps.",
                _ => "Borderless fills the screen and switches apps instantly."
            };
            string requested = loadout?.RequestedResolution ?? Current.Resolution;
            string size = LauncherText.Resolution(requested);
            bool windowed = Current.Fullscreen == "windowed";
            previewTitle.Style = StyleFor(windowed ? "DisplayPreviewWindowTitle" : "DisplayPreviewTitle");
            previewTitle.Text = legacy ? size : modeText.ToUpperInvariant();
            previewMode.Text = Current.Fullscreen switch
            {
                "windowed" => "beside your other apps",
                "exclusive" => "pixel for pixel",
                _ => "fills the display"
            };
            var nativeSize = selected?.Native ?? selected?.Current ?? new DisplayResolution(1920, 1080);
            var gameSize = DisplayResolution.TryParse(requested, out var parsed) ? parsed : nativeSize;
            double scale = Math.Min(panel.Width / nativeSize.Width, panel.Height / nativeSize.Height);
            gamePicture.Width = windowed ? Math.Clamp(gameSize.Width * scale, gamePicture.MinWidth, panel.Width - windowInset) : panel.Width;
            gamePicture.Height = windowed ? Math.Clamp(gameSize.Height * scale, gamePicture.MinHeight, panel.Height - windowInset) : panel.Height;
            previewName.Text = selected is null ? "Display unavailable" : selected.Name + (selected.Monitor == 1 ? " · main display" : "");
            AutomationProperties.SetName(screen, $"Display preview, {previewName.Text}, {modeText}");
            choose.Visibility = loadoutNote.Visibility = IsVisible(loadout is not null);
            resolutionPicker.Visibility = IsVisible(legacy);
            resolution.Text = loadout is not null ? $"{size} from your {loadout.DisplayName} loadout. Each loadout keeps its own." :
                !legacy ? "" : WindowsDisplays.Adjustment(Current.Resolution, selected) is { } adjusted
                    ? $"This display can’t show {size}. The game will use {SizeText(adjusted)}."
                    : selected?.Native is { } native ? $"This display’s own size is {SizeText(native)} pixels." : "";
            if (legacy)
                RefreshResolutions(selected, size);
            restore.IsEnabled = loadoutRead && (Current.WindowsMonitor is not null || Current.Fullscreen != "borderless" ||
                model.Renderer != WindowsRenderMode.Direct3D9 || legacy && Current.Resolution != new GameSettings().Resolution);
        }

        void RefreshResolutions(WindowsDisplay? display, string placeholder)
        {
            refreshingResolutions = true;
            resolutionPicker.IsEnabled = display is { Modes.IsEmpty: false };
            resolutionPicker.PlaceholderText = placeholder;
            if (!ReferenceEquals(listedDisplay, display))
            {
                listedDisplay = display;
                resolutionPicker.Items.Clear();
                if (display is not null)
                    foreach (var mode in display.Modes)
                        resolutionPicker.Items.Add(new ComboBoxItem { Tag = mode.Size.ToString(),
                            Content = SizeText(mode.Size) + (mode.Size == display.Native ? " · Native" : "") });
            }
            resolutionPicker.SelectedItem = resolutionPicker.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is string size && size == Current.Resolution);
            refreshingResolutions = false;
        }
    }

    // While the game runs: said once, above every pane (SettingsWindow.swift notices).
    internal void RefreshDisplayRunning(bool running) => Running.IsOpen = running;

    internal static string SizeText(DisplayResolution size) => LauncherText.Resolution(size.ToString());
}
