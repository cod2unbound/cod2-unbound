using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    internal async Task OpenSetupLinkAsync(string id)
    {
        syncing = true;
        DiscoverPlaces.SelectedIndex = 0;
        Loadouts.SelectedItem = null;
        syncing = false;
        discoverQuery = "";
        discoverGroup = null;
        catalogueDpi = model.DpiFor(model.Studio.ReadActive());
        await model.Discover.LoadAsync();
        if (model.Discover.Entries.FirstOrDefault(entry => entry.Id == id) is { } match)
        {
            await ShowSetupAsync(match, fromSaved: false);
            return;
        }

        await ShowCatalogueAsync(saved: false);
        if (model.Discover.State.Snapshot is not null)
            ShowStatus(InfoBarSeverity.Informational, "", "That setup isn’t in Player Setups. It may have been renamed or taken down.");
    }
}
