namespace CoD2Unbound.Core;

public static class WindowsReleaseAssets
{
    private const string Project = "https://github.com/cod2unbound/cod2-unbound/releases";
    public const int ChecksumsLimit = 64 * 1024;
    public const long SetupLimit = 512L * 1024 * 1024;
    public const string ChecksumsName = "SHA256SUMS";
    public static readonly Uri ReleasesPage = new(Project);
    public static readonly Uri Feed = new("https://api.github.com/repos/cod2unbound/cod2-unbound/releases?per_page=10");

    public static Version? ParseVersion(string value) =>
        Version.TryParse(value, out var version) && version is { Revision: -1, Major: <= 65535, Minor: <= 65535, Build: >= 0 and <= 65535 } &&
        version.ToString(3) == value ? version : null;

    public static bool IsNewer(string candidate, string current) =>
        ParseVersion(candidate) is { } offered && ParseVersion(current) is { } installed && offered > installed;

    public static bool IsLoopbackOrigin(Uri? origin) => origin is
        { IsAbsoluteUri: true, Scheme: "http" or "https", Host: "127.0.0.1", UserInfo.Length: 0, AbsolutePath: "/", Query.Length: 0, Fragment.Length: 0 };

    public static ReleaseOffer? Offer(string version, IEnumerable<ReleaseAsset> assets, Uri? loopbackOrigin = null)
    {
        if (ParseVersion(version) is null || (loopbackOrigin is not null && !IsLoopbackOrigin(loopbackOrigin)))
            return null;
        var page = loopbackOrigin is null ? Project : new Uri(loopbackOrigin, "releases").AbsoluteUri;
        string root = $"{page}/download/v{version}/";
        string name = $"CoD2-Unbound-{version}-windows-x64-setup.exe";
        var list = assets.ToLookup(asset => asset.Name, StringComparer.Ordinal);
        var setup = Trusted(list[name], SetupLimit, root + name);
        var sums = Trusted(list[ChecksumsName], ChecksumsLimit, root + ChecksumsName);
        return setup is not null && sums is not null ? new(version, setup, sums.Url, sums.Size) : null;
    }

    private static ReleaseAsset? Trusted(IEnumerable<ReleaseAsset> candidates, long limit, string address)
    {
        var entries = candidates.Take(2).ToArray();
        return entries is [var only] && only.Size >= 1 && only.Size <= limit && only.Url.IsAbsoluteUri &&
            only.Url.OriginalString == address ? only : null;
    }
}
