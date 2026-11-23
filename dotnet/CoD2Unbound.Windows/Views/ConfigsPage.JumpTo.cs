using System.Collections.Immutable;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private static readonly Lazy<ConfigCatalog> JumpCatalog = new(ConfigCatalog.LoadBundled);
    private static readonly Lazy<ImmutableArray<JumpTarget>> StockBindings = new(() => BuildJumpBindings(ConfigDialect.Stock, ConfigCatalogMode.Stock));
    private static readonly Lazy<ImmutableArray<JumpTarget>> Cod2xBindings = new(() => BuildJumpBindings(ConfigDialect.Cod2x, ConfigCatalogMode.Cod2x));
    private Control? studioFind;
    private Control? catalogueFind;
    private string? jumpBinding;
    private string? jumpSetting;
    private FrameworkElement? jumpBindingRow;

    internal bool ShowsLoadout => DiscoverPlaces.SelectedItem is null && model.State.Current is not null;
    internal bool HasJumpLoadouts => !model.State.Profiles.IsEmpty;
    internal bool CanImportFromShell => !model.State.ReadOnly && model.State.Draft is null;
    internal bool CanCreateFromShell => NewLoadout.IsEnabled;

    internal bool FocusShellSearch()
    {
        var field = DiscoverPlaces.SelectedIndex is 0 or 1 ? catalogueFind :
            ShowsLoadout && studioTab == 0 && expert ? studioFind : null;
        return field is { IsLoaded: true } && field.Focus(FocusState.Keyboard);
    }

    internal IEnumerable<JumpTarget> JumpSettings() =>
        model.Model.Specs.Where(spec => !IsHiddenFromExpert(spec)).OrderByDescending(spec => spec.Setting.Guided)
            .Select(spec => new JumpTarget(JumpGroup.ConfigSettings, spec.Name, spec.Title,
                $"{spec.Name} · {model.Catalog.Groups.First(group => group.Id == spec.Setting.Group).Title}", spec.Name));

    private static bool IsHiddenFromExpert(ConfigSettingSpec spec) => spec.Name.EndsWith("fps", StringComparison.OrdinalIgnoreCase);

    internal static ImmutableArray<JumpTarget> JumpBindings(GameEdition edition) =>
        (edition == GameEdition.Cod2x ? Cod2xBindings : StockBindings).Value;

    private static ImmutableArray<JumpTarget> BuildJumpBindings(ConfigDialect dialect, ConfigCatalogMode mode)
    {
        var bindings = new ConfigBindings(new ConfigDocument([], dialect), JumpCatalog.Value, mode);
        return [.. bindings.Actions.Select(action => new JumpTarget(JumpGroup.Bindings, action.Name, action.Title,
            "Configs › Binds", action.Name))];
    }

    internal void OpenJumpLoadout(string id)
    {
        model.Studio.Focus(id);
        studioTab = 0;
        expert = false;
        query = "";
        ShowStudio();
        RenderLoadouts();
        ResetDetailScroll();
    }

    internal void OpenJumpSetting(string name)
    {
        if (model.Model.Specs.FirstOrDefault(spec => spec.Name == name && !IsHiddenFromExpert(spec)) is not { } setting)
            return;
        studioTab = 0;
        expert = true;
        category = setting.Setting.Group;
        query = jumpSetting = setting.Name;
        ShowStudio();
        ResetDetailScroll();
    }

    internal void OpenJumpHistory()
    {
        studioTab = 3;
        ShowStudio();
        RenderLoadouts();
        ResetDetailScroll();
    }

    internal void OpenJumpBinding(string name)
    {
        studioTab = 1;
        jumpBinding = name;
        try
        {
            ShowStudio();
            RenderLoadouts();
            if (jumpBindingRow is { } row)
            {
                void Reveal() => row.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
                if (row.IsLoaded)
                    Reveal();
                else
                    row.Loaded += (_, _) => Reveal();
            }
        }
        finally
        {
            jumpBinding = null;
            jumpBindingRow = null;
        }
    }

    internal async Task ImportFromShellAsync()
    {
        if (!CanImportFromShell)
            return;
        var picker = new FileOpenPicker(shell.AppWindow.Id) { Title = "Open a config file" };
        picker.FileTypeFilter.Add(".cfg");
        if (await picker.PickSingleFileAsync() is { } file)
            await ReviewFileAsync(file.Path);
    }

    internal async Task PasteFromShellAsync()
    {
        if (!CanImportFromShell)
            return;
        var clipboard = Clipboard.GetContent();
        if (clipboard.Contains(StandardDataFormats.Text) && await clipboard.GetTextAsync() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            await ReviewInputAsync(new ConfigImportInput.Paste([.. ConfigDocument.Encode(text.ReplaceLineEndings("\n"))]),
                "Imported config");
        }
        else
            throw new IOException("Copy config text to the clipboard first.");
    }
}
