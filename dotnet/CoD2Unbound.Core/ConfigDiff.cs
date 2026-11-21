using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public sealed record ConfigChange(string Id, string Label, string? ConsoleName, string Before, string After);

public static class ConfigDiff
{
    public static ImmutableArray<ConfigChange> Changes(ConfigProfileVersion before, ConfigProfileVersion after,
        ConfigDialect dialect, ConfigCatalog catalog)
    {
        ConfigCatalogMode mode = dialect == ConfigDialect.Stock ? ConfigCatalogMode.Stock : ConfigCatalogMode.Cod2x;
        var old = new ConfigDocument(before.Config.AsSpan(), dialect);
        var changed = new ConfigDocument(after.Config.AsSpan(), dialect);
        var commands = old.Commands.Concat(changed.Commands).ToArray();
        var names = commands.Where(item => item.Kind == ConfigCommandKind.Setting && item.Tokens.Length >= 2)
            .Select(item => item.Tokens[1].Text).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name.ToLowerInvariant(), StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<ConfigChange>();
        int other = 0;
        foreach (string name in names)
        {
            string? a = old.EffectiveSetting(name), b = changed.EffectiveSetting(name);
            if (a == b)
                continue;
            if (catalog.Setting(name) is not { } setting)
            {
                other++;
                continue;
            }
            if (ConfigSettingSpec.Create(setting, mode) is { } spec)
            {
                if (a is not null && b is not null && ConfigValues.Same(spec, a, b))
                    continue;
                result.Add(new(name.ToLowerInvariant(), spec.Title, spec.Name, ConfigValues.Display(spec, a ?? spec.DefaultText),
                    ConfigValues.Display(spec, b ?? spec.DefaultText)));
            }
            else
            {
                bool credentials = name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("cdkey", StringComparison.OrdinalIgnoreCase) || name.Equals("codkey", StringComparison.OrdinalIgnoreCase);
                result.Add(new(name.ToLowerInvariant(), setting.Title, setting.Name, Display(a), Display(b)));
                string Display(string? value) => value is null ? "Not set" : credentials ? "Hidden" : value;
            }
        }
        int changedBinds = commands.Where(item => item.Kind is ConfigCommandKind.Bind or ConfigCommandKind.Unbind && item.Tokens.Length >= 2)
            .Select(item => ConfigDocument.KeyNumber(item.Tokens[1].Text)).OfType<int>().Distinct()
            .Select(ConfigDocument.KeySpelling).Count(key => old.EffectiveBind(key) != changed.EffectiveBind(key));
        if (changedBinds > 0)
            result.Add(new("binds", "Key bindings", null, "", changedBinds == 1 ? "1 key changed" : $"{changedBinds} keys changed"));
        if (other > 0)
            result.Add(new("other", "Other lines", null, "", other == 1 ? "1 line changed" : $"{other} lines changed"));
        if (before.Chain.HasValue != after.Chain.HasValue || (before.Chain is { } original && after.Chain is { } update &&
            !original.AsSpan().SequenceEqual(update.AsSpan())))
        {
            string state = before.Chain is null ? "Added" : after.Chain is null ? "Removed" : "Changed";
            result.Add(new("chain", "Advanced binds", null, "", state));
        }
        if (Metadata(before.Metadata) is { } aMeta && Metadata(after.Metadata) is { } bMeta)
        {
            if (aMeta.DisplayName != bMeta.DisplayName)
                result.Add(new("name", "Loadout name", null, aMeta.DisplayName, bMeta.DisplayName));
            if (!aMeta.Locks.SetEquals(bMeta.Locks))
                result.Add(new("locks", "Locked settings", null, List(aMeta.Locks), List(bMeta.Locks)));
            if (aMeta.Notes.Count != bMeta.Notes.Count || aMeta.Notes.Any(pair => bMeta.Notes.GetValueOrDefault(pair.Key) != pair.Value))
            {
                string label = aMeta.Notes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(bMeta.Notes.Keys) ? "Edited notes" : "Notes";
                result.Add(new("notes", label, null, List(aMeta.Notes.Keys), List(bMeta.Notes.Keys)));
            }
            if (aMeta.Dpi != bMeta.Dpi)
                result.Add(new("dpi", "Mouse DPI", null, Dpi(aMeta.Dpi), Dpi(bMeta.Dpi)));
        }
        return result.ToImmutable();

        static ConfigStudioMetadata? Metadata(ImmutableArray<byte>? bytes)
        {
            try { return bytes is null ? null : ConfigProfileFormat.Metadata(bytes, "snapshot"); }
            catch (ConfigProfileException) { return null; }
        }

        string List(IEnumerable<string> keys)
        {
            string value = string.Join(", ", keys.Select(key => catalog.Setting(key)?.Title ?? key).Order(StringComparer.Ordinal));
            return value.Length == 0 ? "None" : value;
        }

        static string Dpi(double? value) => value is { } number ? $"{ConfigValues.Format(number)} DPI" : "Not set";
    }

    public static string Sentence(ImmutableArray<ConfigChange> changes)
    {
        if (changes.IsEmpty)
            return "No settings changed.";
        string text = string.Join(". ", changes.Take(3).Select(item => item.Before.Length == 0 ? $"{item.Label}: {item.After}"
            : $"{item.Label} {item.Before} → {item.After}"));
        return text + "." + (changes.Length > 3 ? $" And {changes.Length - 3} more." : "");
    }
}
