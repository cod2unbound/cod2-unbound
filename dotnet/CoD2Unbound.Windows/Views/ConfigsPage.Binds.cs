using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private int bindView;
    private string bindQuery = "";
    private BindCapture? listening;
    private Action? finishListening;
    private readonly HashSet<(string Profile, EngineKey Key, string Text)> acceptedShared = [];
    private WindowsKeyboard? windowsKeyboard;
    private string? bindsProfile;
    private EngineKey? visualKey = EngineKey.Parse("w");
    private ConfigBindingEdit? bindApproval;
    private InfoBar bindNotice = new();

    private static string KeyLabel(EngineKey key) => WindowsBindInput.Label(key);

    private void ShowBinds(ConfigStudioState state, ConfigProfileRead profile)
    {
        StopListening();
        if (bindsProfile != profile.Profile.Id)
        {
            bindsProfile = profile.Profile.Id;
            chainDraft = null;
            bindApproval = null;
        }
        windowsKeyboard = new WindowsKeyboard();
        var binds = ConfigBindingDetails.ForWindows(profile.Document, model.Catalog, model.Model.Mode);
        var surface = new StackPanel { Spacing = 10 };
        var heading = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(heading, 0, new TextBlock { Text = "Binds", Style = StyleFor("SectionTitle"), VerticalAlignment = VerticalAlignment.Center });
        Put(heading, 1, Tabs(["List", "Visual"], bindView, index =>
        {
            bindView = index;
            bindApproval = null;
            RenderStudioBody();
        }));
        surface.Children.Add(heading);
        bindNotice = new InfoBar { IsClosable = true };
        surface.Children.Add(bindNotice);
        studioBody.Children.Add(surface);

        if (bindApproval is { Conflict: { } conflict, Key: { } key } edit)
        {
            string label = KeyLabel(key);
            string owner = ConfigBindingDetails.Title(binds, conflict, profile.Document.Dialect);
            string target = edit.Action?.Title ?? "this bind";
            BindStatus(InfoBarSeverity.Warning, "", $"{label} already does “{owner}”. Use it for {target} instead? “{owner}” loses this key.",
                Action($"Use {label} for {target}", () => SaveBind(edit, true)),
                SmallButton("Cancel", () => { bindApproval = null; bindNotice.IsOpen = false; }));
        }

        if (bindView == 1)
        {
            ShowVisualBinds(surface, state, profile, binds);
            return;
        }

        var list = new StackPanel { Spacing = 8 };
        var find = new TextBox { PlaceholderText = "Find an action or key", Text = bindQuery };
        studioFind = find;
        AutomationProperties.SetName(find, "Find an action or key");
        var shortcut = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        shortcut.Invoked += (_, args) =>
        {
            if (list.IsLoaded && listening is null)
                args.Handled = find.Focus(FocusState.Keyboard);
        };
        list.KeyboardAccelerators.Add(shortcut);
        list.Children.Add(find);
        var cards = new StackPanel { Spacing = 6 };
        list.Children.Add(cards);

        var workspace = new Grid();
        surface.Children.Add(workspace);
        BuildChainRail(cards, state, profile);
        var rows = new StackPanel { Spacing = 8 };
        list.Children.Add(rows);
        find.TextChanged += (_, _) => { bindQuery = find.Text; BuildRows(); };
        BuildRows();

        var editor = chainDraft is null || jumpBinding is not null ? null : ChainEditor(state, profile);
        workspace.Children.Add(list);
        if (editor is not null)
        {
            var back = SmallButton("‹ Binds", () => { chainDraft = null; RenderStudioBody(); });
            var right = new StackPanel { Spacing = 8, Children = { back, editor } };
            workspace.Children.Add(right);
            workspace.ColumnDefinitions.Add(new ColumnDefinition());
            workspace.ColumnDefinitions.Add(new ColumnDefinition());
            workspace.SizeChanged += (_, args) => Resize(args.NewSize.Width);
            Resize(studioBody.ActualWidth);

            void Resize(double width)
            {
                bool side = width >= 980;
                workspace.ColumnSpacing = side ? 16 : 0;
                workspace.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                workspace.ColumnDefinitions[1].Width = side ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                list.Visibility = IsVisible(side);
                Grid.SetColumn(right, side ? 1 : 0);
            }
        }

        void BuildRows()
        {
            rows.Children.Clear();
            foreach (var entry in ConfigBindingDetails.Shared(binds, profile.Document.Dialect))
            {
                var id = (profile.Profile.Id, entry.Key, entry.Text);
                var actions = ConfigBindingDetails.Actions(binds, entry, profile.Document.Dialect);
                string label = KeyLabel(entry.Key);
                if (acceptedShared.Contains(id) || !Matches($"{label} {ConfigBindingDetails.Title(binds, entry, profile.Document.Dialect)}"))
                    continue;
                var keep = Action($"Keep {label} for {actions[^1].Title} only", async () =>
                {
                    var change = binds.PrepareKey(profile, entry.Key, ConfigBindingDetails.Value(actions[^1]));
                    await SaveBind(change, true);
                });
                keep.IsEnabled = !state.ReadOnly;
                var bar = new InfoBar
                {
                    Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
                    Message = $"{label} does {(actions.Length == 2 ? "both " : "")}{ConfigBindingDetails.Title(binds, entry, profile.Document.Dialect)} in one press.",
                    Content = Buttons(keep, SmallButton(actions.Length == 2 ? "Keep both" : "Keep all", () =>
                    {
                        acceptedShared.Add(id);
                        BuildRows();
                    }))
                };
                rows.Children.Add(bar);
            }

            foreach (var group in binds.Actions.GroupBy(action => action.Group).OrderBy(group => group.Key))
            {
                var matching = group.Select(action => binds.Row(action.Name)!).Where(row =>
                    Matches(row.Action.Title) || Matches(row.Action.Name) || Matches(row.Action.Summary) || row.Keys.Any(key => Matches(KeyLabel(key))))
                    .OrderByDescending(row => row.Primary.HasValue).ToArray();
                if (matching.Length == 0)
                    continue;
                rows.Children.Add(new TextBlock { Text = BindingGroup(group.Key), Style = StyleFor("SectionTitle") });
                foreach (var row in matching)
                {
                    var primary = Slot(row, ConfigBindingSlot.Primary, row.Primary);
                    var second = Slot(row, ConfigBindingSlot.Secondary, row.Secondary);
                    var keys = new StackPanel { Spacing = 6, Children = { Buttons(primary, second) } };
                    foreach (var extra in row.Additional)
                        keys.Children.Add(Slot(row, null, extra));
                    var binding = SettingRow(row.Action.Title, row.Action.Summary, keys);
                    rows.Children.Add(binding);
                    if (row.Action.Name == jumpBinding)
                        jumpBindingRow = binding;
                }
            }
            if (rows.Children.Count == 0)
                rows.Children.Add(Caption("No actions or keys match."));
        }

        bool Matches(string value) => value.Contains(bindQuery.Trim(), StringComparison.CurrentCultureIgnoreCase);

        FrameworkElement Slot(ConfigBindingRow row, ConfigBindingSlot? slot, EngineKey? key)
        {
            string name = slot switch { ConfigBindingSlot.Primary => "Primary", ConfigBindingSlot.Secondary => "Second", _ => "Additional" };
            var button = new Button { Content = key is { } existing ? KeyLabel(existing) : "Add key", MinWidth = 85, IsEnabled = !state.ReadOnly };
            button.Style = StyleFor(key.HasValue ? "Keycap" : "AddKeycap");
            AutomationProperties.SetName(button, $"{row.Action.Title}, {name.ToLowerInvariant()} key, {button.Content}");
            button.Click += (_, _) => StartListening(button, pressed =>
            {
                var current = ForBindingEdit(profile.Profile.Id);
                var fresh = ConfigBindingDetails.ForWindows(current.Document, model.Catalog, model.Model.Mode);
                var edit = slot.HasValue ? fresh.Prepare(current, row.Action.Name, slot.Value, pressed) :
                    fresh.PrepareAdditional(current, row.Action.Name, key!.Value, pressed);
                return OfferBind(edit, current);
            });
            if (key.HasValue)
            {
                var menu = new MenuFlyout();
                var clear = new MenuFlyoutItem { Text = "Clear key", IsEnabled = !state.ReadOnly };
                clear.Click += async (_, _) => await shell.RunAsync(async () =>
                {
                    var current = ForBindingEdit(profile.Profile.Id);
                    var fresh = ConfigBindingDetails.ForWindows(current.Document, model.Catalog, model.Model.Mode);
                    await SaveBind(fresh.PrepareKey(current, key.Value, null), false);
                });
                menu.Items.Add(clear);
                button.ContextFlyout = menu;
            }
            return new StackPanel { Spacing = 2, Children = { Caption(name), button } };
        }
    }

    private ConfigProfileRead ForBindingEdit(string id)
    {
        if (model.State.Current is not { } current || current.Profile.Id != id || model.Studio.IsReadOnly)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        return current;
    }

    private Task OfferBind(ConfigBindingEdit edit, ConfigProfileRead profile)
    {
        if (edit.Key is { } key && profile.Metadata.ChainDefinitions is not null &&
            ConfigChains.OwnedChains(profile).Any(chain => EngineKey.Parse(chain.Key) == key))
        {
            BindStatus(InfoBarSeverity.Warning, "", $"{KeyLabel(key)} runs an advanced bind. Change its key in Advanced binds first.");
            return Task.CompletedTask;
        }
        if (edit.Conflict is null)
            return SaveBind(edit, false);
        bindApproval = edit;
        RenderStudioBody();
        return Task.CompletedTask;
    }

    private Task SaveBind(ConfigBindingEdit edit, bool replace)
    {
        model.Bind(edit, replace);
        if (bindView == 1 && edit.Key is { } key)
            visualKey = key;
        bindApproval = null;
        RenderStudioBody();
        return Task.CompletedTask;
    }

    private void StartListening(Button button, Func<EngineKey, Task> change)
    {
        StopListening();
        if (!button.Focus(FocusState.Programmatic))
            return;
        object original = button.Content;
        string name = AutomationProperties.GetName(button);
        var border = button.BorderBrush;
        var thickness = button.BorderThickness;
        button.Content = "Press a key…";
        AutomationProperties.SetName(button, $"{name}. Press a key or mouse button. Esc cancels.");
        button.BorderBrush = ThemeBrush("AccentFillColorDefaultBrush", ActualTheme, shell.IsHighContrast);
        button.BorderThickness = new Thickness(2);
        bindNotice.IsOpen = false;
        button.Unloaded += SlotUnloaded;
        finishListening = End;
        listening = new BindCapture(this, shell, End,
            (vk, scan, extended) => Capture((windowsKeyboard ?? new WindowsKeyboard()).Map((int)vk, scan, extended), vk),
            number => Capture(WindowsBindInput.Button(number)), delta => Capture(WindowsBindInput.Wheel(delta)));

        bool Capture(EngineKey? key, VirtualKey? vk = null)
        {
            if (key is null or { Reserved: true })
            {
                string name = vk switch
                {
                    VirtualKey.LeftWindows or VirtualKey.RightWindows => "The Windows key",
                    (VirtualKey)0x5d => "The Menu key", (VirtualKey)0x2c => "Print Screen", _ => "This key"
                };
                BindStatus(InfoBarSeverity.Warning, "", $"{name} can’t be used by the game.");
                return true;
            }
            End();
            _ = DispatcherQueue.TryEnqueue(async () => await shell.RunAsync(async () =>
            {
                try { await change(key.Value); }
                catch (ConfigChainException error) { BindStatus(InfoBarSeverity.Warning, "", error.Message); }
                catch (ConfigBindingException error) { BindStatus(InfoBarSeverity.Warning, "", error.Message); }
            }));
            return true;
        }

        void SlotUnloaded(object sender, RoutedEventArgs args) => End();

        void End()
        {
            button.Unloaded -= SlotUnloaded;
            finishListening = null;
            StopListening();
            button.Content = original;
            button.BorderBrush = border;
            button.BorderThickness = thickness;
            AutomationProperties.SetName(button, name);
        }
    }

    private void StopListening()
    {
        if (finishListening is { } finish)
        {
            finish();
            return;
        }
        var previous = listening;
        listening = null;
        previous?.Dispose();
    }

    private void BindStatus(InfoBarSeverity severity, string title, string message, params FrameworkElement[] actions)
    {
        bindNotice.Severity = severity;
        bindNotice.Title = title;
        bindNotice.Message = message;
        bindNotice.Content = actions.Length == 0 ? null : Buttons(actions);
        bindNotice.IsOpen = true;
    }

    private static Button SmallButton(string title, Action click)
    {
        var button = new Button { Content = title };
        button.Click += (_, _) => click();
        return button;
    }

    private static string BindingGroup(ConfigBindingGroup group) =>
        group == ConfigBindingGroup.GameUI ? "Game and UI" : group.ToString();
}
