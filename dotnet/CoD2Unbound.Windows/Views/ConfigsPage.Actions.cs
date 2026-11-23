using System.ComponentModel;
using System.Diagnostics;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private (ConfigImportReceipt Receipt, bool Created)? lastImport;

    private bool CanEdit => !model.Studio.IsReadOnly && model.State.Draft is null;

    private async void OnNewLoadout(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        var source = model.State.Current;
        var name = new TextBox { Header = "Loadout name", MaxLength = 80, PlaceholderText = "Name, for example Scrim" };
        var start = new ComboBox { Header = "Start from", SelectedIndex = -1, HorizontalAlignment = HorizontalAlignment.Stretch };
        start.Items.Add("The game's defaults");
        if (source is not null)
            start.Items.Add($"A copy of {source.Profile.DisplayName}");
        start.SelectedIndex = 0;
        var problem = Caption("");
        var content = new StackPanel { MinWidth = 300, Spacing = 8, Children = { name, problem, start } };
        var dialog = Dialog("New loadout", content, "Create loadout");
        name.TextChanged += (_, _) => Validate();
        TrackGameState(dialog, Validate);
        dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);
        Validate();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        model.Create(name.Text.Trim(), start.SelectedIndex == 1 ? source : null);
        studioTab = 0;
        await RefreshStudioAsync();
        Announce($"Made {name.Text.Trim()}.");
        void Validate()
        {
            problem.Text = model.NameProblem(name.Text) ?? "";
            dialog.IsPrimaryButtonEnabled = CanEdit && problem.Text.Length == 0;
        }
    });

    private async Task RenameAsync(ConfigProfile profile)
    {
        var current = model.Studio.ReadProfile(profile.Id);
        if (await AskNameAsync("Rename loadout", "Rename", current.Profile.DisplayName, current.Profile.Id) is not { } name)
            return;
        model.Studio.Focus(profile.Id);
        model.Rename(current, name);
        await RefreshStudioAsync();
    }

    private async Task UseNextAsync(ConfigProfile profile)
    {
        model.Studio.Select(profile.Id);
        model.Studio.Focus(profile.Id);
        await RefreshStudioAsync();
    }

    private async Task DuplicateAsync(ConfigProfile profile)
    {
        model.Duplicate(profile.Id);
        studioTab = 0;
        await RefreshStudioAsync();
        if (model.State.Current is { } copy)
            Announce($"Made {copy.Profile.DisplayName}.");
    }

    private async Task ExportAsync(ConfigProfile profile)
    {
        ExportStatus.IsOpen = false;
        try
        {
            var bytes = ConfigChains.WindowsExport(model.Studio.ReadProfile(profile.Id));
            var picker = new FileSavePicker(shell.AppWindow.Id)
            {
                SuggestedFileName = "config_mp.cfg", DefaultFileExtension = ".cfg"
            };
            picker.FileTypeChoices.Add("Call of Duty 2 config", [".cfg"]);
            if (await picker.PickSaveFileAsync() is not { } file)
                return;
            await model.WriteExportAsync(file.Path, bytes);
            ExportStatus.Title = "";
            ExportStatus.Severity = InfoBarSeverity.Success;
            ExportStatus.Message = $"Exported {Path.GetFileName(file.Path)}, with its binds.";
            ExportStatus.ActionButton = Action("Show in File Explorer", () => RevealAsync(file.Path));
            ExportStatus.IsClosable = true;
            ExportStatus.IsOpen = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ConfigEditException or
            ConfigChainException or System.Runtime.InteropServices.COMException)
        {
            ExportStatus.Severity = InfoBarSeverity.Error;
            ExportStatus.Title = $"Couldn't export {profile.DisplayName}";
            ExportStatus.Message = error.Message;
            ExportStatus.ActionButton = null;
            ExportStatus.IsClosable = false;
            ExportStatus.IsOpen = true;
        }
    }

    private Task RevealLoadoutAsync(ConfigProfile profile) => RevealAsync(model.ConfigPath(profile.Id));

    private static Task RevealAsync(string path)
    {
        using (WindowsFiles.Open(Path.GetDirectoryName(path)!, Path.GetFileName(path))) { }
        try { using var process = Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Win32Exception error) { throw new IOException("File Explorer couldn't show this config.", error); }
        return Task.CompletedTask;
    }

    private void AttachLoadoutMenu(Grid row)
    {
        if (ContainingListItem(row) is not { } container)
            return;
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (container.Content is not LoadoutRow item)
                return;
            var profile = item.Profile;
            bool shared = model.Share.CanStop(profile.Id);
            menu.Items.Add(MenuItem("Use for next game", () => UseNextAsync(profile), CanEdit && !profile.IsActive));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("Duplicate", () => DuplicateAsync(profile), CanEdit, "Ctrl+Shift+D", "Copy"));
            menu.Items.Add(MenuItem("Rename…", () => RenameAsync(profile), CanEdit, "F2"));
            menu.Items.Add(MenuItem("Export…", () => ExportAsync(profile), shortcut: "Ctrl+Shift+E"));
            menu.Items.Add(MenuItem(shared ? "Stop sharing…" : "Share setup…", () => ChangeSharingAsync(profile),
                shared || model.Share.CanShare(profile.Id, !CanEdit), glyph: "Share"));
            menu.Items.Add(MenuItem("Show in File Explorer", () => RevealLoadoutAsync(profile)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("Delete loadout…", () => DeleteAsync(profile),
                CanEdit && ConfigStudioModel.DeleteBlock(model.State, profile.Id) is null, "Delete"));
        };
        container.ContextFlyout = menu;
    }

    private MenuFlyoutItem MenuItem(string label, Func<Task> action, bool enabled = true, string? shortcut = null, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled };
        if (shortcut is not null)
            item.KeyboardAcceleratorTextOverride = shortcut;
        if (glyph is not null)
            item.Icon = Fluent(glyph, 16);
        AutomationProperties.SetName(item, label);
        item.Click += async (_, _) => await shell.RunAsync(action);
        return item;
    }

    private AppBarButton Command(string label, Func<Task> run, string? glyph = null)
    {
        var button = new AppBarButton { Label = label };
        if (glyph is not null)
            button.Icon = Fluent(glyph, 16);
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await shell.RunAsync(run);
        return button;
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> call,
        Func<bool> enabled, UIElement? scope = null)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers, ScopeOwner = scope };
        accelerator.Invoked += async (_, args) =>
        {
            if (!enabled())
                return;
            args.Handled = true;
            await shell.RunAsync(call);
        };
        (scope ?? this).KeyboardAccelerators.Add(accelerator);
    }

    private ConfigProfile? SelectedProfile => (Loadouts.SelectedItem as LoadoutRow)?.Profile;

    private void AttachConfigShortcuts()
    {
        var ctrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
        AddShortcut(VirtualKey.D, ctrlShift, () => DuplicateAsync(SelectedProfile!), () => CanEdit && SelectedProfile is not null);
        AddShortcut(VirtualKey.E, ctrlShift, () => ExportAsync(SelectedProfile!), () => SelectedProfile is not null);
        AddShortcut(VirtualKey.F2, VirtualKeyModifiers.None, () => RenameAsync(SelectedProfile!),
            () => CanEdit && SelectedProfile is not null, Loadouts);
        AddShortcut(VirtualKey.Delete, VirtualKeyModifiers.None, () => DeleteAsync(SelectedProfile!),
            () => CanEdit && ConfigStudioModel.DeleteBlock(model.State, SelectedProfile?.Id) is null, Loadouts);
        AddShortcut(VirtualKey.O, VirtualKeyModifiers.Control, PickConfigAsync, () => CanEdit);
        AddShortcut(VirtualKey.V, ctrlShift, PasteConfigAsync, () => CanEdit);
        AddShortcut(VirtualKey.Z, VirtualKeyModifiers.Control, UndoImportAsync, () => CanEdit && lastImport is not null);
    }

    private void Announce(string message) => FrameworkElementAutomationPeer.CreatePeerForElement(Status)?.RaiseNotificationEvent(
        AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, message, "Configs");

    private void ShowStatus(InfoBarSeverity severity, string title, string message = "", Button? action = null, bool closable = true)
    {
        Status.Severity = severity;
        Status.Title = title;
        Status.Message = message;
        Status.ActionButton = action;
        Status.IsClosable = closable;
        Status.IsOpen = true;
    }
}
