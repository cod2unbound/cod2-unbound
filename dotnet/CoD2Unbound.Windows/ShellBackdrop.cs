using System;
using Microsoft.UI.Xaml;

namespace CoD2Unbound.Windows;

/// <summary>
/// A page whose art fills the whole launcher window, as the Mac's does (PcHome, pc.css .win): behind the
/// sidebar glass, under the caption buttons, to every edge. The shell puts <see cref="ShellBackdrop"/> into
/// its backdrop host (a layer in the window's root grid below the NavigationView) when the page becomes the
/// current page, and clears it when another page replaces it. The element must not also be in the page's own
/// tree; it does not take input. Pages without art implement nothing and get the plain window ground.
/// </summary>
internal interface IShellBackdrop
{
    UIElement? ShellBackdrop { get; }
}

/// <summary>
/// A page that takes the whole window, with no sidebar (Setup): the shell hides the pane and lays the page
/// out from the window's top-left. Only the caption buttons and the drag strip sit over its top 32 px.
/// </summary>
internal interface IFullWindowPage
{
}

public sealed partial class MainWindow
{
    /// <summary>Puts the current page's backdrop in the host again, for a page that changed its art.</summary>
    internal void RefreshBackdrop()
    {
        if (ContentFrame.Content is IFullWindowPage && Nav.IsPaneVisible)
        {
            Nav.IsPaneVisible = jumpEnabled = false;
            OnPaneChanged(Nav, EventArgs.Empty);
        }
        var art = (ContentFrame.Content as IShellBackdrop)?.ShellBackdrop;
        if (!ReferenceEquals(BackdropHost.Child, art))
            BackdropHost.Child = art;
    }
}
