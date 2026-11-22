namespace CoD2Unbound.Core;

public static class WindowsReleaseAssets
{
    private const string Project = "https://github.com/cod2unbound/cod2-unbound/releases";
    public const int ChecksumsLimit = 64 * 1024;
    public const long SetupLimit = 512L * 1024 * 1024;
    public const string ChecksumsName = "SHA256SUMS";
    public static readonly Uri ReleasesPage = new(Project);
    public static Uri NotesPage(string version) => ParseVersion(version) is null ? ReleasesPage : new($"{Project}/tag/v{version}");
    public static readonly Uri Feed = new("https://api.github.com/repos/cod2unbound/cod2-unbound/releases?per_page=10");

    public static Version? ParseVersion(string value) =>
        Version.TryParse(value, out var version) && version is { Revision: -1, Major: <= 65535, Minor: <= 65535, Build: >= 0 and <= 65535 } &&
        version.ToString(3) == value ? version : null;

    public static bool IsNewer(string candidate, string current) =>
        ParseVersion(candidate) is { } offered && ParseVersion(current) is { } installed && offered > installed;

    public static bool IsLoopbackOrigin(Uri? origin) => origin is
        { IsAbsoluteUri: true, Scheme: "http" or "https", Host: "127.0.0.1", UserInfo.Length: 0, AbsolutePath: "/", Query.Length: 0, Fragment.Length: 0 };

    public static ReleaseOffer? Offer(string version, IEnumerable<ReleaseAsset> assets, Uri? loopbackOrigin = null) =>
        Offer(version, assets, loopbackOrigin, out _);

    public static ReleaseOffer? Offer(string version, IEnumerable<ReleaseAsset> assets, Uri? loopbackOrigin, out string? problem)
    {
        problem = "The release version or update origin was invalid.";
        if (ParseVersion(version) is null || (loopbackOrigin is not null && !IsLoopbackOrigin(loopbackOrigin)))
            return null;
        string downloads = loopbackOrigin is null ? ReleasePackage.GitHubDownloads : new Uri(loopbackOrigin, "releases/download/").AbsoluteUri;
        string root = $"{downloads}v{version}/";
        string? mirror = loopbackOrigin is null ? $"{ReleasePackage.MirrorDownloads}v{version}/" : null;
        string name = $"CoD2-Unbound-{version}-windows-x64-setup.exe";
        var list = assets.ToLookup(asset => asset.Name, StringComparer.Ordinal);
        var setup = Trusted(list[name], SetupLimit, root, mirror, name, out problem);
        if (setup is null)
            return null;
        var sums = Trusted(list[ChecksumsName], ChecksumsLimit, root, mirror, ChecksumsName, out problem);
        return sums is not null ? new(version, setup, sums.Url, sums.Size) : null;
    }

    private static ReleaseAsset? Trusted(IEnumerable<ReleaseAsset> candidates, long limit, string root, string? mirror, string name,
        out string? problem)
    {
        var entries = candidates.Take(2).ToArray();
        problem = entries switch
        {
            not [var _] => "The release was missing a required asset or listed it more than once.",
            [var only] when only.Size < 1 || only.Size > limit => "A required release asset had a size outside the allowed range.",
            [var only] when !only.Url.IsAbsoluteUri || (only.Url.OriginalString != root + name &&
                (mirror is null || only.Url.OriginalString != mirror + name)) =>
                "A required release asset had an unexpected address.",
            _ => null
        };
        return problem is null ? entries[0] : null;
    }
}
