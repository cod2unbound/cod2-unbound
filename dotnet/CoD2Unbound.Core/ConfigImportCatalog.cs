namespace CoD2Unbound.Core;

public sealed record ConfigImportSetting(bool Archived = false, bool CheatProtected = false, bool ReadOnly = false,
    bool WriteProtected = false, bool Inert = false);
public sealed record ConfigImportCommandInfo(bool Bindable = false);

public interface IConfigImportCatalog
{
    ConfigImportSetting? Setting(string name, ConfigDialect dialect);
    ConfigImportCommandInfo? Command(string name, ConfigDialect dialect);
}

public sealed class ConfigImportCatalogAdapter(ConfigCatalog catalog) : IConfigImportCatalog
{
    public ConfigImportSetting? Setting(string name, ConfigDialect dialect)
    {
        if (catalog.Setting(name)?.Variant(Mode(dialect)) is not { } variant)
            return null;
        int flags = variant.Sites.Aggregate(variant.Flags, (current, site) => current | site.ConservativeFlags);
        return new(Archived: (flags & 1) != 0 && variant.Sites.All(site => site.ProtectionKnown && (site.ConservativeFlags & 1) != 0),
            CheatProtected: (flags & 128) != 0, ReadOnly: (flags & 64) != 0,
            WriteProtected: (flags & 16) != 0 || variant.Sites.Any(site => !site.ProtectionKnown));
    }

    public ConfigImportCommandInfo? Command(string name, ConfigDialect dialect)
    {
        var entry = catalog.Command(name);
        return entry?.Variant(Mode(dialect)) is null ? null : new(entry.Bindable);
    }

    private static ConfigCatalogMode Mode(ConfigDialect dialect) =>
        dialect == ConfigDialect.Stock ? ConfigCatalogMode.Stock : ConfigCatalogMode.Cod2x;
}
