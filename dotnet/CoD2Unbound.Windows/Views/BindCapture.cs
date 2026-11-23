using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CoD2Unbound.Windows.Views;

internal sealed class BindCapture : IDisposable
{
    private readonly UIElement root;
    private readonly Window window;
    private readonly Action cancel;
    private readonly KeyEventHandler keys;
    private readonly KeyEventHandler keyUps;
    private readonly PointerEventHandler presses;
    private readonly PointerEventHandler wheel;
    private bool disposed;

    internal BindCapture(UIElement root, Window window, Action cancel,
        Func<VirtualKey, int, bool, bool> keyboard, Func<int, bool>? pointer = null, Func<int, bool>? scroll = null)
    {
        this.root = root;
        this.window = window;
        this.cancel = cancel;
        keys = (_, args) =>
        {
            if (args.OriginalKey == VirtualKey.Escape)
            {
                args.Handled = true;
                cancel();
            }
            else if (args.KeyStatus.WasKeyDown)
                args.Handled = true;
            else
                args.Handled = keyboard(args.OriginalKey, (int)args.KeyStatus.ScanCode, args.KeyStatus.IsExtendedKey);
        };
        keyUps = (_, args) =>
        {
            // Windows commonly reports Print Screen only on release.
            if (args.OriginalKey == (VirtualKey)0x2c)
                args.Handled = keyboard(args.OriginalKey, (int)args.KeyStatus.ScanCode, args.KeyStatus.IsExtendedKey);
        };
        presses = (_, args) =>
        {
            int number = args.GetCurrentPoint(root).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed => 1, PointerUpdateKind.RightButtonPressed => 2,
                PointerUpdateKind.MiddleButtonPressed => 3, PointerUpdateKind.XButton1Pressed => 4,
                PointerUpdateKind.XButton2Pressed => 5, _ => 0
            };
            if (number != 0 && pointer is not null)
                args.Handled = pointer(number);
        };
        wheel = (_, args) =>
        {
            if (scroll is not null)
                args.Handled = scroll(args.GetCurrentPoint(root).Properties.MouseWheelDelta);
        };
        root.PreviewKeyDown += keys;
        root.PreviewKeyUp += keyUps;
        root.AddHandler(UIElement.PointerPressedEvent, presses, true);
        root.AddHandler(UIElement.PointerWheelChangedEvent, wheel, true);
        if (root is FrameworkElement element)
            element.Unloaded += Unloaded;
        window.Activated += Activated;
    }

    private void Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            cancel();
    }

    private void Unloaded(object sender, RoutedEventArgs args) => cancel();

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        root.PreviewKeyDown -= keys;
        root.PreviewKeyUp -= keyUps;
        root.RemoveHandler(UIElement.PointerPressedEvent, presses);
        root.RemoveHandler(UIElement.PointerWheelChangedEvent, wheel);
        if (root is FrameworkElement element)
            element.Unloaded -= Unloaded;
        window.Activated -= Activated;
    }
}
