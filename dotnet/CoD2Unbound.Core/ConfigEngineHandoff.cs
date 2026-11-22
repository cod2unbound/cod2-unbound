using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

/// <summary>Registration limits that may truncate a loadout when changing edition.</summary>
public sealed class ConfigEngineLimits
{
    private sealed record Limit(double? Minimum, double? Maximum, ImmutableHashSet<string>? Choices, bool Protected);
    private readonly Dictionary<(ConfigDialect, string), Limit> limits = new();

    public ConfigEngineLimits(ConfigCatalog catalog)
    {
        foreach (var setting in catalog.Settings.Where(item => item.Availability == "registered"))
        {
            Add(setting, ConfigDialect.Cod2x, ConfigCatalogMode.Cod2x, ConfigCatalogMode.Stock);
            Add(setting, ConfigDialect.Stock, ConfigCatalogMode.Stock, ConfigCatalogMode.Cod2x);
        }
    }

    private void Add(ConfigCatalogSetting setting, ConfigDialect from, ConfigCatalogMode source, ConfigCatalogMode destination)
    {
        if (setting.Variant(source) is not { } saved || setting.Variant(destination) is not { } other || (saved.Flags & 1) == 0)
            return;
        double? min = ConfigChecker.Literal(other.Minimum), max = ConfigChecker.Literal(other.Maximum);
        bool protectedValue = (other.Flags & 0xd0) != 0 && (saved.Flags & 0xd0) == 0;
        var choices = Entries(other.EnumValues);
        var previous = Entries(saved.EnumValues);
        if (protectedValue || min is { } low && (ConfigChecker.Literal(saved.Minimum) ?? double.NegativeInfinity) < low ||
            max is { } high && (ConfigChecker.Literal(saved.Maximum) ?? double.PositiveInfinity) > high ||
            choices is not null && (previous is null || !previous.IsSubsetOf(choices)))
            limits[(from, setting.Name.ToLowerInvariant())] = new(min, max, choices, protectedValue);
    }

    public ImmutableArray<string> Names(ConfigDialect dialect) =>
        [.. limits.Keys.Where(key => key.Item1 == dialect).Select(key => key.Item2).Order(StringComparer.Ordinal)];

    public bool Refuses(string text, string name, ConfigDialect dialect)
    {
        if (!limits.TryGetValue((dialect, name.ToLowerInvariant()), out var found))
            return false;
        if (found.Protected)
            return true;
        string value = text.Trim();
        if (found.Choices is { } choices)
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                ? index < 0 || index >= choices.Count : !choices.Contains(value);
        return ConfigRules.Number(value) is { } number && (number < found.Minimum || number > found.Maximum);
    }

    private static ImmutableHashSet<string>? Entries(ConfigCatalogFact? fact) =>
        fact is { Status: "literal", Value: { ValueKind: JsonValueKind.Array } array }
            ? array.EnumerateArray().Select(item => item.GetString()!).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase) : null;
}

public sealed partial class ConfigStudioSession
{
    private const string HandoffPath = "main/players/.studio-engine-handoff.json";

    public void HandOff(ConfigDialect target, ConfigEngineLimits limits)
    {
        CheckWritable(rejectDraft: true);
        var saved = files.Read(HandoffPath);
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> record = new(StringComparer.Ordinal);
        if (saved is { Length: > 0 and <= 1_048_576 } bytes)
        {
            try
            {
                record = JsonSerializer.Deserialize(bytes.AsSpan(), HandoffJson.Default.DictionaryStringDictionaryStringDictionaryStringString)
                    ?? record;
            }
            catch (JsonException) { }
        }
        string targetKey = Key(target);
        var permitted = limits.Names(target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (record.TryGetValue(targetKey, out var targetValues) && targetValues is not null)
        {
            foreach (var (id, values) in targetValues.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (values is null || !ConfigStudioLaunch.IsProfileId(id))
                    continue;
                ConfigProfileRead read;
                try { read = store.Read(id); }
                catch (ConfigProfileException error) when (error.Failure == ConfigProfileFailure.MissingProfile) { continue; }
                var document = read.Document;
                foreach (var (name, value) in values.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    if (value is null || !permitted.Contains(name) || !limits.Refuses(value, name, target) ||
                        document.EffectiveSetting(name) == value)
                        continue;
                    try { document = document.SetSetting(name, value); }
                    catch (ConfigEditException) { }
                }
                if (!document.Serialized.AsSpan().SequenceEqual(read.Version.Config.AsSpan()))
                    _ = store.Update(id, document, read.Version, null, target == ConfigDialect.Cod2x
                        ? "Put back CoD2x values that Original 1.3 can’t keep" : "Put back Original 1.3 values that CoD2x can’t keep");
            }
        }
        record.Remove(targetKey);
        var other = target == ConfigDialect.Stock ? ConfigDialect.Cod2x : ConfigDialect.Stock;
        if (ReadActive() is { } active)
        {
            string key = Key(other);
            foreach (string name in limits.Names(other))
            {
                if (active.Document.EffectiveSetting(name) is not { } value || !limits.Refuses(value, name, other))
                    continue;
                if (!record.TryGetValue(key, out var table) || table is null)
                    record[key] = table = new(StringComparer.Ordinal);
                if (!table.TryGetValue(active.Profile.Id, out var settings) || settings is null)
                    table[active.Profile.Id] = settings = new(StringComparer.Ordinal);
                settings.TryAdd(name, value);
            }
        }
        if (record.Count > 0)
            files.WriteAtomic(HandoffPath, [.. JsonSerializer.SerializeToUtf8Bytes(record,
                HandoffJson.Default.DictionaryStringDictionaryStringDictionaryStringString)]);
        else if (saved is not null)
            files.DeleteFile(HandoffPath);
        Refresh();

        static string Key(ConfigDialect dialect) => dialect == ConfigDialect.Cod2x ? "cod2x" : "stock";
    }
}

[JsonSerializable(typeof(Dictionary<string, Dictionary<string, Dictionary<string, string>>>))]
internal sealed partial class HandoffJson : JsonSerializerContext;
