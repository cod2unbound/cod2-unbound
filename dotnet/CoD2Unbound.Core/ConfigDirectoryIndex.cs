using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core;

public enum ConfigDirectoryAttribution { SelfPublished, Listed, Uploaded }
public enum ConfigDirectoryPlatform { Windows, Native }
public sealed record ConfigDirectoryProvenance(string PublishedBy, string RetrievedAt,
    ConfigDirectoryAttribution Attribution, string Changes);
public sealed record ConfigDirectoryFacts(string? Sensitivity, string? FrameCap, string? Resolution);
public sealed record ConfigDirectoryEntry(string Id, string Player, string? Team, string Era, string? Role,
    string Feel, string Story, ImmutableArray<string> StyleTags, string? FavoriteMap, string Credit, Uri SourceUrl,
    ConfigDirectoryProvenance Provenance, ConfigDirectoryFacts? Facts, bool? Featured, Uri ConfigUrl, string Sha256,
    int ByteCount, ConfigDirectoryPlatform Platform);
public sealed record ConfigDirectoryIndex(int SchemaVersion, long Revision, ImmutableArray<ConfigDirectoryEntry> Entries);

internal static class ConfigDirectoryIndexReader
{
    private static readonly string[] IndexFields = ["schemaVersion", "revision", "entries"];
    private static readonly string[] EntryFields = ["id", "player", "era", "feel", "story", "styleTags", "credit",
        "sourceURL", "provenance", "configURL", "sha256", "byteCount", "platform"];
    private static readonly string[] OptionalEntryFields = ["team", "role", "favoriteMap", "facts", "featured"];
    private static readonly string[] ProvenanceFields = ["publishedBy", "retrievedAt", "attribution", "changes"];
    private static readonly string[] FactsFields = ["sensitivity", "frameCap", "resolution"];

    internal static ConfigDirectoryIndex Read(ImmutableArray<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory());
            var root = document.RootElement;
            CheckShape(root, IndexFields);
            int schema = root.GetProperty("schemaVersion").GetInt32();
            long revision = root.GetProperty("revision").GetInt64();
            var entries = root.GetProperty("entries");
            if (schema != 1 || revision < 1 || entries.GetArrayLength() > ConfigDirectory.MaximumEntries)
                throw InvalidIndex();

            var result = ImmutableArray.CreateBuilder<ConfigDirectoryEntry>(entries.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in entries.EnumerateArray())
            {
                var entry = ReadEntry(value);
                if (!ids.Add(entry.Id))
                    throw InvalidIndex();
                result.Add(entry);
            }
            return new(schema, revision, result.MoveToImmutable());
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            throw new ConfigDirectoryException(ConfigDirectoryFailure.InvalidIndex, error);
        }
    }

    internal static bool ValidId(string value) => value.Length is >= 1 and <= 64 && IsIdChar(value[0]) &&
        value.All(ch => IsIdChar(ch) || ch == '-');

    internal static bool PublicUrl(Uri value) => ConfigFetchRequest.IsPermitted(value) &&
        value.Scheme == Uri.UriSchemeHttps && value.Query.Length == 0 && value.Fragment.Length == 0;

    private static bool IsIdChar(char ch) => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch);

    private static ConfigDirectoryEntry ReadEntry(JsonElement value)
    {
        CheckShape(value, EntryFields, OptionalEntryFields);
        string id = RequiredText(value, "id");
        if (!ValidId(id))
            throw InvalidIndex();

        var tags = value.GetProperty("styleTags");
        if (tags.GetArrayLength() > 16)
            throw InvalidIndex();
        var styleTags = tags.EnumerateArray().Select(tag => ParseText(tag, 64)).ToImmutableArray();
        if (styleTags.Select(tag => tag.Normalize()).Distinct(StringComparer.Ordinal).Count() != styleTags.Length)
            throw InvalidIndex();

        string sha256 = RequiredText(value, "sha256");
        int byteCount = value.GetProperty("byteCount").GetInt32();
        if (byteCount is < 1 or > 65_535 || sha256.Length != 64 ||
            !sha256.All(ch => char.IsAsciiHexDigitLower(ch)))
            throw InvalidIndex();

        var platform = RequiredText(value, "platform") switch
        {
            "windows" => ConfigDirectoryPlatform.Windows,
            "native" => ConfigDirectoryPlatform.Native,
            _ => throw InvalidIndex()
        };

        return new(id, RequiredText(value, "player"), OptionalText(value, "team"), RequiredText(value, "era"),
            OptionalText(value, "role"), RequiredText(value, "feel"), RequiredText(value, "story", 8192),
            styleTags, OptionalText(value, "favoriteMap"), RequiredText(value, "credit"), ReadUrl(value, "sourceURL"),
            ReadProvenance(value.GetProperty("provenance")),
            value.TryGetProperty("facts", out var facts) ? ReadFacts(facts) : null,
            value.TryGetProperty("featured", out var featured) && featured.ValueKind != JsonValueKind.Null
                ? featured.GetBoolean() : null,
            ReadUrl(value, "configURL"), sha256, byteCount, platform);
    }

    private static ConfigDirectoryProvenance ReadProvenance(JsonElement value)
    {
        CheckShape(value, ProvenanceFields);
        string date = RequiredText(value, "retrievedAt");
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
            parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != date)
            throw InvalidIndex();

        var attribution = RequiredText(value, "attribution") switch
        {
            "self-published" => ConfigDirectoryAttribution.SelfPublished,
            "listed" => ConfigDirectoryAttribution.Listed,
            "uploaded" => ConfigDirectoryAttribution.Uploaded,
            _ => throw InvalidIndex()
        };
        return new(RequiredText(value, "publishedBy"), date, attribution, RequiredText(value, "changes", 512));
    }

    private static ConfigDirectoryFacts ReadFacts(JsonElement value)
    {
        CheckShape(value, [], FactsFields);
        return new(OptionalText(value, "sensitivity", 32), OptionalText(value, "frameCap", 32),
            OptionalText(value, "resolution", 32));
    }

    private static void CheckShape(JsonElement element, string[] required, string[]? optional = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || (!required.Contains(property.Name) && optional?.Contains(property.Name) != true))
                throw InvalidIndex();
        if (!required.All(seen.Contains))
            throw InvalidIndex();
    }

    private static string RequiredText(JsonElement element, string key, int limit = 256) =>
        ParseText(element.GetProperty(key), limit);

    private static string? OptionalText(JsonElement element, string key, int limit = 256) =>
        element.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? ParseText(value, limit) : null;

    private static string ParseText(JsonElement element, int limit)
    {
        string? text = element.GetString();
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > limit ||
            text.Any(ch => (ch < ' ' && ch != '\n') || ch == '\u007f'))
            throw InvalidIndex();
        return text;
    }

    private static Uri ReadUrl(JsonElement element, string key) =>
        Uri.TryCreate(element.GetProperty(key).GetString(), UriKind.Absolute, out var value) && PublicUrl(value)
            ? value : throw InvalidIndex();

    private static ConfigDirectoryException InvalidIndex() => new(ConfigDirectoryFailure.InvalidIndex);
}
