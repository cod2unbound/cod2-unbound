using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private bool focusResolution;

    private Border DisplayResolutionRow(ConfigStudioState state, string profileId, ConfigSettingSpec spec, string? saved, string? block)
    {
        var settings = model.DisplaySettings;
        string requested = saved ?? settings.Resolution;
        var display = WindowsDisplays.ForLoadout(WindowsDisplayInventory.ReadForGame(), settings.WindowsMonitor,
            state.Current?.Document.EffectiveSetting("r_monitor"));
        var choice = new ComboBox { MinWidth = 160, IsEnabled = block is null && display is { Modes.IsEmpty: false } };
        AutomationProperties.SetName(choice, "Display resolution");
        if (display is not null)
        {
            foreach (var mode in display.Modes)
            {
                string name = SettingsPage.SizeText(mode.Size) + (mode.Size == display.Native ? " · Native" : "");
                var option = new ComboBoxItem { Content = name, Tag = mode.Size.ToString() };
                choice.Items.Add(option);
                if (mode.Size.ToString() == requested)
                    choice.SelectedItem = option;
            }
        }
        choice.PlaceholderText = LauncherText.Resolution(requested);
        var note = Caption("");
        var row = SettingRow(spec.Title, block ?? DisplaySizeNote(display), choice, grouped: !expert);
        var content = (Grid)row.Child;
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(note, 1);
        Grid.SetColumnSpan(note, 2);
        note.Margin = new Thickness(0, 4, 0, 0);
        content.Children.Add(note);
        UpdateNote();
        if (focusResolution)
        {
            focusResolution = false;
            row.Loaded += (_, _) =>
            {
                row.StartBringIntoView();
                if (choice.IsEnabled)
                    choice.Focus(FocusState.Programmatic);
            };
        }
        choice.SelectionChanged += async (_, _) =>
        {
            if (choice.SelectedItem is not ComboBoxItem { Tag: string selected } || selected == requested)
                return;
            await shell.RunAsync(() =>
            {
                if (model.State.Current is { } current && current.Profile.Id == profileId)
                {
                    model.Set(current, spec, selected);
                    requested = selected;
                    UpdateNote();
                }
                return Task.CompletedTask;
            });
        };
        return row;

        void UpdateNote()
        {
            var adjustment = WindowsDisplays.Adjustment(requested, display);
            note.Visibility = IsVisible(adjustment.HasValue);
            if (adjustment is { } size)
                note.Text = $"This display can't show {LauncherText.Resolution(requested)}. The game will use {SettingsPage.SizeText(size)}.";
        }
    }

    private static string DisplaySizeNote(WindowsDisplay? display) =>
        display?.Native is { } native
            ? $"This display's own size is {SettingsPage.SizeText(native)} pixels. Larger sizes are sharper and slower; anti-aliasing multiplies the work."
            : display is null ? "Windows hasn't reported display modes." :
            $"The desktop uses {SettingsPage.SizeText(display.Current)} pixels. Larger sizes are sharper and slower; anti-aliasing multiplies the work.";
}
