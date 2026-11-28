using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private MenuFlyout MakeAddConfigMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("From a File…", PickConfigAsync, shortcut: "Ctrl+O"));
        menu.Items.Add(MenuItem("From a Link or GitHub…", () => PickLinkAsync(localhost: false)));
        menu.Items.Add(MenuItem("From This PC’s Server (localhost)…", () => PickLinkAsync(localhost: true)));
        menu.Items.Add(MenuItem("From Your Call of Duty 2 Install…", () => PickInstalledProfileAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Paste Config", PasteConfigAsync, shortcut: "Ctrl+Shift+V"));
        return menu;
    }

    private const string LinkProblem =
        "Use an https:// link, a GitHub file or gist, or http://localhost for a server on this PC. Links with passwords in them aren't accepted.";

    private sealed record ImportSource(string Label, string Detail, string Icon, string? Credit = null, string? Scope = null);

    internal async Task PickConfigAsync()
    {
        if (!CanEdit)
            return;
        var picker = new FileOpenPicker(shell.AppWindow.Id) { Title = "Open config files" };
        picker.FileTypeFilter.Add(".cfg");
        foreach (var file in await picker.PickMultipleFilesAsync())
        {
            if (!CanEdit)
                break;
            await shell.ShowProblemAsync(() => ReviewFileAsync(file.Path));
        }
    }

    internal Task ReviewFileAsync(string path)
    {
        if (!WindowsInstallIdentity.IsConfigFile(path))
            throw new IOException("Choose a .cfg config file.");
        if (ConfigImportLoader.IsCredentialFile(Path.GetFileName(path)))
            throw new IOException("Choose a multiplayer config file.");
        return ReviewInputAsync(() => new ConfigImportInput.File(new Uri(path)),
            ConfigImportLoader.SuggestedName(path), new("File", Path.GetFileName(path), "Document", Scope: "Selected file and its folder"));
    }

    internal async Task PasteConfigAsync()
    {
        if (!CanEdit)
            return;
        var data = Clipboard.GetContent();
        string? text = data.Contains(StandardDataFormats.Text) ? await data.GetTextAsync() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowStatus(InfoBarSeverity.Informational, "", "The clipboard has no config text to review.");
            return;
        }

        await ReviewTextAsync(text, dropped: false);
    }

    private Task ReviewTextAsync(string text, bool dropped) => ReviewInputAsync(() =>
    {
        if (text.Length > 65_535)
            throw new ConfigImportException(ConfigImportFailure.TooLarge);
        var bytes = ConfigDocument.Encode(text);
        return dropped ? new ConfigImportInput.Drop([.. bytes]) : new ConfigImportInput.Paste([.. bytes]);
    }, dropped ? "Dropped config" : "Pasted config", new(dropped ? "Dropped text" : "Pasted text", "", "Document",
        Scope: "Text only"));

    private async Task PickLinkAsync(bool localhost)
    {
        if (!CanEdit)
            return;
        var link = new TextBox { Header = "Config link", PlaceholderText = "https://", MaxLength = 4096,
            Text = localhost ? "http://localhost:" : "" };
        var problem = Caption("");
        var content = new StackPanel { Spacing = 8, MinWidth = 340, MaxWidth = 440, Children =
        {
            Body("CoD2 Unbound reads the link as plain text and shows you every line before anything changes."),
            link,
            Caption("✓  An https:// link to plain config text"),
            Caption("✓  A GitHub file or gist"),
            Caption("✓  A server on this PC, such as http://localhost:8000/config.cfg"),
            problem
        } };
        var dialog = Dialog("Add a config from a link", content, "Review");
        link.TextChanged += (_, _) => Validate();
        TrackGameState(dialog, Validate);
        dialog.Opened += (_, _) =>
        {
            link.Focus(FocusState.Programmatic);
            link.SelectionStart = link.Text.Length;
        };
        Validate();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (ConfigImportLoader.AcceptsLink(link.Text))
                return;
            args.Cancel = true;
            problem.Text = LinkProblem;
            problem.Style = StyleFor("NegativeText");
            link.Focus(FocusState.Programmatic);
        };
        if (await dialog.ShowSheetAsync() != ContentDialogResult.Primary)
            return;

        var url = new Uri(link.Text.Trim());
        await ReviewInputAsync(() => new ConfigImportInput.Remote(url),
            ConfigImportLoader.SuggestedName(Uri.UnescapeDataString(url.Segments.LastOrDefault() ?? "").TrimEnd('/')),
            new("Link", url.GetLeftPart(UriPartial.Path), "ArrowDownload", Scope: "Linked file and its source folder"));
        void Validate()
        {
            bool empty = string.IsNullOrWhiteSpace(link.Text);
            problem.Text = empty ? "Paste or type a link to review it." : "";
            problem.Style = StyleFor("SecondaryText");
            dialog.IsPrimaryButtonEnabled = CanEdit && !empty;
        }
    }

    private void AttachImportDrop()
    {
        ConfigSurface.AddHandler(DragOverEvent, new DragEventHandler(OnImportDragOver), handledEventsToo: true);
        ConfigSurface.AddHandler(DragLeaveEvent, new DragEventHandler(OnImportDragLeave), handledEventsToo: true);
        ConfigSurface.AddHandler(DropEvent, new DragEventHandler(OnImportDrop), handledEventsToo: true);
    }

    private void OnImportDragOver(object sender, DragEventArgs args)
    {
        if (CheckingConfig || !CanEdit || !(args.DataView.Contains(StandardDataFormats.StorageItems) || args.DataView.Contains(StandardDataFormats.Text)))
        {
            ImportDropCue.Visibility = Visibility.Collapsed;
            return;
        }
        args.AcceptedOperation = DataPackageOperation.Copy;
        args.DragUIOverride.Caption = "Review";
        ImportDropCue.Visibility = Visibility.Visible;
        args.Handled = true;
    }

    private void OnImportDragLeave(object sender, DragEventArgs args) => ImportDropCue.Visibility = Visibility.Collapsed;

    private async void OnImportDrop(object sender, DragEventArgs args)
    {
        ImportDropCue.Visibility = Visibility.Collapsed;
        if (CheckingConfig || !CanEdit)
            return;
        args.Handled = true;
        var deferral = args.GetDeferral();
        Func<Task>? review = null;
        try
        {
            if (args.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var files = await args.DataView.GetStorageItemsAsync();
                review = files.Count == 1 ? files[0] switch
                {
                    StorageFolder folder => () => PickInstalledProfileAsync(folder.Path),
                    StorageFile file => () => ReviewFileAsync(file.Path),
                    _ => null
                } : null;
            }
            else if (args.DataView.Contains(StandardDataFormats.Text))
            {
                string text = await args.DataView.GetTextAsync();
                review = () => ReviewTextAsync(text, dropped: true);
            }
        }
        catch (Exception error) when (error is IOException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, "Couldn't read it", error.Message);
            return;
        }
        finally
        {
            args.AcceptedOperation = review is null ? DataPackageOperation.None : DataPackageOperation.Copy;
            deferral.Complete();
        }

        if (review is not null)
            await shell.RunAsync(review);
        else
            ShowStatus(InfoBarSeverity.Informational, "", "Drop a single .cfg file, profile folder or config text.");
    }

    private Task ReviewInputAsync(Func<ConfigImportInput> source, string name, ImportSource provenance) =>
        ShowImportReviewAsync(name, provenance, async token =>
        {
            var catalog = new ConfigImportCatalogAdapter(model.Catalog);
            var dialect = model.Dialect;
            var loader = new ConfigImportLoader(catalog, new WindowsConfigImportFiles(), new HttpConfigTextFetcher());
            var bundle = await loader.LoadAsync(source(), dialect, token).ConfigureAwait(false);
            return new(bundle, dialect, catalog, name, ConfigImportPlatform.Windows, ConfigImportPlatform.Windows);
        });

    private Task UndoImportAsync()
    {
        if (lastImport is not { } previous || !CanEdit)
            return Task.CompletedTask;
        return UndoAsync(previous);

        async Task UndoAsync((ConfigImportReceipt Receipt, bool Created) item)
        {
            try
            {
                model.UndoImport(item.Receipt, item.Created);
                lastImport = null;
                await RefreshStudioAsync();
                Feedback("Import undone.");
                Announce("Import undone.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShowStatus(InfoBarSeverity.Error, "Couldn't undo the import", error.Message);
            }
        }
    }
}
