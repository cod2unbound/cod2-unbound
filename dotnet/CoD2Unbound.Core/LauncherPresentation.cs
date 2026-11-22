using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core;

public enum MapScenery { Desert, Europe, Winter }

public static class MapCatalog
{
    private static readonly Dictionary<string, (string Name, string Region, MapScenery Scenery)> Stock = new(StringComparer.Ordinal)
    {
        ["toujane"] = ("Toujane", "NORTH AFRICA", MapScenery.Desert),
        ["matmata"] = ("Matmata", "NORTH AFRICA", MapScenery.Desert),
        ["decoy"] = ("Decoy", "NORTH AFRICA", MapScenery.Desert),
        ["carentan"] = ("Carentan", "NORMANDY", MapScenery.Europe),
        ["brecourt"] = ("Brécourt", "NORMANDY", MapScenery.Europe),
        ["dawnville"] = ("Dawnville", "NORMANDY", MapScenery.Europe),
        ["breakout"] = ("Breakout", "NORMANDY", MapScenery.Europe),
        ["farmhouse"] = ("Farmhouse", "NORMANDY", MapScenery.Europe),
        ["trainstation"] = ("Trainstation", "NORMANDY", MapScenery.Europe),
        ["burgundy"] = ("Burgundy", "FRANCE", MapScenery.Europe),
        ["rhine"] = ("Rhine", "GERMANY", MapScenery.Europe),
        ["leningrad"] = ("Leningrad", "EASTERN FRONT", MapScenery.Winter),
        ["railyard"] = ("Railyard", "EASTERN FRONT", MapScenery.Winter),
        ["harbor"] = ("Harbor", "EASTERN FRONT", MapScenery.Winter),
        ["downtown"] = ("Downtown", "EASTERN FRONT", MapScenery.Winter),
        ["stalingrad"] = ("Stalingrad", "EASTERN FRONT", MapScenery.Winter)
    };

    public static ImmutableArray<string> StockKeys { get; } = [.. Stock.Keys.Order(StringComparer.Ordinal)];

    public static string? Key(string map)
    {
        if (map.Length is < 1 or > 64 || !map.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            return null;
        string lower = map.ToLowerInvariant();
        string key = lower.StartsWith("mp_", StringComparison.Ordinal) ? lower[3..] : lower;
        return key.Length > 0 ? key : null;
    }

    public static string DisplayName(string map) => Key(map) is not { } key ? "Unknown map" :
        Stock.TryGetValue(key, out var item) ? item.Name :
        string.Join(' ', key.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    public static string? Region(string map) => Key(map) is { } key && Stock.TryGetValue(key, out var item) ? item.Region : null;

    public static MapScenery Scenery(string map)
    {
        if (Key(map) is not { } key)
            return MapScenery.Desert;
        return Stock.TryGetValue(key, out var item)
            ? item.Scenery : (MapScenery)(key.Aggregate(0, (seed, letter) => (seed * 31 + letter) & 0xffff) % 3);
    }
}

public static class GameModes
{
    public static string Short(string code)
    {
        string trimmed = code.Trim(' ', '\t', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004',
            '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u202f', '\u205f', '\u3000');
        return trimmed.Length == 0 ? "—" : LauncherText.Prefix(trimmed, 12).ToUpperInvariant();
    }

    public static string Long(string code) => code.ToLowerInvariant() switch
    {
        "tdm" => "Team Deathmatch",
        "dm" => "Deathmatch",
        "sd" => "Search & Destroy",
        "hq" => "Headquarters",
        "ctf" => "Capture the Flag",
        _ => Short(code)
    };
}

public enum ServerOccupancy { Empty, Open, Full }
public sealed record ServerFacts(bool IsCod2x, string VersionLabel, int PingLevel, ServerOccupancy Occupancy, string Access)
{
    public static ServerFacts From(GameServer server)
    {
        string version = server.Version == "1.4.x" ? "1.4" : LauncherText.Prefix(server.Version, 16);
        return new(server.IsCod2x, (server.IsCod2x ? "CoD2x " : "Original ") + version,
            server.Ping switch { < 40 => 4, < 60 => 3, < 80 => 2, _ => 1 },
            server.PlayerCount == 0 ? ServerOccupancy.Empty
                : server.MaxPlayers > 0 && server.PlayerCount >= server.MaxPlayers ? ServerOccupancy.Full : ServerOccupancy.Open,
            server.PasswordRequired ? "Password" : "Open");
    }
}

public static class LauncherText
{
    internal static string Prefix(string text, int elements)
    {
        int[] offsets = StringInfo.ParseCombiningCharacters(text);
        return offsets.Length <= elements ? text : text[..offsets[elements]];
    }

    public static string StencilDay(DateTimeOffset date) =>
        date.ToString("dd MMM", CultureInfo.InvariantCulture).ToUpperInvariant();

    public static string StencilTime(DateTimeOffset date) =>
        StencilDay(date) + date.ToString(" HH:mm", CultureInfo.InvariantCulture);

    public static string Resolution(string value) => value.Replace("x", " × ", StringComparison.Ordinal);

    public static string Aspect(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return "";
        double ratio = (double)width / height;
        (string Label, double Ratio)[] known = [("16:9", 16.0 / 9), ("16:10", 1.6), ("4:3", 4.0 / 3), ("5:4", 1.25),
            ("3:2", 1.5), ("21:9", 64.0 / 27), ("32:9", 32.0 / 9)];
        return known.FirstOrDefault(entry => Math.Abs(entry.Ratio - ratio) / entry.Ratio < 0.012).Label
            ?? FormattableString.Invariant($"{ratio:F2}:1");
    }

    public static string MaskedKey(string lastFour) => "•••• •••• •••• •••• " + lastFour.ToUpperInvariant();
}

public sealed record NewsDispatch(string Stamp, string Title, string Summary, string? Link = null,
    DateTimeOffset? Date = null, string? Version = null, string? Open = null);

public sealed record NewsFeed(ImmutableArray<NewsDispatch> Items, NewsDispatch? Next)
{
    private const int FeedLimit = 65_536;
    public static Uri Url { get; } = new("https://raw.githubusercontent.com/cod2unbound/cod2-unbound/main/docs/news.json");
    internal static Uri MirrorUrl { get; } = new("https://cod2unbound.com/update/news.json");

    public static async ValueTask<ImmutableArray<byte>?> FetchAsync(ReleaseDownload download, CancellationToken cancellationToken)
    {
        if (download.LoopbackOrigin is not null)
            return null;
        foreach (var source in new[] { Url, MirrorUrl })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (status, bytes) = await download.ReadFeedAsync(source, FeedLimit, cancellationToken).ConfigureAwait(false);
                if (status == HttpStatusCode.OK)
                    return bytes.IsEmpty ? null : bytes;
                if (status == HttpStatusCode.NotFound && source == Url)
                    return ImmutableArray<byte>.Empty;
            }
            catch (Exception error) when (ReleaseDownload.IsTransportFailure(error, cancellationToken)) { }
            catch (LauncherUpdateException) { return null; } // A 200 whose body exceeds the limit is malformed news.
        }
        return null;
    }

    public static NewsFeed Parse(ReadOnlyMemory<byte> data, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        if (data.Length > FeedLimit)
            throw new InvalidDataException("The news file is too large.");
        using var json = JsonDocument.Parse(data);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected the project news object.");
        var items = new List<NewsDispatch>();
        if (Value(json.RootElement, "items") is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } entries)
        {
            if (entries.ValueKind != JsonValueKind.Array)
                throw new JsonException("Expected an array of news items.");
            foreach (var entry in entries.EnumerateArray().Take(50))
            {
                if (Dispatch(entry) is not { } dispatch ||
                    !DateOnly.TryParseExact(Text(entry, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ||
                    day.Year < 2026)
                    continue;
                var start = day.ToDateTime(TimeOnly.MinValue);
                var date = new DateTimeOffset(start, timeZone.GetUtcOffset(start));
                if (date <= now)
                    items.Add(dispatch with { Date = date, Stamp = LauncherText.StencilDay(date) });
            }
        }
        return new([.. items.OrderByDescending(item => item.Date).Take(5)], Dispatch(Value(json.RootElement, "next")));
    }

    public static ImmutableArray<NewsDispatch> Home(string version, IEnumerable<ReleaseDispatch> releases,
        IEnumerable<NewsDispatch> news, NewsDispatch? next = null, int rows = 3)
    {
        var available = releases.Select(item => new NewsDispatch(item.Stamp, item.Title, item.Summary, item.Link, item.Date, item.Version)).ToArray();
        var own = available.FirstOrDefault(item => item.Version == version || item.Title == version ||
            item.Title.EndsWith(" " + version, StringComparison.Ordinal)) ?? new("NOW", "CoD2 Unbound " + version,
                "What changed in this version is in the release notes.", "https://github.com/cod2unbound/cod2-unbound/releases", Version: version);
        int limit = rows <= 1 ? 1 : rows - (next is null ? 0 : 1);
        var visible = available.Concat(news).Where(item => item.Date is not null && item != own).Append(own)
            .OrderByDescending(item => item.Date).Take(limit).ToList();
        if (!visible.Contains(own))
            visible[^1] = own;
        if (next is not null)
            visible.Add(next);
        return [.. visible];
    }

    private static NewsDispatch? Dispatch(JsonElement item)
    {
        if (item.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        if (item.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a news item.");
        string title = Flatten(Text(item, "title"), 80);
        if (title.Length == 0)
            return null;
        string? link = Text(item, "link"), open = Text(item, "open");
        if (link is not { Length: <= 256 } || !link.StartsWith("https://github.com/", StringComparison.Ordinal) ||
            link.Any(char.IsWhiteSpace) || !Uri.TryCreate(link, UriKind.Absolute, out var parsed) ||
            !parsed.IsDefaultPort || parsed.Host != "github.com" || parsed.UserInfo.Length != 0 || link.Contains('\\'))
            link = null;
        return new("NEXT", title, Flatten(Text(item, "summary"), 160), link, Open:
            open is "configs" or "servers" or "library" or "updates" ? open : null);
    }

    private static string Flatten(string? raw, int limit)
    {
        var flat = new StringBuilder();
        foreach (Rune rune in (raw ?? "").EnumerateRunes().Take(2_000))
        {
            var category = Rune.GetUnicodeCategory(rune);
            flat.Append(category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? " " : rune.ToString());
        }
        string line = string.Join(' ', flat.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return LauncherText.Prefix(line, limit) == line ? line : LauncherText.Prefix(line, limit - 1).TrimEnd() + "…";
    }

    private static JsonElement Value(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var property) ? property : default;

    private static string? Text(JsonElement item, string key) => Value(item, key) switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        _ => throw new JsonException("Expected a news text field.")
    };
}
