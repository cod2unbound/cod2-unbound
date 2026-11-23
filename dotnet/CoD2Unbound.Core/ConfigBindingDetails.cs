using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public static class ConfigBindingDetails
{
    private static readonly IReadOnlyDictionary<string, string> WindowsLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["+forward"] = "Forward",
        ["+talk"] = "Voice chat"
    };

    public static ConfigBindings ForWindows(ConfigDocument document, ConfigCatalog catalog, ConfigCatalogMode mode) =>
        new(document, catalog, mode, WindowsLabels);

    public static ImmutableArray<ConfigBindingAction> Actions(ConfigBindings bindings, ConfigBindingEntry entry, ConfigDialect dialect)
    {
        var lookup = bindings.Actions.ToDictionary(action => action.Name, StringComparer.OrdinalIgnoreCase);
        return [.. new ConfigDocument(entry.Bytes.AsSpan(), dialect).Commands.Where(command => command.Tokens.Length == 1)
            .Select(command => lookup.GetValueOrDefault(command.Tokens[0].Text)).OfType<ConfigBindingAction>().Distinct()];
    }

    public static string Title(ConfigBindings bindings, ConfigBindingEntry entry, ConfigDialect dialect)
    {
        var actions = Actions(bindings, entry, dialect);
        return actions.IsEmpty ? entry.Text : string.Join(" and ", actions.Select(action => action.Title));
    }

    public static ImmutableArray<ConfigBindingEntry> Shared(ConfigBindings bindings, ConfigDialect dialect) =>
        [.. bindings.Entries.Where(entry => Actions(bindings, entry, dialect).Length >= 2)];

    public static ImmutableArray<byte> Value(ConfigBindingAction action) => [.. ConfigDocument.Encode(action.Name)];
}
