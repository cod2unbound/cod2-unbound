using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VirtualKey = Windows.System.VirtualKey;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class SafeRemovalDialog : ContentDialog
{
    internal SafeRemovalDialog(string title, string message, string action)
    {
        Title = title;
        var body = Body(message);
        body.MinWidth = 352;
        Content = body;
        PrimaryButtonText = action;
        CloseButtonText = "Cancel";
        PrimaryButtonStyle = StyleFor("NegativeButton");
        DefaultButton = ContentDialogButton.None;
        IsPrimaryButtonEnabled = false;
        Opened += (_, _) =>
        {
            ApplyTemplate();
            FocusCancel();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusCancel);
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter && GetTemplateChild("PrimaryButton") is Button { FocusState: not FocusState.Unfocused })
            {
                args.Handled = true;
                FocusCancel();
            }
        };
    }

    private void FocusCancel()
    {
        var cancel = GetTemplateChild("CloseButton") as Button;
        cancel?.Focus(FocusState.Keyboard);
        IsPrimaryButtonEnabled = true;
        cancel?.Focus(FocusState.Keyboard);
    }
}
