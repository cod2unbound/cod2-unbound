using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private readonly Grid configToolbar = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
    private TextBlock? studioChangedCount;
    private Button? studioNext;
    private SelectorBar? bindModes;
    private ContentPresenter? studioHeaderExtra;

    private void PositionConfigToolbar()
    {
        if (XamlRoot is null)
            return;
        double leading = ConfigSurface.TransformToVisual(null).TransformPoint(new global::Windows.Foundation.Point()).X;
        var margin = new Thickness(leading + ConfigSurface.ColumnDefinitions[0].Width.Value + 24, 16, 18, 0);
        if (!configToolbar.Margin.Equals(margin))
            configToolbar.Margin = margin;
    }

    private void ShowConfigStudioToolbar(SelectorBar tabs, ConfigProfileRead profile)
    {
        AutomationProperties.SetName(tabs, "Loadout section");
        tabs.Style = StyleFor("SegmentedLarge");
        tabs.VerticalAlignment = VerticalAlignment.Center;
        var actions = Buttons();
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        AddStudioSnapshotCommand(actions, profile);

        var jump = new Button
        {
            Style = StyleFor("ToolbarPill"), Content = IconLabel("Search", "Jump to Any Setting  Ctrl+K", 18)
        };
        jump.Click += (_, _) => shell.OpenJumpTo();
        AutomationProperties.SetName(jump, "Jump to Any Setting");
        ToolTipService.SetToolTip(jump, "Jump to any setting (Ctrl+K)");
        actions.Children.Add(jump);
        var more = new Button { Style = StyleFor("ToolbarPill"), Width = 44, Padding = new Thickness(0), Content = Fluent("More", 20) };
        AutomationProperties.SetName(more, "More Actions for This Loadout");
        ToolTipService.SetToolTip(more, "More actions for this loadout");
        more.Flyout = LoadoutMenu(profile.Profile, includeNew: true);
        actions.Children.Add(more);

        var snapshot = historySnapshotButton!;
        SizeChangedEventHandler fit = (_, _) => FitToolbar();
        configToolbar.SizeChanged += fit;
        actions.Unloaded += (_, _) => configToolbar.SizeChanged -= fit;
        FitToolbar();
        Put(configToolbar, 0, new Border { Style = StyleFor("ToolbarSurface"), Child = tabs,
            HorizontalAlignment = HorizontalAlignment.Left });
        Put(configToolbar, 1, actions);

        void FitToolbar()
        {
            bool compact = configToolbar.ActualWidth < 700;
            if (jump.Content is StackPanel content)
                ((TextBlock)content.Children[1]).Visibility = IsVisible(!compact);
            jump.Width = compact ? 44 : double.NaN;
            jump.Padding = compact ? new Thickness(0) : new Thickness(16, 0, 16, 0);
            if (snapshot.Content is StackPanel snapshotLabel)
            {
                ((TextBlock)snapshotLabel.Children[1]).Visibility = IsVisible(!compact);
                snapshot.Width = compact ? 44 : double.NaN;
                snapshot.Padding = jump.Padding;
            }
        }
    }
}
