using CoD2Unbound.Core;
using CoD2Unbound.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ServersPage
{
    private string? revealedJumpAddress;

    internal bool ShowsServerBrowser => HostingContent.Visibility != Visibility.Visible;
    internal bool IsRefreshing => refreshing is not null;
    internal GameServer? JumpSelectedServer => ShowsServerBrowser ? (ServerList.SelectedItem as ServerRow)?.Server : null;
    internal bool FocusShellSearch() => ShowsServerBrowser && SearchBox.Focus(FocusState.Keyboard);
    internal void OpenShellFilters() => FilterButton.Flyout.ShowAt(FilterButton);
    internal Task ConnectFromShellAsync() => ShowConnectAsync();
    internal Task ToggleShellFavoriteAsync() => ServerList.SelectedItem is ServerRow row ? ToggleFavoriteAsync(row) : Task.CompletedTask;

    internal void RevealJumpServer(string address)
    {
        revealedJumpAddress = address;
        Render(address);
        if (ServerList.SelectedItem is { } item)
            ServerList.ScrollIntoView(item);
    }

    internal void CopyShellAddress()
    {
        if (JumpSelectedServer is not { } server)
            return;
        var data = new DataPackage();
        data.SetText(server.Address);
        Clipboard.SetContent(data);
    }
}
