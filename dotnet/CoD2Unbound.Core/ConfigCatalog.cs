using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Resources;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public enum ConfigCatalogMode { Stock, Cod2x }

public sealed record ConfigCatalogGroup(string Id, string Title);
public sealed record ConfigPracticalRange(double Min, double Max);
public sealed record ConfigCatalogControl(string Kind, double? Step = null, string? Unit = null,
    ImmutableDictionary<string, string>? Labels = null, ConfigPracticalRange? PracticalRange = null);

public sealed record ConfigCatalogFact(string Status, JsonElement? Value = null, string? Expression = null)
{
    private static readonly ConfigCatalogFact Ambiguous = new("ambiguous", null, null);

    internal static ConfigCatalogFact? Consensus(IEnumerable<ConfigCatalogFact?> facts)
    {
        using var list = facts.GetEnumerator();
        if (!list.MoveNext())
            return Ambiguous;

        var first = list.Current;
        while (list.MoveNext())
        {
            var next = list.Current;
            if (first?.Status != next?.Status || first?.Expression != next?.Expression ||
                !SameValue(first?.Value, next?.Value))
                return Ambiguous;
        }
        return first;
    }

    private static bool SameValue(JsonElement? left, JsonElement? right) =>
        left is { } a && right is { } b ? JsonElement.DeepEquals(a, b) : left is null && right is null;

    internal bool IsValid(Func<JsonElement, bool> literal) => Status switch
    {
        "literal" => Value is { } value && literal(value),
        "dynamic" => Value is null && !string.IsNullOrEmpty(Expression),
        _ => false
    };

    internal static bool Number(JsonElement value) => value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number) && double.IsFinite(number);
}

public sealed record ConfigSettingSite(string Name, string Path, int Line, string Mechanism, string Type,
    [property: JsonPropertyName("default")] ConfigCatalogFact DefaultValue,
    ConfigCatalogFact? Minimum = null, ConfigCatalogFact? Maximum = null, ConfigCatalogFact? EnumValues = null,
    int? Flags = null, ImmutableArray<int>? FlagCandidates = null, string? FlagsExpression = null)
{
    public bool ProtectionKnown => Flags is not null || FlagCandidates is { IsEmpty: false };
    public int ConservativeFlags => (FlagCandidates ?? []).Aggregate(Flags ?? 0, (combined, flags) => combined | flags);

    internal bool IsValid => DefaultValue.IsValid(ValidDefault) &&
        (Minimum?.IsValid(ConfigCatalogFact.Number) ?? true) && (Maximum?.IsValid(ConfigCatalogFact.Number) ?? true) &&
        (EnumValues?.IsValid(value => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 &&
            value.EnumerateArray().All(element => element.ValueKind == JsonValueKind.String)) ?? true);

    private bool ValidDefault(JsonElement value) => Type switch
    {
        "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "string" => value.ValueKind == JsonValueKind.String,
        "int" or "enum" => ConfigCatalogFact.Number(value) && Math.Truncate(value.GetDouble()) == value.GetDouble(),
        "float" => ConfigCatalogFact.Number(value),
        "vec2" => Vector(value, 2),
        "vec3" => Vector(value, 3),
        "vec4" or "color" => Vector(value, 4),
        _ => false
    };

    private static bool Vector(JsonElement value, int count) => value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() == count && value.EnumerateArray().All(ConfigCatalogFact.Number);
}

public sealed record ConfigSettingVariant(ImmutableArray<ConfigSettingSite> Sites, int Flags,
    ImmutableArray<string> FlagNames, bool FlagsDynamic, bool ServerControlled,
    [property: JsonPropertyName("editable")] bool EditableHint)
{
    public string? Type => !Sites.IsEmpty && Sites.All(site => site.Type == Sites[0].Type) ? Sites[0].Type : null;
    public ConfigCatalogFact? DefaultValue => ConfigCatalogFact.Consensus(Sites.Select(site => site.DefaultValue));
    public ConfigCatalogFact? Minimum => ConfigCatalogFact.Consensus(Sites.Select(site => site.Minimum));
    public ConfigCatalogFact? Maximum => ConfigCatalogFact.Consensus(Sites.Select(site => site.Maximum));
    public ConfigCatalogFact? EnumValues => ConfigCatalogFact.Consensus(Sites.Select(site => site.EnumValues));
    public bool IsEditable => EditableHint && Type is not null && (Flags & 0xd0) == 0 &&
        Sites.All(site => site.ProtectionKnown && (site.ConservativeFlags & 0xd0) == 0);
}

public sealed record ConfigCatalogSetting(string Name, string Title, string Summary, string Details, string Group,
    ConfigCatalogControl Control, string Availability, ImmutableDictionary<string, ConfigSettingVariant> Variants,
    ImmutableArray<string> Origins, string Cod2x, ImmutableArray<string> Sources, bool Guided, bool? MenuExposed = null,
    ImmutableArray<string>? EffectSources = null, ImmutableArray<string>? RuntimeNotes = null)
{
    public ConfigSettingVariant? Variant(ConfigCatalogMode mode) => Variants.GetValueOrDefault(ConfigCatalog.ModeKey(mode));
    public bool IsEditable(ConfigCatalogMode mode) => Availability == "registered" && Variant(mode)?.IsEditable == true;
}

public sealed record ConfigCommandSite(string Name, string Path, int Line, string Mechanism);
public sealed record ConfigCommandVariant(ImmutableArray<ConfigCommandSite> Sites);
public sealed record ConfigCatalogCommand(string Name, string Title, string Summary, string Details, string Group,
    string Availability, ImmutableDictionary<string, ConfigCommandVariant> Variants, ImmutableArray<string> Origins,
    string Cod2x, ImmutableArray<string> Sources, bool Bindable, string? MenuLabel = null, ImmutableArray<string>? EffectSources = null)
{
    public ConfigCommandVariant? Variant(ConfigCatalogMode mode) => Variants.GetValueOrDefault(ConfigCatalog.ModeKey(mode));
}

public sealed record ConfigDynamicRegistration(string Path, int Line, string Mechanism, string Function, string NameExpression);

public sealed class ConfigCatalog
{
    private readonly FrozenDictionary<string, ConfigCatalogSetting> settingsByName;
    private readonly FrozenDictionary<string, ConfigCatalogCommand> commandsByName;
    private static readonly string[] GroupIds = ["aim", "net", "vis", "hud", "audio", "gfx", "frame", "other"];
    private static readonly string[] Modes = ["stock", "cod2x"];
    private static readonly string[] Availability = ["registered", "dispatchOnly", "referenceOnly", "scriptOrMod", "unavailable"];
    private static readonly string[] Origins = ["stock", "cod2x", "port"];
    private static readonly string[] SiteTypes = ["bool", "int", "float", "string", "color", "vec2", "vec3", "vec4", "enum"];
    private static readonly string[] ControlKinds = ["segmented", "slider", "menu", "colour", "text", "number", "vector", "switch"];

    public int SchemaVersion { get; }
    public string SourceRevision { get; }
    public ImmutableArray<ConfigCatalogGroup> Groups { get; }
    public ImmutableArray<ConfigCatalogSetting> Settings { get; }
    public ImmutableArray<ConfigCatalogCommand> Commands { get; }
    public ImmutableDictionary<string, ImmutableArray<ConfigDynamicRegistration>> DynamicRegistrations { get; }

    private ConfigCatalog(ConfigCatalogResource resource)
    {
        if (resource.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported config catalogue schema {resource.SchemaVersion}.");
        if (resource.Groups.Length != GroupIds.Length || !GroupIds.ToHashSet().SetEquals(resource.Groups.Select(group => group.Id)))
            throw new InvalidDataException("Invalid config catalogue groups.");

        SchemaVersion = resource.SchemaVersion;
        SourceRevision = resource.SourceRevision;
        Groups = resource.Groups;
        Settings = resource.Settings;
        Commands = resource.Commands;
        DynamicRegistrations = resource.DynamicRegistrations;

        foreach (var setting in Settings)
        {
            if (!ValidEntry(setting.Name, setting.Title, setting.Summary, setting.Details, setting.Group, setting.Availability,
                    setting.Origins, setting.Cod2x, setting.Sources, setting.Variants.Keys) ||
                !ControlKinds.Contains(setting.Control.Kind) || (setting.Availability == "registered") != (setting.Variants.Count > 0) ||
                setting.Variants.Values.Any(variant => variant.Sites.IsEmpty || variant.Sites.Any(site =>
                    !ValidSite(setting.Name, site.Name, site.Path, site.Line, site.Mechanism) || !SiteTypes.Contains(site.Type) || !site.IsValid)))
                throw new InvalidDataException($"Invalid config catalogue setting {setting.Name}.");
        }
        foreach (var command in Commands)
        {
            bool registered = command.Availability is "registered" or "dispatchOnly";
            if (!ValidEntry(command.Name, command.Title, command.Summary, command.Details, command.Group, command.Availability,
                    command.Origins, command.Cod2x, command.Sources, command.Variants.Keys) ||
                registered != (command.Variants.Count > 0) ||
                command.Variants.Values.Any(variant => variant.Sites.IsEmpty || variant.Sites.Any(site =>
                    !ValidSite(command.Name, site.Name, site.Path, site.Line, site.Mechanism))))
                throw new InvalidDataException($"Invalid config catalogue command {command.Name}.");
        }
        settingsByName = Index(Settings, setting => setting.Name);
        commandsByName = Index(Commands, command => command.Name);
    }

    private static bool ValidEntry(string name, string title, string summary, string details, string group, string availability,
        ImmutableArray<string> origins, string change, ImmutableArray<string> sources, IEnumerable<string> modes) =>
        name.Length > 0 && title.Length > 0 && summary.Length > 0 && details.Length > 0 && !sources.IsEmpty &&
        GroupIds.Contains(group) && Availability.Contains(availability) && origins.All(Origins.Contains) &&
        change is "unchanged" or "changed" or "added" && modes.All(Modes.Contains);

    private static bool ValidSite(string entry, string name, string path, int line, string mechanism) =>
        name.ToLowerInvariant() == entry.ToLowerInvariant() && path.Length > 0 && line > 0 && mechanism.Length > 0;

    private static FrozenDictionary<string, T> Index<T>(IEnumerable<T> entries, Func<T, string> name)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (!index.TryAdd(name(entry).ToLowerInvariant(), entry))
                throw new InvalidDataException($"Duplicate config catalogue entry {name(entry)}.");
        return index.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static ConfigCatalog LoadBundled()
    {
        using var stream = typeof(ConfigCatalog).Assembly.GetManifestResourceStream("ConfigCatalog.json")
            ?? throw new MissingManifestResourceException("ConfigCatalog.json");
        return Load(stream);
    }

    public static ConfigCatalog Load(Stream stream) =>
        new(JsonSerializer.Deserialize(stream, ConfigCatalogJson.Default.ConfigCatalogResource) ??
            throw new JsonException("Expected a config catalogue object."));

    public ConfigCatalogSetting? Setting(string name) => settingsByName.GetValueOrDefault(name.ToLowerInvariant());
    public ConfigCatalogCommand? Command(string name) => commandsByName.GetValueOrDefault(name.ToLowerInvariant());
    public IEnumerable<ConfigCatalogSetting> SettingsIn(string group) => Settings.Where(setting => setting.Group == group);
    public IEnumerable<ConfigCatalogCommand> CommandsIn(string group) => Commands.Where(command => command.Group == group);
    public IEnumerable<ConfigCatalogSetting> GuidedSettings(string group, ConfigCatalogMode mode) =>
        SettingsIn(group).Where(setting => setting.Guided && setting.IsEditable(mode));

    internal static string ModeKey(ConfigCatalogMode mode) => mode switch
    {
        ConfigCatalogMode.Stock => "stock",
        ConfigCatalogMode.Cod2x => "cod2x",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

internal sealed record ConfigCatalogResource(int SchemaVersion, string SourceRevision, ImmutableArray<ConfigCatalogGroup> Groups,
    ImmutableArray<ConfigCatalogSetting> Settings, ImmutableArray<ConfigCatalogCommand> Commands,
    ImmutableDictionary<string, ImmutableArray<ConfigDynamicRegistration>> DynamicRegistrations);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ConfigCatalogResource))]
internal sealed partial class ConfigCatalogJson : JsonSerializerContext;
