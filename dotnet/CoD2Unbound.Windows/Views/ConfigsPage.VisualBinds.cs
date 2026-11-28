using System.Collections.Immutable;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private KeyboardSize keyboardSize = KeyboardSize.Full;
    private KeyboardShape? verifiedShape;
    private int mouseTab;
    private int chosenMouse;
    private readonly Dictionary<WindowsMouse, PointerButtonMap> learnedMice = [];
    private static readonly string[] PointerNames = ["Left button", "Right button", "Wheel button", "Back side button", "Forward side button"];
    private static readonly string[] PointerPrompts =
    [
        "Press the left button.", "Press the right button.", "Press down on the scroll wheel.",
        "Press the side button nearer your wrist (usually Back).", "Press the side button further forward (usually Forward)."
    ];

    private void ShowVisualBinds(StackPanel surface, ConfigStudioState state, ConfigProfileRead profile, ConfigBindings binds)
    {
        var keyboard = windowsKeyboard!;
        bool extraLarge = ExtraLargeText;
        var details = new StackPanel { Spacing = 4 };
        var keyTiles = new Dictionary<Button, EngineKey>();
        var keyboardDrawing = new StackPanel { Style = StyleFor("StudioKeyboardDrawing") };
        var keyboardViewport = new ScrollViewer { Style = StyleFor("StudioKeyboardViewport"), Content = keyboardDrawing };
        keyboardViewport.SizeChanged += (_, args) => ResizeDrawing(args.NewSize.Width);
        var legends = new List<(Button Tile, ColumnDefinition Column, TextBlock Text)>();
        if (extraLarge)
            keyboardDrawing.Loaded += (_, _) => MeasureConnectedLegends();
        var keyboardHeader = Columns(16, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        var settings = new Button { Style = StyleFor("SmallAction") };
        AutomationProperties.SetName(settings, "Keyboard layout and size");
        UpdateKeyboardLabel();
        var picker = new ComboBox { ItemsSource = KeyboardSizes.Select(PcKeyboardVerification.SizeName).ToArray(),
            SelectedIndex = Array.IndexOf(KeyboardSizes, keyboardSize) };
        AutomationProperties.SetName(picker, "Size");
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex >= 0)
            {
                keyboardSize = KeyboardSizes[picker.SelectedIndex];
                UpdateKeyboardLabel();
                DrawKeys();
            }
        };
        var shapes = new KeyboardShape?[] { null, KeyboardShape.Ansi, KeyboardShape.Iso, KeyboardShape.Jis };
        var shape = new ComboBox { ItemsSource = new[] { "Automatic", "ANSI", "ISO", "JIS" },
            SelectedIndex = Array.IndexOf(shapes, verifiedShape) };
        AutomationProperties.SetName(shape, "Key shape");
        shape.SelectionChanged += (_, _) =>
        {
            if (shape.SelectedIndex >= 0)
            {
                verifiedShape = shapes[shape.SelectedIndex];
                DrawKeys();
            }
        };
        var verify = Action("Press Keys to Verify…", async () =>
        {
            (settings.Flyout as Flyout)?.Hide();
            await VerifyKeyboardAsync();
        });
        settings.Flyout = new Flyout { Content = new StackPanel { Spacing = 12, MaxWidth = 380, Children =
        {
            CheckStrong("Keyboard"), Caption("Connected · detected automatically"),
            SettingRow("Key shape", null, shape), SettingRow("Size", null, picker),
            SettingRow("Modifier labels", "Ctrl / Win / Alt"), SettingRow("Layout", $"{keyboard.Name} · from Windows"),
            Caption("Shape and layout come from Windows. Change the size if the drawing looks wrong. Labels follow your keyboard; binds stay on the same game keys."),
            verify
        }}};
        Put(keyboardHeader, 0, settings);
        var instruction = Caption("Click any key or button to see or change what it does.");
        instruction.VerticalAlignment = VerticalAlignment.Center;
        instruction.HorizontalAlignment = HorizontalAlignment.Right;
        Put(keyboardHeader, 1, instruction);

        var legend = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Key colours", Style = StyleFor("GroupLabelText") } } };
        foreach (var group in Enum.GetValues<ConfigBindingGroup>())
        {
            var swatch = new Border { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Background = GroupBrush(group) };
            legend.Children.Add(Buttons(swatch, Caption(BindingGroup(group))));
        }
        legend.Children.Add(Caption("▧ Game can’t use this key"));
        AutomationProperties.SetName(keyboardViewport, "PC keyboard");
        var keysAndLegend = CheckPair(keyboardViewport, legend, 690, rightAuto: true);
        var keyboardPanel = new StackPanel { Spacing = 16, Children = { keyboardHeader, keysAndLegend } };
        surface.Children.Add(new Border { Style = StyleFor("FieldCard"), Child = keyboardPanel });

        var peripheral = new StackPanel { Spacing = 10 };
        var pointer = new StackPanel { Spacing = 6 };
        var pointerTabs = Tabs(["Mouse", "Touchpad"], mouseTab, index => { mouseTab = index; DrawPointer(); });
        AutomationProperties.SetName(pointerTabs, "Pointing device");
        peripheral.Children.Add(pointerTabs);
        peripheral.Children.Add(pointer);
        peripheral.Children.Add(MouseDpiRow(state, profile));
        var pointerCard = new Border { Style = StyleFor("FieldCard"), Child = peripheral };
        var keyCard = new Border { Style = StyleFor("FieldCard"), Child = details };
        var lower = CheckPair(pointerCard, keyCard, 1040);
        lower.SizeChanged += (_, args) =>
        {
            // At narrow widths keep the selected key beside the keyboard it describes.
            bool compact = args.NewSize.Width < 1040;
            Grid.SetRow(pointerCard, compact ? 1 : 0);
            Grid.SetRow(keyCard, 0);
        };
        surface.Children.Add(lower);
        DrawKeys();
        DrawPointer();
        ShowSelected();

        void UpdateKeyboardLabel() => settings.Content =
            IconLabel("Desktop", $"{keyboard.Name} · {PcKeyboardVerification.SizeName(keyboardSize)}", 16);

        var layoutWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        layoutWatch.Tick += (_, _) =>
        {
            if (listening is null && WindowsKeyboard.ActiveLayout != keyboard.Layout && surface.IsLoaded)
                RenderStudioBody();
        };
        surface.Loaded += (_, _) => layoutWatch.Start();
        surface.Unloaded += (_, _) => layoutWatch.Stop();

        void DrawKeys()
        {
            keyTiles.Clear();
            legends.Clear();
            keyboardDrawing.Children.Clear();
            foreach (var layoutRow in PcKeyRows(verifiedShape ?? keyboard.Shape, keyboardSize))
            {
                var row = new Grid { Style = StyleFor("StudioKeyboardRow") };
                foreach (var key in layoutRow)
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(key.Width, GridUnitType.Star) });
                for (int index = 0; index < layoutRow.Length; index++)
                {
                    var shape = layoutRow[index];
                    if (shape.Scan == 0)
                        continue;
                    var (key, label) = keyboard.Describe(shape.Scan, shape.Caption);
                    string fullLabel = extraLarge ? FullKeyLegend(label) : label;
                    var actions = KeyActions(key);
                    bool unavailable = key is null or { Reserved: true } || key.Value.Number == 27;
                    var tile = SmallButton(label, () =>
                    {
                        if (key is { } chosen && !unavailable)
                            SelectKey(chosen);
                        else
                            BindStatus(InfoBarSeverity.Informational, "", $"{fullLabel} can’t be used by the game.");
                    });
                    tile.Style = StyleFor("StudioKeyboardKey");
                    var keyText = new TextBlock
                    {
                        Text = label,
                        Style = StyleFor("StudioKeyboardLegend")
                    };
                    if (extraLarge)
                        keyText.TextTrimming = TextTrimming.None;
                    var keyFace = new StackPanel { Children = { keyText } };
                    if (unavailable || !actions.IsEmpty)
                        keyFace.Children.Add(new TextBlock
                        {
                            Text = unavailable ? "▧" : actions[0].Title,
                            Style = StyleFor("StudioKeyboardDetail")
                        });
                    tile.Content = keyFace;
                    var column = row.ColumnDefinitions[index];
                    if (extraLarge)
                        legends.Add((tile, column, keyText));
                    else
                        MeasureLegend(tile, column, keyText);
                    if (!actions.IsEmpty && !shell.IsHighContrast)
                        tile.Background = GroupBrush(actions[0].Group);
                    if (key.HasValue && !unavailable)
                        keyTiles[tile] = key.Value;
                    string accessible = unavailable ? $"{fullLabel}, unavailable in game" : actions.IsEmpty
                        ? $"{fullLabel}, unassigned" : $"{fullLabel}, {string.Join(" and ", actions.Select(action => action.Title))}";
                    AutomationProperties.SetName(tile, accessible);
                    ToolTipService.SetToolTip(tile, accessible);
                    Put(row, index, tile);
                }
                keyboardDrawing.Children.Add(row);
            }
            if (extraLarge)
                DispatcherQueue.TryEnqueue(MeasureConnectedLegends);
            else
                FitMeasuredDrawing();
            MarkSelected();
        }

        void MeasureConnectedLegends()
        {
            if (!keyboardDrawing.IsLoaded)
                return;
            // Re-measure after WinUI attaches the keys to the root and applies text scaling.
            foreach (var (tile, column, text) in legends)
            {
                text.InvalidateMeasure();
                MeasureLegend(tile, column, text);
            }
            FitMeasuredDrawing();
        }

        static void MeasureLegend(Button tile, ColumnDefinition column, TextBlock text)
        {
            text.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            column.MinWidth = Math.Ceiling(text.DesiredSize.Width) + tile.Padding.Left + tile.Padding.Right +
                tile.BorderThickness.Left + tile.BorderThickness.Right;
        }

        void FitMeasuredDrawing()
        {
            double minimumWidth = keyboardDrawing.Children.OfType<Grid>().Max(row =>
                row.ColumnDefinitions.Sum(column => column.MinWidth) + row.ColumnSpacing * (row.ColumnDefinitions.Count - 1));
            keyboardDrawing.MinWidth = minimumWidth + keyboardDrawing.Padding.Left + keyboardDrawing.Padding.Right;
            ResizeDrawing(keyboardViewport.ActualWidth);
        }

        void ResizeDrawing(double viewportWidth) => keyboardDrawing.Width = Math.Max(viewportWidth, keyboardDrawing.MinWidth);

        void MarkSelected()
        {
            foreach (var (tile, key) in keyTiles)
                if (key == visualKey)
                    tile.BorderBrush = ThemeBrush("AccentBrush", ActualTheme, shell.IsHighContrast);
                else
                    tile.ClearValue(Control.BorderBrushProperty);
        }

        void DrawPointer()
        {
            pointer.Children.Clear();
            pointer.Children.Add(new TextBlock { Text = mouseTab == 1 ? "Precision touchpad" : "Mouse", Style = StyleFor("SectionTitle") });
            if (mouseTab == 1)
            {
                pointer.Children.Add(Body("Primary click sends Mouse 1. Secondary click sends Mouse 2. Two-finger scrolling sends the wheel, as set in Windows Settings › Bluetooth & devices › Touchpad."));
                return;
            }
            var mice = WindowsMouse.Read();
            if (chosenMouse >= mice.Count)
                chosenMouse = 0;
            var mouse = mice.ElementAtOrDefault(chosenMouse) ?? new WindowsMouse("Mouse", null);
            if (mice.Count > 1)
            {
                var devices = new ComboBox { Header = "Mouse", Height = double.NaN,
                    ItemsSource = mice.Select(item => item.Name).ToArray(), SelectedIndex = chosenMouse };
                devices.SelectionChanged += (_, _) =>
                {
                    if (devices.SelectedIndex >= 0)
                    {
                        chosenMouse = devices.SelectedIndex;
                        DrawPointer();
                    }
                };
                pointer.Children.Add(devices);
            }
            else
                pointer.Children.Add(Caption(mouse.Name));
            var mouseColumns = Columns(12, GridLength.Auto, new GridLength(1, GridUnitType.Star));
            Put(mouseColumns, 0, MouseDrawing(null, position =>
            {
                if (MouseKey(mouse, position) is { } key)
                    SelectKey(key);
            }));
            var rows = new StackPanel { Spacing = 3 };
            foreach (var position in Enum.GetValues<PointerButtonPosition>())
            {
                var key = MouseKey(mouse, position);
                var actions = KeyActions(key);
                var button = SmallButton(PointerNames[(int)position], () => { if (key is { } selected) SelectKey(selected); });
                button.IsEnabled = key.HasValue;
                AutomationProperties.SetName(button, $"{PointerNames[(int)position]}, {(key is { } mouseKey ? KeyLabel(mouseKey) : "no reported button")}");
                rows.Children.Add(SettingRow(actions.IsEmpty ? "Unassigned" : string.Join(" and ", actions.Select(action => action.Title)),
                    key is { } assigned ? KeyLabel(assigned) : null, button, grouped: true));
            }
            rows.Children.Add(Buttons(SmallButton("Wheel up", () => SelectKey(WindowsBindInput.Wheel(1)!.Value)),
                SmallButton("Wheel down", () => SelectKey(WindowsBindInput.Wheel(-1)!.Value))));
            Put(mouseColumns, 1, rows);
            pointer.Children.Add(mouseColumns);
            pointer.Children.Add(Action("Learn button positions…", async () =>
            {
                await LearnMouseAsync(mouse);
                DrawPointer();
            }));
            pointer.Children.Add(Caption(mouse.Buttons is { } count
                ? $"This mouse reports {count} buttons. Buttons 6 and up can’t be used by the original game. Map them to a key in your mouse’s software."
                : "Windows didn’t report a button count. Buttons 6 and up can’t be used by the original game. Map them to a key in your mouse’s software."));
            pointer.Children.Add(Caption(profile.Metadata.Dpi is { } dpi
                ? $"{dpi:G} DPI · you entered this · Polling rate not measured" : "DPI not entered · Polling rate not measured"));
        }

        EngineKey? MouseKey(WindowsMouse mouse, PointerButtonPosition position)
        {
            if (learnedMice.TryGetValue(mouse, out var map))
                return map.Buttons.TryGetValue(position, out int learned) ? WindowsBindInput.Button(learned + 1) : null;
            return mouse.Buttons is not { } count || (int)position < count ? WindowsBindInput.Button((int)position + 1) : null;
        }

        ImmutableArray<ConfigBindingAction> KeyActions(EngineKey? key) =>
            binds.Entries.FirstOrDefault(entry => entry.Key == key) is { } binding
                ? ConfigBindingDetails.Actions(binds, binding, profile.Document.Dialect) : [];

        void SelectKey(EngineKey key)
        {
            visualKey = key;
            ShowSelected();
            MarkSelected();
        }

        void ShowSelected()
        {
            details.Children.Clear();
            if (visualKey is not { } key)
                return;
            var binding = binds.Entries.FirstOrDefault(entry => entry.Key == key);
            var actions = KeyActions(key);
            string label = KeyLabel(key);
            string text = actions.IsEmpty ? binding is null ? "Unassigned" : "Advanced or console bind" :
                string.Join(" and ", actions.Select(action => action.Title));
            details.Children.Add(new TextBlock { Text = "Selected key", Style = StyleFor("GroupLabelText") });
            details.Children.Add(new TextBlock { Text = $"{label} · {text}", Style = StyleFor("SectionTitle"), TextWrapping = TextWrapping.Wrap });
            if (!actions.IsEmpty)
                details.Children.Add(Caption(BindingGroup(actions[0].Group)));
            if (binding is not null)
            {
                details.Children.Add(Body($"Pressing {label} does this in game."));
                if (!actions.IsEmpty)
                {
                    var keys = actions.SelectMany(action => binds.Row(action.Name)?.Keys ?? []).Append(key).Distinct().Select(KeyLabel);
                    details.Children.Add(Body(string.Join(" · ", keys)));
                }
                details.Children.Add(new TextBlock { Text = $"bind {key.Spelling} \"{binding.Text}\"",
                    FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            }
            var owned = ReadVisualChain(profile, key);
            if (owned is not null)
            {
                details.Children.Add(SmallButton("Edit advanced bind…", () =>
                {
                    chainDraft = ConfigChainDraft.From(owned);
                    bindView = 0;
                    RenderStudioBody();
                }));
                return;
            }
            var change = new Button { Content = "Change…", Style = StyleFor("AccentButtonStyle"), IsEnabled = !state.ReadOnly };
            change.Click += (_, _) =>
            {
                if (binding is not null)
                {
                    StartListening(change, pressed =>
                    {
                        var current = ForBindingEdit(profile.Profile.Id);
                        var fresh = ConfigBindingDetails.ForWindows(current.Document, model.Catalog, model.Model.Mode);
                        var edit = fresh.PrepareMove(current, key, pressed);
                        return OfferBind(edit, current);
                    });
                }
                else
                {
                    var actionsBox = new ComboBox { Header = "Action", Height = double.NaN, ItemsSource = binds.Actions,
                        DisplayMemberPath = "Title", MinWidth = 220 };
                    var dialog = Dialog($"Bind {label}", actionsBox, "Set binding");
                    dialog.IsPrimaryButtonEnabled = false;
                    actionsBox.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = actionsBox.SelectedIndex >= 0;
                    _ = shell.RunAsync(async () =>
                    {
                        if (await dialog.ShowSheetAsync() == ContentDialogResult.Primary && actionsBox.SelectedItem is ConfigBindingAction action)
                        {
                            var current = ForBindingEdit(profile.Profile.Id);
                            var fresh = ConfigBindingDetails.ForWindows(current.Document, model.Catalog, model.Model.Mode);
                            await OfferBind(fresh.PrepareKey(current, key, ConfigBindingDetails.Value(action)), current);
                        }
                    });
                }
            };
            var clear = Action("Clear", async () =>
            {
                var current = ForBindingEdit(profile.Profile.Id);
                var fresh = ConfigBindingDetails.ForWindows(current.Document, model.Catalog, model.Model.Mode);
                await SaveBind(fresh.PrepareKey(current, key, null), false);
            });
            clear.IsEnabled = !state.ReadOnly && binding is not null;
            details.Children.Add(Buttons(change, clear));
        }
    }

    private static ConfigChain? ReadVisualChain(ConfigProfileRead profile, EngineKey key)
    {
        try { return ConfigChains.OwnedChains(profile).FirstOrDefault(chain => EngineKey.Parse(chain.Key) == key); }
        catch (ConfigChainException) { return null; }
    }

    private FrameworkElement MouseDpiRow(ConfigStudioState state, ConfigProfileRead profile)
    {
        var number = new NumberBox { Value = ConfigMouseDpi.For(profile), Minimum = 1, Width = 116,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, IsEnabled = !state.ReadOnly };
        AutomationProperties.SetName(number, "Your mouse’s DPI");
        number.ValueChanged += async (_, args) =>
        {
            if (!number.IsLoaded || (double.IsNaN(args.NewValue) && !profile.Metadata.Dpi.HasValue) ||
                args.NewValue == profile.Metadata.Dpi)
                return;
            await shell.RunAsync(() =>
            {
                ConfigMouseDpi.Save(model.Studio, ForBindingEdit(profile.Profile.Id),
                    double.IsFinite(args.NewValue) ? args.NewValue : null);
                RenderStudioBody();
                return Task.CompletedTask;
            });
        };
        string detail = studioTab == 1
            ? "Your mouse’s DPI isn’t shared with Windows, so mouse speed uses the number you enter here. It’s saved with this loadout."
            : "CoD2 Unbound uses it only to work out how far you turn. The game never reads it.";
        return SettingRow("Your mouse’s DPI", detail, number, grouped: studioTab == 0);
    }

    private Brush GroupBrush(ConfigBindingGroup group)
    {
        if (shell.IsHighContrast)
            return ThemeBrush("PanelBrush", ActualTheme, true);
        bool light = ActualTheme == ElementTheme.Light;
        var color = group switch
        {
            ConfigBindingGroup.Movement => light ? ColorHelper.FromArgb(255, 194, 223, 248) : ColorHelper.FromArgb(255, 39, 73, 103),
            ConfigBindingGroup.Communication => light ? ColorHelper.FromArgb(255, 224, 207, 244) : ColorHelper.FromArgb(255, 85, 62, 106),
            ConfigBindingGroup.Combat => light ? ColorHelper.FromArgb(255, 246, 214, 201) : ColorHelper.FromArgb(255, 111, 63, 57),
            _ => light ? ColorHelper.FromArgb(255, 204, 231, 210) : ColorHelper.FromArgb(255, 49, 88, 67)
        };
        return new SolidColorBrush(color);
    }

    private static readonly KeyboardSize[] KeyboardSizes =
        [KeyboardSize.Full, KeyboardSize.Tkl, KeyboardSize.Percent75, KeyboardSize.Percent65, KeyboardSize.Percent60];

    private readonly record struct PcKey(int Scan, string? Caption = null, double Width = 1);

    private static string FullKeyLegend(string legend) => legend switch
    {
        "Esc" => "Escape", "Back" => "Backspace", "Caps" => "Caps Lock", "Ctrl" => "Control",
        "Win" => "Windows", "PrtSc" => "Print Screen", "Scroll" => "Scroll Lock", "Num" => "Num Lock",
        "Ins" => "Insert", "Del" => "Delete", "PgUp" => "Page Up", "PgDn" => "Page Down",
        "↑" => "Up Arrow", "↓" => "Down Arrow", "←" => "Left Arrow", "→" => "Right Arrow",
        _ => legend
    };

    private static IEnumerable<PcKey[]> PcKeyRows(KeyboardShape shape, KeyboardSize size)
    {
        var f = new[] { new PcKey(1, "Esc") }.Concat(Enumerable.Range(0, 12)
            .Select(index => new PcKey(index < 10 ? 0x3b + index : 0x57 + index - 10, $"F{index + 1}"))).ToArray();
        var number = new[] { new PcKey(0x29) }.Concat(Enumerable.Range(2, 12).Select(scan => new PcKey(scan)))
            .Append(new(0x0e, "Back", 2)).ToArray();
        var top = new[] { new PcKey(0x0f, "Tab", 1.5) }.Concat(Enumerable.Range(0x10, 12).Select(scan => new PcKey(scan)))
            .Append(new(shape == KeyboardShape.Iso ? 0x1c : 0x2b, shape == KeyboardShape.Iso ? "Enter" : null, 1.5)).ToArray();
        var middle = new[] { new PcKey(0x3a, "Caps", 1.75) }.Concat(Enumerable.Range(0x1e, 11).Select(scan => new PcKey(scan)))
            .Concat(shape == KeyboardShape.Iso ? [new(0x2b), new(0, null, 1.25)] : new PcKey[] { new(0x1c, "Enter", 2.25) }).ToArray();
        var bottom = new[] { new PcKey(0x2a, "Shift", shape == KeyboardShape.Iso ? 1.25 : 2.25) }
            .Concat(shape == KeyboardShape.Iso ? [new PcKey(0x56)] : [])
            .Concat(Enumerable.Range(0x2c, 10).Select(scan => new PcKey(scan)))
            .Concat(shape == KeyboardShape.Jis ? [new PcKey(0x73), new(0x36, "Shift", 1.75)] : new PcKey[] { new(0x36, "Shift", 2.75) }).ToArray();
        PcKey[] modifiers = [new(0x1d, "Ctrl", 1.25), new(0xe05b, "Win", 1.25), new(0x38, "Alt", 1.25),
            new(0x39, "Space", 6.25), new(0xe038, "Alt", 1.25), new(0xe05c, "Win", 1.25), new(0xe05d, "Menu", 1.25), new(0xe01d, "Ctrl", 1.25)];
        if (shape == KeyboardShape.Jis)
            modifiers = [new(0x1d, "Ctrl", 1.25), new(0xe05b, "Win", 1.25), new(0x38, "Alt", 1.25), new(0x7b, "無変換", 1.25),
                new(0x39, "Space", 3.75), new(0x79, "変換", 1.25), new(0x70, "かな", 1.25), new(0xe038, "Alt", 1.25), new(0xe05d, "Menu", 1.25), new(0xe01d, "Ctrl", 1.25)];
        if (size == KeyboardSize.Percent60)
            number[0] = new(1, "Esc");
        if (size is KeyboardSize.Full or KeyboardSize.Tkl)
        {
            bool full = size == KeyboardSize.Full;
            yield return [.. f, new(0, null, 2), new(0xe037, "PrtSc"), new(0x46, "Scroll"), new(0x45, "Pause"), .. (full ? new PcKey[] { new(0, null, 4) } : [])];
            yield return [.. number, new(0, null, .4), new(0xe052, "Ins"), new(0xe047, "Home"), new(0xe049, "PgUp"), .. Pad(new(0x45, "Num"), new(0xe035, "/"), new(0x37, "*"), new(0x4a, "−"))];
            yield return [.. top, new(0, null, .4), new(0xe053, "Del"), new(0xe04f, "End"), new(0xe051, "PgDn"), .. Pad(new(0x47, "7"), new(0x48, "8"), new(0x49, "9"), new(0x4e, "+"))];
            yield return [.. middle, new(0, null, 3.4), .. Pad(new(0x4b, "4"), new(0x4c, "5"), new(0x4d, "6"), new(0))];
            yield return [.. bottom, new(0, null, 1.4), new(0xe048, "↑"), new(0), .. Pad(new(0x4f, "1"), new(0x50, "2"), new(0x51, "3"), new(0xe01c, "Enter"))];
            yield return [.. modifiers, new(0, null, .4), new(0xe04b, "←"), new(0xe050, "↓"), new(0xe04d, "→"), .. Pad(new(0x52, "0", 2), new(0x53, "."), new(0))];

            PcKey[] Pad(params PcKey[] keys) => full
                ? [new(0, null, .4), .. keys.Select(key => key.Scan is >= 0x47 and <= 0x53 && key.Caption is not ("+" or "−") ? key with { Scan = key.Scan | 0x100 } : key)]
                : [];
        }
        else
        {
            if (size == KeyboardSize.Percent75)
                yield return f;
            yield return size == KeyboardSize.Percent60 ? number : [.. number, new(0xe053, "Del")];
            yield return top;
            yield return middle;
            yield return size == KeyboardSize.Percent60 ? bottom : [.. bottom, new(0xe048, "↑")];
            yield return size == KeyboardSize.Percent60 ? modifiers : [.. modifiers, new(0xe04b, "←"), new(0xe050, "↓"), new(0xe04d, "→")];
        }
    }

    private async Task VerifyKeyboardAsync()
    {
        var verification = new PcKeyboardVerification();
        var panel = new StackPanel { Spacing = 8 };
        var prompt = Body("");
        var note = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false };
        var sizes = new RadioButtons { Visibility = Visibility.Collapsed };
        panel.Children.Add(prompt);
        panel.Children.Add(note);
        panel.Children.Add(sizes);
        var dialog = Dialog("Verify your keyboard", panel, "Confirm size");
        dialog.SecondaryButtonText = "I don’t have this key";
        dialog.SecondaryButtonClick += (_, args) => { args.Cancel = true; verification.Skip(); Update(); };
        sizes.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = sizes.SelectedIndex >= 0;
        BindCapture? capture = null;
        dialog.Opened += (_, _) => capture = new BindCapture(dialog, shell, dialog.Hide, (key, scan, extended) =>
        {
            if (!verification.Sizes.IsEmpty || key is VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Space)
                return false;
            verification.Press(scan, extended);
            Update();
            return true;
        });
        dialog.Closed += (_, _) => capture?.Dispose();
        Update();
        if (await dialog.ShowSheetAsync() == ContentDialogResult.Primary && sizes.SelectedIndex >= 0)
        {
            keyboardSize = verification.Sizes[sizes.SelectedIndex];
            verifiedShape = verification.Shape;
            RenderStudioBody();
        }

        void Update()
        {
            prompt.Text = verification.Prompt;
            note.IsOpen = verification.Note is not null;
            note.Message = verification.Note ?? "";
            dialog.IsSecondaryButtonEnabled = verification.CanSkip;
            if (verification.Sizes.IsEmpty)
            {
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }
            sizes.ItemsSource = verification.Sizes.Select(PcKeyboardVerification.SizeName).ToArray();
            sizes.Visibility = Visibility.Visible;
            sizes.SelectedIndex = verification.Sizes.Length == 1 ? 0 : -1;
        }
    }

    private async Task LearnMouseAsync(WindowsMouse mouse)
    {
        var flow = new PointerButtonLearning();
        var prompt = Body("");
        var drawing = new ContentControl();
        var note = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Warning };
        var finish = new StackPanel { Spacing = 4 };
        var captureSurface = new StackPanel { Spacing = 8, Padding = new Thickness(8), Background = new SolidColorBrush(Colors.Transparent),
            Children = { prompt, drawing, note, finish } };
        var dialog = Dialog("Learn your mouse buttons", new StackPanel { Spacing = 8, Children =
        {
            captureSurface, Caption("Press a button to record it. Press Enter if your mouse doesn’t have this button. Esc stops.")
        }}, "Done");
        dialog.SecondaryButtonText = "I don’t have this button";
        dialog.SecondaryButtonClick += (_, args) => { args.Cancel = true; flow.Skip(); Update(); };
        BindCapture? pointer = null;
        BindCapture? keys = null;
        dialog.Opened += (_, _) =>
        {
            pointer = new BindCapture(captureSurface, shell, dialog.Hide, (_, _, _) => false, number =>
            {
                if (flow.IsDone)
                    return false;
                flow.Click(number - 1);
                Update();
                return true;
            });
            keys = new BindCapture(dialog, shell, dialog.Hide, (key, _, _) =>
            {
                if (key != VirtualKey.Enter || flow.IsDone)
                    return false;
                flow.Skip();
                Update();
                return true;
            });
        };
        dialog.Closed += (_, _) => { pointer?.Dispose(); keys?.Dispose(); };
        Update();
        if (await dialog.ShowSheetAsync() == ContentDialogResult.Primary && flow.Result is { } result)
            learnedMice[mouse] = result;

        void Update()
        {
            prompt.Text = flow.IsDone ? "Done." : flow.Position is { } position ? PointerPrompts[(int)position] :
                "Press any other buttons your mouse has, one at a time. Press Enter when you’re done.";
            drawing.Content = MouseDrawing(flow.Position);
            note.Message = flow.Note ?? "";
            note.IsOpen = flow.Note is not null;
            dialog.IsPrimaryButtonEnabled = flow.IsDone;
            dialog.IsSecondaryButtonEnabled = !flow.IsDone;
            dialog.SecondaryButtonText = flow.IsCollectingExtras ? "Finish learning" : "I don’t have this button";
            if (flow.Result is { } result && finish.Children.Count == 0)
                foreach (var (physical, index) in result.Buttons.OrderBy(pair => pair.Key))
                    finish.Children.Add(Caption($"{PointerNames[(int)physical]} · Mouse {index + 1}"));
        }
    }

    private Border MouseDrawing(PointerButtonPosition? mark, Action<PointerButtonPosition>? select = null)
    {
        var body = new StackPanel { Spacing = 5 };
        body.Children.Add(Buttons(Tile(PointerButtonPosition.Left, "L"), Tile(PointerButtonPosition.Wheel, "●"), Tile(PointerButtonPosition.Right, "R")));
        body.Children.Add(Buttons(Tile(PointerButtonPosition.Back, "‹"), Tile(PointerButtonPosition.Forward, "›")));
        return new Border { Child = body, CornerRadius = new CornerRadius(28, 28, 42, 42), Padding = new Thickness(9, 12, 9, 28),
            BorderThickness = new Thickness(1), BorderBrush = ThemeBrush("PanelBrush", ActualTheme, shell.IsHighContrast),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

        FrameworkElement Tile(PointerButtonPosition position, string label)
        {
            if (select is null)
                return new Border
                {
                    Background = mark == position ? ThemeBrush("AccentBrush", ActualTheme, shell.IsHighContrast) : null,
                    Padding = new Thickness(10), CornerRadius = new CornerRadius(4), Child = Body(label)
                };
            var button = SmallButton(label, () => select(position));
            button.MinWidth = 28;
            button.Padding = new Thickness(6);
            AutomationProperties.SetName(button, PointerNames[(int)position]);
            return button;
        }
    }
}
