using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ServersPage
{
    private string? revealedJumpAddress;

    internal bool ShowsServerBrowser => HostingContent.Visibility != Visibility.Visible;
    internal bool IsRefreshing => refreshing is not null;
    private ServerRow? ShortcutRow => ShowsServerBrowser ? FocusedRow() ?? ServerList.SelectedItem as ServerRow : null;
    internal bool CanRunRowShortcut => ShortcutRow is not null;
    internal bool FocusShellSearch() => ShowsServerBrowser && SearchBox.Focus(FocusState.Keyboard);
    internal void OpenShellFilters() => FilterButton.Flyout.ShowAt(FilterButton);
    internal Task ConnectFromShellAsync() => ShowConnectAsync();
    internal Task ToggleShellFavoriteAsync() => ShortcutRow is { } row ? ToggleFavoriteAsync(row) : Task.CompletedTask;
    internal Task CopyShellAddressAsync() => ShortcutRow is { } row ? CopyRowAddressAsync(row) : Task.CompletedTask;

    internal void RevealJumpServer(string address)
    {
        revealedJumpAddress = address;
        Render(address);
        if (ServerList.SelectedItem is { } item)
            ServerList.ScrollIntoView(item);
    }

}
