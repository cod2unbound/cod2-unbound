using System.Text.Json;

namespace CoD2Unbound.Core;

public enum ZpamLine { Four, Three }

public sealed record ZpamRelease(string Version, string Asset, Uri Url, long Bytes, string Sha256)
{
    private const string DownloadRoot = "https://github.com/eyza-cod2/zpam3/releases/download/";
    public static Uri ReleasesUrl { get; } = new("https://api.github.com/repos/eyza-cod2/zpam3/releases?per_page=30");
    public static Uri ProjectUrl { get; } = new("https://github.com/eyza-cod2/zpam3");

    public bool VerifyArchive(Stream archive, CancellationToken cancellationToken = default) =>
        ReleasePackage.VerifyArchive(archive, Bytes, Sha256, cancellationToken);

    internal bool HasVersionedAsset() => Version is { Length: 4 } && Version[0] is '3' or '4' && Version[1] == '.' &&
        char.IsAsciiDigit(Version[2]) && char.IsAsciiDigit(Version[3]) && Asset == $"zpam{Version[0]}{Version[2..]}.zip";

    public static ZpamRelease? Newest(ZpamLine line, ReadOnlySpan<byte> listing)
    {
        try
        {
            using var json = JsonDocument.Parse(listing.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return null;
            char major = line == ZpamLine.Four ? '4' : '3';
            foreach (var release in root.EnumerateArray())
            {
                if (Get(release, "draft").ValueKind == JsonValueKind.True || Get(release, "prerelease").ValueKind == JsonValueKind.True ||
                    Get(release, "assets") is not { ValueKind: JsonValueKind.Array } assets)
                    continue;
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = Text(asset, "name");
                    string? urlText = Text(asset, "browser_download_url");
                    string? digest = Text(asset, "digest");
                    var length = Get(asset, "size");
                    if (name is not { Length: 11 } || !name.StartsWith("zpam", StringComparison.Ordinal) ||
                        name[4] != major || !char.IsAsciiDigit(name[5]) || !char.IsAsciiDigit(name[6]) ||
                        !name.EndsWith(".zip", StringComparison.Ordinal) ||
                        urlText?.StartsWith(DownloadRoot, StringComparison.Ordinal) != true ||
                        !Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
                        !url.AbsoluteUri.StartsWith(DownloadRoot, StringComparison.Ordinal) || !ConfigFetchRequest.IsPermitted(url) ||
                        length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out long size) || size is < 1 or > 1_073_741_824 ||
                        digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                        !digest[7..].All(char.IsAsciiHexDigit))
                        continue;
                    return new($"{major}.{name[5..7]}", name, url, size, digest[7..].ToLowerInvariant());
                }
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static JsonElement Get(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? property : default;

    private static string? Text(JsonElement value, string key) =>
        Get(value, key) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;
}
