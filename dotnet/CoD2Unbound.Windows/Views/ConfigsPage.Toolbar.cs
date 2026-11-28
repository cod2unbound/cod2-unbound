using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private TextBlock? studioChangedCount;
    private Button? studioNext;
    private SelectorBar? bindModes;
    private ContentPresenter? studioHeaderExtra;
    private bool LargeText => new global::Windows.UI.ViewManagement.UISettings().TextScaleFactor > 1.2;
    private bool ExtraLargeText => new global::Windows.UI.ViewManagement.UISettings().TextScaleFactor >= 2;

    private void ShowConfigStudioToolbar(SelectorBar tabs, ConfigProfileRead profile)
    {
        AutomationProperties.SetName(tabs, "Loadout section");
        tabs.VerticalAlignment = VerticalAlignment.Center;
        var actions = Buttons();
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        AddStudioSnapshotCommand(actions, profile);

        var jump = new Button
        {
            Content = IconLabel("Search", "Jump to Any Setting  Ctrl+K", 18)
        };
        jump.Click += (_, _) => shell.OpenJumpTo();
        AutomationProperties.SetName(jump, "Jump to Any Setting");
        ToolTipService.SetToolTip(jump, "Jump to any setting (Ctrl+K)");
        actions.Children.Add(jump);
        var more = new Button { Content = Fluent("More", 20) };
        AutomationProperties.SetName(more, "More Actions for This Loadout");
        ToolTipService.SetToolTip(more, "More actions for this loadout");
        more.Flyout = LoadoutMenu(profile.Profile, includeNew: true);
        actions.Children.Add(more);

        var snapshot = historySnapshotButton!;
        var snapshotTarget = historySnapshotTarget!;
        SizeChangedEventHandler fit = (_, _) => FitToolbar();
        ConfigToolbar.SizeChanged += fit;
        var visibility = snapshotTarget.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => FitToolbar());
        actions.Unloaded += (_, _) =>
        {
            ConfigToolbar.SizeChanged -= fit;
            snapshotTarget.UnregisterPropertyChangedCallback(VisibilityProperty, visibility);
        };
        FitToolbar();
        Put(ConfigToolbar, 0, tabs);
        Put(ConfigToolbar, 1, actions);

        void FitToolbar()
        {
            bool largeText = LargeText;
            bool compact = ConfigToolbar.ActualWidth < 700 || largeText;
            Grid.SetColumnSpan(tabs, largeText ? 2 : 1);
            Grid.SetRow(actions, largeText ? 1 : 0);
            if (jump.Content is StackPanel content)
                ((TextBlock)content.Children[1]).Visibility = IsVisible(!compact);
            if (snapshot.Content is StackPanel snapshotLabel)
            {
                ((TextBlock)snapshotLabel.Children[1]).Visibility = IsVisible(!compact);
            }
        }
    }
}
