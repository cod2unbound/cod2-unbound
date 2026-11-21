using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VirtualKey = Windows.System.VirtualKey;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class DeleteLoadoutDialog : ContentDialog
{
    internal DeleteLoadoutDialog(string name)
    {
        Title = $"Delete the loadout “{name}”?";
        Content = Caption("Its settings, binds and history go with it. Your other loadouts aren’t affected.");
        PrimaryButtonText = "Delete loadout";
        CloseButtonText = "Cancel";
        PrimaryButtonStyle = StyleFor("NegativeButton");
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
