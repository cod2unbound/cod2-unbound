using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public sealed record ReleaseDispatch(string Stamp, string Title, string Summary, string? Link, DateTimeOffset? Date, string? Version);
public sealed record ReleaseAsset(string Name, long Size, Uri Url);
public sealed record ReleaseNoteItem(string Tag, string Title, string Detail);
public sealed record ReleaseFeed(ImmutableArray<ReleaseDispatch> Dispatches, string? LatestVersion,
    ImmutableArray<ReleaseAsset> LatestAssets, ImmutableArray<ReleaseNoteItem> LatestNotes, Uri? LatestUrl = null,
    bool ListedByGitHub = true);

public static partial class ReleaseNotes
{
    public const int FeedLimit = 1_000_000;
    internal static readonly Uri MirrorUrl = new("https://cod2unbound.com/update/releases.json");
    private static readonly string[] ReleaseDates = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:sszzz"];
    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    public static async ValueTask<ReleaseFeed> FetchAsync(ReleaseDownload download, TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        if (download.LoopbackOrigin is { } origin)
            return await ReadAsync(new Uri(origin, "releases"), listedByGitHub: false).ConfigureAwait(false);
        try { return await ReadAsync(WindowsReleaseAssets.Feed, listedByGitHub: true).ConfigureAwait(false); }
        catch (Exception error) when (error is JsonException or LauncherUpdateException ||
            ReleaseDownload.IsTransportFailure(error, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ReadAsync(MirrorUrl, listedByGitHub: false).ConfigureAwait(false);
        }

        async Task<ReleaseFeed> ReadAsync(Uri url, bool listedByGitHub)
        {
            var (status, bytes) = await download.ReadFeedAsync(url, FeedLimit, cancellationToken).ConfigureAwait(false);
            if (status != HttpStatusCode.OK)
                throw ReleaseDownload.Unavailable();
            return Parse(bytes.AsSpan(), timeZone: timeZone) with { ListedByGitHub = listedByGitHub };
        }
    }

    public static ReleaseFeed Parse(ReadOnlySpan<byte> data, int limit = 3, TimeZoneInfo? timeZone = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (data.Length > FeedLimit)
            throw new InvalidDataException("The release feed is too large.");

        var parsed = JsonSerializer.Deserialize(data, ReleaseJson.Default.ImmutableArrayGitHubRelease);
        if (parsed.IsDefault || parsed.Any(release => release is null || release.Assets?.Any(asset => asset is null) == true))
            throw new JsonException("Expected release and asset records.");
        foreach (var release in parsed)
            foreach (var asset in release.Assets ?? [])
                if (asset.Size is { } size)
                {
                    try { _ = GameSettingsJson.Integer(size); }
                    catch (Exception error) when (error is FormatException or InvalidOperationException)
                    { throw new JsonException("Expected an integer release asset size.", error); }
                }

        var releases = parsed.Take(50).Where(release => release.Draft != true).ToArray();
        var dispatches = ImmutableArray.CreateBuilder<ReleaseDispatch>();
        foreach (var release in releases.Take(limit))
        {
            string tag = Prefix(release.Tag ?? "", 32);
            string title = Prefix((release.Name ?? "").Trim(), 80);
            DateTimeOffset? date = DateTimeOffset.TryParseExact(release.Published, ReleaseDates,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published) ? published : null;
            string stamp = "NEW";
            if (date is { } value)
            {
                var day = TimeZoneInfo.ConvertTime(value, timeZone ?? TimeZoneInfo.Utc);
                stamp = FormattableString.Invariant($"{day.Day:00} {Months[day.Month - 1]}");
            }
            string? link = ReleaseLink(release.HtmlUrl)?.OriginalString;
            dispatches.Add(new(stamp, title.Length == 0 ? $"CoD2 Unbound {Version(tag)}" : title, Summary(release.Body ?? ""), link,
                date, tag.Length == 0 ? null : Version(tag)));
        }

        var latest = releases.FirstOrDefault(release => release.Prerelease != true);
        var assets = ImmutableArray.CreateBuilder<ReleaseAsset>();
        foreach (var asset in (latest?.Assets ?? []).Take(64))
            if (asset.Name is { } name && asset.Size is { } size && asset.Url is { } link && Uri.TryCreate(link, UriKind.RelativeOrAbsolute, out var url))
                assets.Add(new(name, GameSettingsJson.Integer(size), url));

        return new(dispatches.ToImmutable(), latest?.Tag is { } latestTag ? Version(latestTag) : null,
            assets.ToImmutable(), Items(latest?.Body ?? ""), ReleaseLink(latest?.HtmlUrl));
    }

    private static Uri? ReleaseLink(string? link) => link is { Length: <= 256 } && Uri.TryCreate(link, UriKind.Absolute, out var url)
        && ReleasePackage.IsProjectLink(url) ? url : null;

    public static string Version(string tag) => Prefix(TrimHorizontal(tag).TrimStart('v', 'V'), 32);

    public static bool IsNewer(string candidate, string current) =>
        CultureInfo.InvariantCulture.CompareInfo.Compare(candidate, current, CompareOptions.NumericOrdering) > 0;

    public static string Summary(string body, int length = 160)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var paragraph = new List<string>();
        foreach (string raw in Lines(body, 65_536))
        {
            string line = TrimHorizontal(raw);
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('|') || line.StartsWith('>') || line.StartsWith('<') ||
                line.StartsWith("\x60\x60\x60", StringComparison.Ordinal))
            {
                if (paragraph.Count > 0)
                    break;
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ||
                line.StartsWith("+ ", StringComparison.Ordinal))
                line = line[2..];
            paragraph.Add(line);
        }
        return Shorten(Plain(string.Join(' ', paragraph)), length);
    }

    public static ImmutableArray<ReleaseNoteItem> Items(string body, int limit = 12, int length = 180)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var result = ImmutableArray.CreateBuilder<ReleaseNoteItem>();
        var bullet = new List<string>();
        string? tag = null;

        void Flush()
        {
            if (tag is not null && bullet.Count > 0 && result.Count < limit)
            {
                string text = Plain(string.Join(' ', bullet));
                if (text.Length > 0)
                {
                    Match end = SentenceEnd().Match(text);
                    string title = end.Success ? text[..end.Index] : text;
                    string detail = end.Success ? TrimHorizontal(text[(end.Index + end.Length)..]) : "";
                    result.Add(new(tag, Prefix(title, 120), Shorten(detail, length)));
                }
            }
            bullet.Clear();
        }

        foreach (string raw in Lines(body, 200_000))
        {
            string line = TrimHorizontal(raw);
            if (line.StartsWith('#'))
            {
                Flush();
                tag = line.StartsWith("###", StringComparison.Ordinal)
                    ? TrimHorizontal(line.TrimStart('#')).ToLowerInvariant() switch
                    {
                        "added" or "new" => "New", "changed" or "improved" => "Changed", "fixed" => "Fixed", _ => null
                    } : null;
            }
            else if (raw.StartsWith("- ", StringComparison.Ordinal) || raw.StartsWith("* ", StringComparison.Ordinal))
            {
                Flush();
                // Swift's dropFirst tolerates the empty bullet after trailing whitespace is trimmed.
                bullet.Add(line[Math.Min(2, line.Length)..]);
            }
            else if (line.Length == 0)
                Flush();
            else if (bullet.Count > 0)
                bullet.Add(line);
        }
        Flush();
        return result.ToImmutable();
    }

    private static string[] Lines(string text, int maximum) => Prefix(text, maximum).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    private static string Plain(string text) => TrimHorizontal(Spaces().Replace(Markers().Replace(Links().Replace(text, "$1"), ""), " "));

    private static string Shorten(string value, int length)
    {
        var elements = new StringInfo(value);
        if (elements.LengthInTextElements <= length)
            return value;
        string cut = elements.SubstringByTextElements(0, length);
        int stop = cut.LastIndexOf('.');
        if (stop >= 0 && new StringInfo(cut[..stop]).LengthInTextElements > length / 2)
            return cut[..(stop + 1)];
        return string.Join(' ', cut.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipLast(1)) + "…";
    }

    private static string Prefix(string value, int limit)
    {
        var elements = new StringInfo(value);
        return elements.LengthInTextElements <= limit ? value : elements.SubstringByTextElements(0, limit);
    }

    private static string TrimHorizontal(string value) => GameSettings.TrimHorizontal(value);

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Links();
    [GeneratedRegex(@"[*\x60]+", RegexOptions.CultureInvariant)]
    private static partial Regex Markers();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
    [GeneratedRegex(@"[.:!?](\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();
}

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? Tag = null, string? Name = null, string? Body = null,
    [property: JsonPropertyName("published_at")] string? Published = null,
    [property: JsonPropertyName("html_url")] string? HtmlUrl = null,
    bool? Draft = null, bool? Prerelease = null, ImmutableArray<GitHubAsset>? Assets = null);
internal sealed record GitHubAsset(string? Name = null, JsonElement? Size = null,
    [property: JsonPropertyName("browser_download_url")] string? Url = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImmutableArray<GitHubRelease>))]
internal sealed partial class ReleaseJson : JsonSerializerContext;
