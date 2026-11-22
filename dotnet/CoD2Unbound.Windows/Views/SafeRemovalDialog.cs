using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VirtualKey = Windows.System.VirtualKey;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class SafeRemovalDialog : ContentDialog
{
    internal SafeRemovalDialog(string title, string message, string action, string actionStyle)
    {
        Title = title;
        Content = Body(message);
        PrimaryButtonText = action;
        CloseButtonText = "Cancel";
        PrimaryButtonStyle = StyleFor(actionStyle);
        DefaultButton = ContentDialogButton.None;
        IsPrimaryButtonEnabled = false;
        Opened += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusCancel);
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
        if (GetTemplateChild("CloseButton") is not Button cancel)
            return;
        cancel.Focus(FocusState.Keyboard);
        IsPrimaryButtonEnabled = true;
        cancel.Focus(FocusState.Keyboard);
    }
}
