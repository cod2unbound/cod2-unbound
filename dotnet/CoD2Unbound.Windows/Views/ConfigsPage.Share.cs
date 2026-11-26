using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private Action? refreshSharing;

    internal void UpdateSharingAvailability() => refreshSharing?.Invoke();

    private FrameworkElement LoadoutShareStatus(ConfigProfile profile, InfoBar readOnlyNotice)
    {
        var details = new StackPanel { Spacing = 4 };
        refreshSharing = () =>
        {
            readOnlyNotice.IsOpen = model.State.ReadOnly;
            UpdateStudioHeader();
            RenderState();
        };
        refreshSharing();
        return details;

        void RenderState()
        {
            var state = model.Share.Status(profile.Id, model.ShareCatalogue);
            bool shared = state.Phase != ConfigShareLoadoutPhase.NotShared;
            details.Children.Clear();
            details.Visibility = IsVisible(shared || model.Share.RecordsProblem is not null);
            if (!shared)
            {
                if (model.Share.RecordsProblem is { } problem)
                {
                    var error = new InfoBar { IsClosable = false };
                    ShowShareNotice(error, problem);
                    details.Children.Add(error);
                }
                return;
            }

            string label = state.Phase switch
            {
                ConfigShareLoadoutPhase.Listed => "Shared · in Player Setups",
                ConfigShareLoadoutPhase.Stopping => "Stopping sharing…",
                _ => "Shared · waiting for review"
            };
            var tag = Caption(label);
            if (state.Listed is not null)
                tag.Style = StyleFor("PositiveCaption");
            AutomationProperties.SetAutomationId(tag, "SharedSetupStatus");
            var badge = new Border
            {
                Style = StyleFor("FieldCard"), Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left, Child = tag
            };
            var summary = Columns(8, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
            Put(summary, 0, badge);
            badge.VerticalAlignment = VerticalAlignment.Center;
            details.Children.Add(summary);
            if (state.Detail is { } description)
            {
                var text = Caption(description);
                text.VerticalAlignment = VerticalAlignment.Center;
                Put(summary, 1, text);
            }
            if (state.RequestUrl is { } url)
                Put(summary, 2, ShareSetupDialog.RequestLink(url));
            else if (state.Listed is { } listing)
            {
                var open = new HyperlinkButton { Content = "Open in Player Setups", Padding = new Thickness(0, 4, 0, 4) };
                AutomationProperties.SetName(open, "Open in Player Setups");
                open.Click += async (_, _) => await shell.RunAsync(() => OpenListedSetupAsync(listing.Id));
                Put(summary, 2, open);
            }
        }

    }

    private async Task ChangeSharingAsync(ConfigProfile profile, InfoBar? noticeBar = null, Action? onPending = null)
    {
        if (!model.Share.CanStop(profile.Id))
        {
            if (CanEdit)
                await ShowShareAsync(profile);
            if (noticeBar is null && model.State.Current?.Profile.Id == profile.Id)
                ShowStudio();
            return;
        }

        var confirmation = Dialog($"Stop sharing “{profile.DisplayName}”?",
            Body("It is taken off Player Setups for everyone. Copies other players already imported stay on their PCs."),
            "Stop Sharing");
        confirmation.AsAlert(destructive: false);
        confirmation.DefaultButton = ContentDialogButton.Primary;
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (noticeBar is not null)
            noticeBar.IsOpen = false;
        var pending = model.Share.StopAsync(profile);
        onPending?.Invoke();
        var notice = await pending;
        if (noticeBar is not null)
            ShowShareNotice(noticeBar, notice);
        else
        {
            if (model.State.Current?.Profile.Id == profile.Id)
                ShowStudio();
            Status.ActionButton = null;
            Status.IsClosable = true;
            ShowShareNotice(Status, notice);
        }
    }

    private Grid PlayerSetupsHeading(FrameworkElement title)
    {
        var action = new Button { Content = IconLabel("Share", "Share Your Setup…", 16), AccessKey = "S" };
        AutomationProperties.SetName(action, "Share your setup");
        AutomationProperties.SetAutomationId(action, "ShareYourSetup");
        var profiles = model.State.Profiles;
        refreshSharing = UpdateEnabled;
        UpdateEnabled();
        if (profiles.Length == 1)
            action.Click += async (_, _) => await PickAsync(profiles[0]);
        else if (profiles.Length > 1)
        {
            var choices = new MenuFlyout();
            action.Flyout = choices;
            choices.Opening += (_, _) =>
            {
                choices.Items.Clear();
                bool readOnly = model.Studio.IsReadOnly;
                foreach (var profile in profiles)
                {
                    var item = new MenuFlyoutItem
                    {
                        Text = profile.DisplayName, IsEnabled = model.Share.CanShare(profile.Id, readOnly)
                    };
                    AutomationProperties.SetName(item, profile.DisplayName);
                    if (model.Share.CanStop(profile.Id))
                        AutomationProperties.SetHelpText(item, "Already shared. Choose Stop sharing on this loadout to share it again.");
                    item.Click += async (_, _) => await PickAsync(profile);
                    choices.Items.Add(item);
                }
            };
        }

        var header = Columns(12, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(header, 0, title);
        Put(header, 1, action);
        MoveBelowWhenNarrow(header, action, 400);
        return header;

        void UpdateEnabled()
        {
            bool readOnly = model.Studio.IsReadOnly;
            action.IsEnabled = profiles.Any(profile => model.Share.CanShare(profile.Id, readOnly));
        }

        async Task PickAsync(ConfigProfile profile)
        {
            await shell.RunAsync(() => ShowShareAsync(profile));
            UpdateEnabled();
        }
    }

    private async Task ShowShareAsync(ConfigProfile profile)
    {
        if (!model.Share.CanShare(profile.Id, model.Studio.IsReadOnly))
            return;
        var form = await model.PrepareShareAsync(profile);
        var dialog = new ShareSetupDialog(profile.DisplayName, model.AppVersion, form, (DataTemplate)Resources["ShareLeftOutLine"], () => model.Studio.IsReadOnly,
            token => model.Share.PublishAsync(profile.Id, form, model.Dialect, model.AppVersion, model.Studio.IsReadOnly, token))
        {
            XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme
        };
        await dialog.RunAsync();
    }

    private async Task OpenListedSetupAsync(string id)
    {
        syncing = true;
        DiscoverPlaces.SelectedIndex = 0;
        syncing = false;
        Loadouts.SelectedItem = null;
        await model.Discover.LoadAsync();
        model.RememberVerifiedCatalogue();
        if (model.Discover.Entries.FirstOrDefault(entry => entry.Id == id) is { } entry)
            await ShowSetupAsync(entry, fromSaved: false);
        else
            await ShowCatalogueAsync(saved: false);
    }

    internal static void ShowShareNotice(InfoBar bar, ConfigShareNotice? notice)
    {
        bar.IsOpen = false;
        if (notice is null)
            return;
        bar.Title = notice.Title;
        bar.Message = notice.Message;
        bar.Severity = notice.Level switch
        {
            ConfigShareNoticeLevel.Error => InfoBarSeverity.Error,
            ConfigShareNoticeLevel.Warning => InfoBarSeverity.Warning,
            ConfigShareNoticeLevel.Success => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational
        };
        bar.IsOpen = true;
    }
}
