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

    private FrameworkElement LoadoutShareActions(ConfigProfile profile, Button next, Button rename, Button delete,
        TextBlock readOnlyNotice, InfoBar shareNotice)
    {
        var share = new Button { AccessKey = "S" };
        var shareInMore = new AppBarButton();
        var deleteInMore = new AppBarButton { Label = "Delete loadout…" };
        AutomationProperties.SetName(deleteInMore, deleteInMore.Label);
        var more = new CommandBar { IsDynamicOverflowEnabled = false, Background = null };
        AutomationProperties.SetName(more, "Loadout actions");
        more.SecondaryCommands.Add(shareInMore);
        more.SecondaryCommands.Add(deleteInMore);
        deleteInMore.Click += async (_, _) => await shell.RunAsync(() => DeleteAsync(profile));
        var row = Buttons(next, rename, share, delete, more);
        foreach (var control in row.Children.OfType<FrameworkElement>())
            control.VerticalAlignment = VerticalAlignment.Center;
        var details = new StackPanel { Spacing = 4 };
        var panel = new StackPanel { Spacing = 8, Children = { row, details } };
        Adapt(ActualWidth > 0 && ActualWidth < 1100);
        panel.SizeChanged += (_, args) => Adapt(args.NewSize.Width < 670);
        share.Click += async (_, _) => await shell.RunAsync(ChangeSharingAsync);
        shareInMore.Click += async (_, _) => await shell.RunAsync(ChangeSharingAsync);
        refreshSharing = UpdateEnabled;
        RenderState();
        return panel;

        void Adapt(bool compact)
        {
            share.Visibility = delete.Visibility = IsVisible(!compact);
            more.Visibility = IsVisible(compact);
        }

        void RenderState()
        {
            var state = model.Share.Status(profile.Id, model.ShareCatalogue);
            bool shared = state.Phase != ConfigShareLoadoutPhase.NotShared;
            string action = shared ? "Stop sharing…" : "Share setup…";
            share.Content = IconLabel("Share", action, 16);
            shareInMore.Label = action;
            shareInMore.Icon = Fluent("Share", 16);
            UpdateEnabled();
            AutomationProperties.SetName(share, action);
            AutomationProperties.SetName(shareInMore, action);
            AutomationProperties.SetAutomationId(share, shared ? "StopSharing" : "ShareSetup");
            AutomationProperties.SetAutomationId(shareInMore, shared ? "StopSharingInMore" : "ShareSetupInMore");

            details.Children.Clear();
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

        void UpdateEnabled()
        {
            var studio = model.State;
            readOnlyNotice.Visibility = IsVisible(studio.ReadOnly);
            next.IsEnabled = !studio.ReadOnly && !profile.IsActive;
            rename.IsEnabled = !studio.ReadOnly;
            delete.IsEnabled = deleteInMore.IsEnabled = ConfigStudioModel.DeleteBlock(studio, profile.Id) is null;
            share.IsEnabled = shareInMore.IsEnabled =
                model.Share.CanStop(profile.Id) || model.Share.CanShare(profile.Id, studio.ReadOnly);
        }

        async Task ChangeSharingAsync()
        {
            if (model.Share.CanStop(profile.Id))
            {
                var confirmation = Dialog($"Stop sharing “{profile.DisplayName}”?",
                    Body("It is taken off Player Setups for everyone. Copies other players already imported stay on their PCs."),
                    "Stop sharing");
                if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
                    return;

                shareNotice.IsOpen = false;
                var pending = model.Share.StopAsync(profile);
                RenderState();
                ShowShareNotice(shareNotice, await pending);
            }
            else
                await ShowShareAsync(profile);
            RenderState();
        }
    }

    private FrameworkElement PlayerSetupsHeading(TextBlock title)
    {
        var action = new Button { Content = IconLabel("Share", "Share your setup…", 16), AccessKey = "S" };
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
        var dialog = new ShareSetupDialog(profile.DisplayName, form, (DataTemplate)Resources["ShareLeftOutLine"], () => model.Studio.IsReadOnly,
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
