namespace CoD2Unbound.Core;

public static class SetupLink
{
    private const string Prefix = "cod2unbound://setup/";

    public static bool IsSetupScheme(string input) => input.StartsWith("cod2unbound:", StringComparison.OrdinalIgnoreCase);

    public static string? Parse(string input)
    {
        // Match the original spelling before URI decoding or normalization can disguise an extra path component.
        if (!input.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string id = input[Prefix.Length..];
        if (id.EndsWith('/'))
            id = id[..^1];
        return ConfigDirectoryIndexReader.ValidId(id) ? id : null;
    }
}
