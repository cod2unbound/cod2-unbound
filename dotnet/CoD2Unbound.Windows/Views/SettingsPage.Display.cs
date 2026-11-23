using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SettingsPage
{
    private InfoBar? displayRunning;
    private InfoBar? displaySave;
    private TextBlock? displaySaved;

    private void Display()
    {
        var container = new StackPanel { Spacing = 12, MaxWidth = 900 };
        Fields.Children.Add(container);
        displayRunning = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsClosable = false,
            Message = "Call of Duty 2 is running. Changes you make here apply the next time it starts."
        };
        container.Children.Add(displayRunning);
        RefreshDisplayRunning(session.ClientIsRunning);

        var previewTitle = new TextBlock { Style = StyleFor("StencilText"), FontSize = 22, TextAlignment = TextAlignment.Center };
        var previewMode = Caption("");
        previewMode.TextAlignment = TextAlignment.Center;
        var gamePicture = new Border
        {
            Style = StyleFor("FieldCard"), Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Stretch,
            Child = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { previewTitle, previewMode } }
        };
        var monitorOutline = new Border
        {
            Style = StyleFor("FieldCard"), Padding = new Thickness(3), Width = 198, Height = 118, Child = gamePicture
        };
        var stand = new Border
        {
            Style = StyleFor("FieldCard"), Padding = new Thickness(0), Width = 40, Height = 8,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var previewName = Caption("");
        previewName.TextAlignment = TextAlignment.Center;
        var screen = new StackPanel
        {
            Spacing = 4, MinHeight = 154, VerticalAlignment = VerticalAlignment.Center,
            Children = { monitorOutline, stand, previewName }
        };
        var rows = new StackPanel { Spacing = 4 };
        var layout = Columns(16, new GridLength(220), new GridLength(1, GridUnitType.Star));
        Put(layout, 0, screen);
        Put(layout, 1, rows);
        layout.SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < 650;
            layout.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(220);
            layout.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetRow(rows, compact ? 1 : 0);
            Grid.SetColumn(rows, compact ? 0 : 1);
            layout.RowSpacing = compact ? 8 : 0;
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.Children.Add(layout);

        var displays = WindowsDisplayInventory.ReadForGame();
        var monitors = new ComboBox { MinWidth = 160, MaxWidth = 270, IsEnabled = !displays.IsEmpty };
        var selectedDisplay = WindowsDisplays.Selected(displays, Current.WindowsMonitor);
        foreach (var display in displays)
            monitors.Items.Add(new ComboBoxItem { Content = $"{display.Name} · {SizeText(display.Native ?? display.Current)}", Tag = display });
        if (selectedDisplay is not null)
            monitors.SelectedIndex = displays.IndexOf(selectedDisplay);
        else
            monitors.PlaceholderText = "Display unavailable";
        AutomationProperties.SetName(monitors, "Display");
        var monitorHint = Caption("Your next game’s loadout can choose a different display.");
        AddRow("Display", monitors, monitorHint);

        string[] modes = ["borderless", "exclusive", "windowed"];
        var mode = new ComboBox { ItemsSource = modes.Select(WindowsDisplays.ModeName).ToArray(), MinWidth = 160,
            SelectedIndex = Math.Max(0, Array.IndexOf(modes, Current.Fullscreen)) };
        AutomationProperties.SetName(mode, "Mode");
        var modeHint = Caption("");
        AddRow("Mode", mode, modeHint);

        var graphics = new ComboBox { ItemsSource = new[] { "Direct3D 9", "DirectX 7" }, MinWidth = 160,
            SelectedIndex = (int)model.Renderer };
        AutomationProperties.SetName(graphics, "Graphics");
        rows.Children.Add(SettingRow("Graphics", "Choose how the game draws on Windows.", graphics));

        var resolution = Body("");
        var choose = Action("Choose in Configs", shell.OpenResolutionAsync);
        choose.Style = StyleFor("SubtleLinkAction");
        AddRow("Resolution", choose, resolution);

        container.Children.Add(new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Style = StyleFor("SettingsNote"),
            Message = "Your loadouts keep their own resolution and picture settings.",
            ActionButton = Action("Open Configs", () => shell.NavigateAsync(LauncherPlace.Configs))
        });
        Control retryFocus = mode;
        displaySave = new InfoBar
        {
            Severity = InfoBarSeverity.Error, IsClosable = false,
            Message = "Your changes couldn't be saved. Check that your CoD2 Unbound folder is writable, then try again.",
            ActionButton = Action("Try again", async () =>
            {
                await ChangeAsync(Current, display: true);
                if (Current == model.Settings.Saved && retryFocus.IsLoaded)
                    retryFocus.Focus(FocusState.Programmatic);
            })
        };
        container.Children.Add(displaySave);
        displaySaved = Caption("Saved. Applies the next time the game starts.");
        container.Children.Add(displaySaved);
        RefreshDisplaySave(Current != model.Settings.Saved);
        var restore = Action("Restore defaults", async () =>
        {
            previousDisplay = Current;
            var defaults = new GameSettings();
            await ChangeAsync(Current with { Fullscreen = defaults.Fullscreen, WindowsRenderer = null, WindowsMonitor = null }, display: true);
            Render();
        });
        var undo = Action("Undo", async () =>
        {
            if (previousDisplay is not { } saved)
                return;
            previousDisplay = null;
            await ChangeAsync(Current with { Fullscreen = saved.Fullscreen, WindowsRenderer = saved.WindowsRenderer,
                WindowsMonitor = saved.WindowsMonitor }, display: true);
            Render();
        });
        undo.Visibility = IsVisible(previousDisplay is not null);
        container.Children.Add(Buttons(restore, undo));

        ConfigProfile? loadout = null;
        UpdatePreview();
        container.Loaded += async (_, _) => await shell.ShowProblemAsync(async () =>
        {
            loadout = await session.ReadLoadoutAsync();
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
        graphics.SelectionChanged += async (_, _) =>
        {
            if (graphics.SelectedIndex >= 0)
                await Save(Current with { WindowsRenderer = (WindowsRenderMode)graphics.SelectedIndex }, graphics);
        };

        void AddRow(string label, FrameworkElement action, TextBlock detail)
        {
            var row = SettingRow(label, null, action);
            ((StackPanel)((Grid)row.Child).Children[0]).Children.Add(detail);
            rows.Children.Add(row);
        }

        async Task Save(GameSettings settings, Control editor)
        {
            retryFocus = editor;
            var pending = ChangeAsync(settings, display: true);
            displaySaved!.Visibility = Visibility.Collapsed;
            UpdatePreview();
            await pending;
        }

        void UpdatePreview()
        {
            var selected = WindowsDisplays.ForLoadout(displays, Current.WindowsMonitor, loadout?.RequestedMonitor);
            string modeText = WindowsDisplays.ModeName(Current.Fullscreen);
            modeHint.Text = Current.Fullscreen switch
            {
                "exclusive" => "Full screen shows the chosen size pixel for pixel. Switching apps can take a moment.",
                "windowed" => "A window at the chosen size, beside your other apps.",
                _ => "Borderless fills the screen and switches apps instantly."
            };
            previewTitle.Text = modeText.ToUpperInvariant();
            previewMode.Text = Current.Fullscreen switch
            {
                "windowed" => "beside your other apps",
                "exclusive" => "pixel for pixel",
                _ => "fills the display"
            };
            gamePicture.Margin = Current.Fullscreen == "windowed" ? new Thickness(18, 14, 18, 18) : new Thickness(0);
            previewName.Text = selected is null ? "Display unavailable" : selected.Name + (selected.Monitor == 1 ? " · main display" : "");
            AutomationProperties.SetName(screen, $"Display preview, {previewName.Text}, {modeText}");
            string size = LauncherText.Resolution(loadout?.RequestedResolution ?? Current.Resolution);
            resolution.Text = loadout is null ? $"{size}. Each loadout keeps its own." :
                $"{size} from your {loadout.DisplayName} loadout. Each loadout keeps its own.";
            restore.IsEnabled = Current.WindowsMonitor is not null || Current.Fullscreen != "borderless" ||
                model.Renderer != WindowsRenderMode.Direct3D9;
        }
    }

    internal void RefreshDisplayRunning(bool running)
    {
        if (displayRunning is { } notice)
            notice.IsOpen = running;
    }

    private void RefreshDisplaySave(bool failed)
    {
        if (displaySave is { } notice && lastPane == 1)
        {
            notice.IsOpen = failed;
            displaySaved!.Visibility = IsVisible(!failed);
        }
    }

    internal static string SizeText(DisplayResolution size) => LauncherText.Resolution(size.ToString());
}
