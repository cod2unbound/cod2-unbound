using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void ConfigCases()
    {
        using var file = Fixtures("config");
        var root = file.RootElement;
        foreach (var expected in root.GetProperty("documents").EnumerateArray())
        {
            Check(expected.String("id")!, () =>
            {
                var doc = new ConfigDocument(expected.Bytes("bytes"), Dialect(expected));
                Equal(expected.String("serialized"), Convert.ToBase64String(doc.Serialized.AsSpan()));
                Equal(expected.GetProperty("canonical").GetBoolean(), doc.IsCanonical);
                var commands = expected.GetProperty("commands").EnumerateArray().ToArray();
                Equal(commands.Length, doc.Commands.Length);
                for (int index = 0; index < commands.Length; index++)
                {
                    var command = doc.Commands[index];
                    var row = commands[index];
                    SpanEqual(row.GetProperty("span"), command.Span);
                    SpanEqual(row.GetProperty("separator"), command.Separator);
                    Equal(row.GetProperty("limited").GetBoolean(), command.SplitAtLimit);
                    Equal(row.String("kind"), Key(command.Kind));
                    TokenizationEqual(row, command.Syntax);
                }
                var diagnostics = expected.GetProperty("diagnostics").EnumerateArray().ToArray();
                Equal(diagnostics.Length, doc.Diagnostics.Length);
                for (int index = 0; index < diagnostics.Length; index++)
                {
                    var diagnostic = doc.Diagnostics[index];
                    var row = diagnostics[index];
                    Equal(row.String("kind"), Key(diagnostic.Kind));
                    if (row.TryGetProperty("span", out var span))
                        SpanEqual(span, diagnostic.Span!.Value);
                    else
                        Equal(null, diagnostic.Span);
                    if (row.TryGetProperty("discarded", out var removed))
                        SpanEqual(removed, diagnostic.Discarded!.Value);
                    else
                        Equal(null, diagnostic.Discarded);
                    Equal(row.OptionalString("failure"), diagnostic.Failure is { } failure ? Key(failure) : null);
                }
            });
        }

        foreach (var expected in root.GetProperty("tokenizations").EnumerateArray())
            Check(expected.String("id")!, () => TokenizationEqual(expected, ConfigDocument.Tokenize(
                expected.Bytes("bytes"), Dialect(expected), expected.Int("limit"), expected.Int("offset"))));

        foreach (var expected in root.GetProperty("edits").EnumerateArray())
            Check(expected.String("id")!, () => AssertEdit(expected));

        foreach (var key in root.GetProperty("keys").EnumerateArray())
        {
            string name = key.String("name")!;
            Check($"key {name}", () =>
            {
                int? folded = ConfigDocument.KeyNumber(name);
                Equal(key.NullableInt("folded"), folded);
                Equal(key.NullableInt("exact"), ConfigDocument.KeyNumber(name, folding: false));
                Equal(key.String("spelling"), folded is { } number ? ConfigDocument.KeySpelling(number) : null);
            });
        }

        CatalogueCases(root.GetProperty("catalogue"));
    }

    private static void AssertEdit(JsonElement expected)
    {
        var input = expected.Bytes("input");
        var original = new ConfigDocument(input, Dialect(expected));
        var result = original;
        string name = expected.String("name")!, action = expected.String("action")!;
        string? asText = expected.OptionalString("textInput");
        byte[] value = expected.Bytes("value");
        string? failure = null;
        try
        {
            result = action switch
            {
                "setSetting" => asText is null ? original.SetSetting(name, value) : original.SetSetting(name, asText),
                "setBind" => asText is null ? original.SetBind(name, value) : original.SetBind(name, asText),
                "removeSetting" => original.RemoveSetting(name),
                "removeBind" => original.RemoveBind(name),
                _ => throw new InvalidDataException(action)
            };
        }
        catch (ConfigEditException error)
        {
            failure = Key(error.Failure);
        }

        Equal(expected.OptionalString("error"), failure);
        Sequence(input, original.Serialized);
        Equal(expected.String("serialized"), Convert.ToBase64String(result.Serialized.AsSpan()));
        bool bind = action.EndsWith("Bind", StringComparison.Ordinal);
        var actual = bind ? result.EffectiveBindBytes(name) : result.EffectiveSettingBytes(name);
        Equal(expected.String("effective"), actual is { } bytes ? Convert.ToBase64String(bytes.AsSpan()) : null);
        Equal(expected.String("effectiveText"), bind ? result.EffectiveBind(name) : result.EffectiveSetting(name));
        var unresolved = expected.GetProperty("unresolved").EnumerateArray().ToArray();
        var commands = result.UnresolvedCommands.ToArray();
        Equal(unresolved.Length, commands.Length);
        for (int index = 0; index < unresolved.Length; index++)
            SpanEqual(unresolved[index], commands[index].Span);
    }

    private static void TokenizationEqual(JsonElement expected, ConfigTokenization actual)
    {
        Equal(expected.String("failure"), actual.Failure is { } failure ? Key(failure) : null);
        var comments = expected.GetProperty("comments").EnumerateArray().ToArray();
        Equal(comments.Length, actual.Comments.Length);
        for (int index = 0; index < comments.Length; index++)
            SpanEqual(comments[index], actual.Comments[index]);
        var tokens = expected.GetProperty("tokens").EnumerateArray().ToArray();
        Equal(tokens.Length, actual.Tokens.Length);
        for (int index = 0; index < tokens.Length; index++)
        {
            var token = actual.Tokens[index];
            var row = tokens[index];
            SpanEqual(row.GetProperty("span"), token.Span);
            Sequence(row.Bytes("bytes"), token.Value);
            Equal(row.String("kind"), Key(token.Kind));
            Equal(row.GetProperty("closed").GetBoolean(), token.Closed);
        }
    }

    private static void CatalogueCases(JsonElement expected)
    {
        ConfigCatalog? catalog = null;
        Check("bundled catalogue", () =>
        {
            catalog = ConfigCatalog.LoadBundled();
            Equal(expected.Int("settingCount"), catalog.Settings.Length);
            Equal(expected.Int("commandCount"), catalog.Commands.Length);
            Equal(expected.Int("schemaVersion"), catalog.SchemaVersion);
            Equal(expected.String("revision"), catalog.SourceRevision);
            Sequence(expected.GetProperty("groups").EnumerateArray().Select(group => group.String("id")), catalog.Groups.Select(group => group.Id));
        });
        if (catalog is null)
            return;

        foreach (var group in expected.GetProperty("groups").EnumerateArray())
        {
            string id = group.String("id")!;
            Check($"catalogue group {id}", () =>
            {
                Equal(group.String("title"), catalog.Groups.Single(g => g.Id == id).Title);
                Sequence(group.Strings("settings"), catalog.SettingsIn(id).Select(setting => setting.Name));
                Sequence(group.Strings("commands"), catalog.CommandsIn(id).Select(command => command.Name));
                foreach (var mode in Enum.GetValues<ConfigCatalogMode>())
                    Sequence(group.GetProperty("guided").Strings(Key(mode)), catalog.GuidedSettings(id, mode).Select(setting => setting.Name));
            });
        }

        foreach (var row in expected.GetProperty("settings").EnumerateArray())
        {
            string query = row.String("query")!;
            Check($"catalogue setting {query}", () =>
            {
                var setting = catalog.Setting(query);
                Equal(row.OptionalString("name"), setting?.Name);
                if (setting is null)
                    return;
                Equal(row.String("group"), setting.Group);
                Equal(row.String("title"), setting.Title);
                Equal(row.String("availability"), setting.Availability);
                var variants = row.GetProperty("variants").EnumerateArray().ToArray();
                Equal(variants.Length, setting.Variants.Count);
                foreach (var variant in variants)
                {
                    var mode = Enum.Parse<ConfigCatalogMode>(variant.String("mode")!, ignoreCase: true);
                    var actual = setting.Variant(mode)!;
                    Equal(variant.Int("flags"), actual.Flags);
                    Equal(variant.String("type"), actual.Type);
                    Equal(variant.GetProperty("editable").GetBoolean(), setting.IsEditable(mode));
                    FactEqual(variant.GetProperty("default"), actual.DefaultValue);
                    FactEqual(variant.GetProperty("minimum"), actual.Minimum);
                    FactEqual(variant.GetProperty("maximum"), actual.Maximum);
                    FactEqual(variant.GetProperty("enumValues"), actual.EnumValues);
                }
            });
        }

        foreach (var row in expected.GetProperty("commands").EnumerateArray())
        {
            string query = row.String("query")!;
            Check($"catalogue command {query}", () =>
            {
                var command = catalog.Command(query);
                Equal(row.OptionalString("name"), command?.Name);
                if (command is null)
                    return;
                Equal(row.GetProperty("bindable").GetBoolean(), command.Bindable);
                Sequence(row.Strings("modes"), Enum.GetValues<ConfigCatalogMode>().Where(mode => command.Variant(mode) is not null).Select(Key));
            });
        }

        foreach (var row in expected.GetProperty("rejected").EnumerateObject())
        {
            Check($"catalogue validation {row.Name}", () =>
            {
                using var source = typeof(ConfigCatalog).Assembly.GetManifestResourceStream("ConfigCatalog.json")!;
                var json = JsonNode.Parse(source)!.AsObject();
                switch (row.Name)
                {
                    case "unknownSchema":
                        json["schemaVersion"] = 999;
                        break;
                    case "missingGroups":
                        json["groups"] = new JsonArray();
                        break;
                    case "duplicateSetting":
                        var entries = json["settings"]!.AsArray();
                        var duplicate = entries[0]!.DeepClone();
                        duplicate["name"] = duplicate["name"]!.GetValue<string>().ToUpperInvariant();
                        entries.Add(duplicate);
                        break;
                    default: throw new InvalidDataException(row.Name);
                }
                using var bytes = new MemoryStream();
                using (var writer = new Utf8JsonWriter(bytes))
                    json.WriteTo(writer);
                bytes.Position = 0;
                bool rejected = false;
                try { ConfigCatalog.Load(bytes); }
                catch (InvalidDataException) { rejected = true; }
                Equal(row.Value.GetBoolean(), rejected);
            });
        }
    }

    private static void FactEqual(JsonElement expected, ConfigCatalogFact? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Equal(null, actual);
            return;
        }
        Equal(expected.String("status"), actual!.Status);
        Equal(expected.OptionalString("expression"), actual.Expression);
        if (expected.TryGetProperty("value", out var value))
            Equal(true, actual.Value is { } actualValue && JsonElement.DeepEquals(value, actualValue));
        else
            Equal(null, actual.Value);
    }

    private static ConfigDialect Dialect(JsonElement item) => Enum.Parse<ConfigDialect>(item.String("dialect")!, ignoreCase: true);
    private static void SpanEqual(JsonElement range, ByteSpan span)
    {
        Equal(range[0].GetInt32(), span.Start);
        Equal(range[1].GetInt32(), span.End);
    }
    private static string Key<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}

internal static class FixtureFields
{
    internal static string? String(this JsonElement item, string name) => item.GetProperty(name).GetString();
    internal static string? OptionalString(this JsonElement item, string name) => item.TryGetProperty(name, out var field) ? field.GetString() : null;
    internal static int Int(this JsonElement item, string name) => item.GetProperty(name).GetInt32();
    internal static int? NullableInt(this JsonElement item, string name) =>
        item.GetProperty(name) is { ValueKind: JsonValueKind.Number } field ? field.GetInt32() : null;
    internal static byte[] Bytes(this JsonElement item, string name) => item.GetProperty(name).GetBytesFromBase64();
    internal static IEnumerable<string?> Strings(this JsonElement item, string name) =>
        item.GetProperty(name).EnumerateArray().Select(value => value.GetString());
}
