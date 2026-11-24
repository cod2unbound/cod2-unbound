using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows.Views;

internal sealed class SafeRemovalDialog : ContentDialog
{
    internal SafeRemovalDialog(string title, string message, string action)
    {
        Title = title;
        Content = PagePresentation.Body(message);
        PrimaryButtonText = action;
        CloseButtonText = "Cancel";
        this.AsAlert(destructive: true);
        DefaultButton = ContentDialogButton.Close;
    }
}
