namespace CoD2Unbound.Core;

/// <summary>A shell link requests an edition and a validated connect target.</summary>
public sealed record ServerLink(GameEdition Edition, ServerAddress Target)
{
    public static ServerLink? Parse(string input)
    {
        const string cod2x = "cod2x://";
        const string original = "cod2://";
        GameEdition edition;
        string rest;
        if (input.StartsWith(cod2x, StringComparison.OrdinalIgnoreCase))
            (edition, rest) = (GameEdition.Cod2x, input[cod2x.Length..]);
        else if (input.StartsWith(original, StringComparison.OrdinalIgnoreCase))
            (edition, rest) = (GameEdition.Original, input[original.Length..]);
        else
            return null;

        if (LaunchLink.TryParse(cod2x + rest, out var command))
            return new(edition, new(LaunchLink.CanonicalAddress(command.Address),
                command.Password.Length == 0 ? null : command.Password));

        // The Mac also accepts a bare address, with connect/ and a trailing slash optional.
        if (rest.EndsWith('/'))
            rest = rest[..^1];
        if (rest.StartsWith("connect/", StringComparison.OrdinalIgnoreCase))
            rest = rest[8..];
        string address = Uri.UnescapeDataString(rest);
        return address.Length <= 255 && LaunchLink.ValidAddress(address)
            ? new(edition, new(LaunchLink.CanonicalAddress(address), null))
            : null;
    }
}
