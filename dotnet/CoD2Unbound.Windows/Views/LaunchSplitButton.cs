using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows.Views;

public sealed class LaunchSplitButton : SplitButton
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("SecondaryButton") is Button arrow)
        {
            AutomationProperties.SetName(arrow, "More ways to launch");
            AutomationProperties.SetAccessibilityView(arrow, AccessibilityView.Control);
        }
    }
}
