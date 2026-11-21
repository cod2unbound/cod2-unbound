using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public sealed record ConfigTextResponse(ImmutableArray<byte> Data, Uri FinalUrl, string? ContentType);

public sealed record ConfigFetchRequest(Uri Url, int MaximumBytes, Uri Root)
{
    private static readonly char[] AuthorityEnd = ['/', '?', '#'];
    private static readonly char[] PathEnd = ['?', '#'];
    private static readonly string[] UnsafeEscapes = ["%2e", "%2f", "%5c", "%25"];

    public bool Allows(Uri candidate)
    {
        if (!IsPermitted(Root) || !IsPermitted(candidate) || candidate.Scheme != Root.Scheme ||
            candidate.IdnHost != Root.IdnHost || candidate.Port != Root.Port)
            return false;

        string path = Uri.UnescapeDataString(candidate.AbsolutePath);
        string root = Uri.UnescapeDataString(Root.AbsolutePath);
        string prefix = root.EndsWith('/') ? root : root + '/';
        return path.Equals(root, StringComparison.Ordinal) || path.StartsWith(prefix, StringComparison.Ordinal);
    }

    internal static bool IsPermitted(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Host.Length == 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1")))
            return false;

        // Uri canonicalizes separators and dot components. Check the supplied spelling before using its path.
        string original = uri.OriginalString;
        int schemeEnd = original.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 1 || original.Any(ch => ch == '\\' || char.IsControl(ch)))
            return false;

        var rest = original.AsSpan(schemeEnd + 3);
        int end = rest.IndexOfAny(AuthorityEnd);
        var authority = end < 0 ? rest : rest[..end];
        if (authority.IsEmpty || authority.Contains('@'))
            return false;

        var path = end < 0 ? ReadOnlySpan<char>.Empty : rest[end..];
        int pathEnd = path.IndexOfAny(PathEnd);
        if (pathEnd >= 0)
            path = path[..pathEnd];
        string rawPath = path.ToString();
        return !UnsafeEscapes.Any(escape => rawPath.Contains(escape, StringComparison.OrdinalIgnoreCase)) &&
            !rawPath.Split('/').Any(segment => segment is "." or "..") &&
            !uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Any(item => CredentialParameter(Uri.UnescapeDataString(item.Split('=', 2)[0])));
    }

    internal static Uri Parent(Uri uri) => new(uri, ".");

    internal static bool SameResource(Uri expected, Uri actual) =>
        IsPermitted(actual) && expected.AbsoluteUri.Equals(actual.AbsoluteUri, StringComparison.Ordinal);

    internal static bool CredentialName(string name) =>
        name.Contains("password", StringComparison.Ordinal) || name.Contains("passwd", StringComparison.Ordinal) ||
        name.Contains("cdkey", StringComparison.Ordinal) || name.Contains("cd_key", StringComparison.Ordinal) ||
        name.Contains("token", StringComparison.Ordinal) || name.Contains("secret", StringComparison.Ordinal) ||
        name.StartsWith("cl_hwid", StringComparison.Ordinal) ||
        name is "match_login" or "codkey" or "auth" or "apikey" or "api_key";

    private static bool CredentialParameter(string name)
    {
        name = name.ToLowerInvariant();
        return CredentialName(name) || name is "key" or "signature" or "sig" or "access_key";
    }
}

public sealed class ConfigFetchHttpException(int statusCode) : IOException($"The config server returned HTTP {statusCode}.")
{
    public int StatusCode { get; } = statusCode;
}

// Only connectivity failures and request timeouts qualify for offline fallback.
public sealed class ConfigFetchUnavailableException(string message, Exception? innerException = null) :
    IOException(message, innerException);

public interface IConfigTextFetcher
{
    // Bound the response before allocation, and check request.Allows before the initial request
    // and each redirect. No cookies, stored credentials or ambient authorization.
    ValueTask<ConfigTextResponse> FetchAsync(ConfigFetchRequest request, CancellationToken cancellationToken);
}
