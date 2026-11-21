namespace CoD2Unbound.Core;

/// <summary>The same restricted connect/password syntax as the engine's cod2x URL handler.</summary>
public sealed record LaunchLink
{
    public const int DefaultPort = 28960;
    public const string InvalidMessage = "This link is invalid. Only connect and password are accepted.";

    public string Address { get; }
    public string Password { get; }
    public bool HasPassword { get; }
    public string Url { get; }

    private LaunchLink(string address, string? password, string url) =>
        (Address, Password, HasPassword, Url) = (address, password ?? "", password is not null, url);

    public override string ToString() => $"LaunchLink {{ Address = {Address} }}";

    public static LaunchLink Direct(string address, string password = "") =>
        Parse("cod2x://" + Uri.EscapeDataString($"connect {address} password \"{password}\""));

    public static string CanonicalAddress(string address)
    {
        string parsed = Direct(address.Trim()).Address.ToLowerInvariant();
        return parsed.Contains(':') ? parsed : FormattableString.Invariant($"{parsed}:{DefaultPort}");
    }

    public static LaunchLink Parse(string url) =>
        TryParse(url, out var link) ? link : throw new FormatException(InvalidMessage);

    public static bool TryParse(string url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LaunchLink? link)
    {
        link = null;
        if (!url.StartsWith("cod2x://", StringComparison.OrdinalIgnoreCase))
            return false;

        Span<char> decoded = stackalloc char[1023];
        int length = 0;
        for (int index = 8; index < url.Length; index++)
        {
            char character = url[index];
            if (character == '%')
            {
                if (index + 2 >= url.Length || Hex(url[index + 1]) is not int high || Hex(url[index + 2]) is not int low)
                    return false;
                character = (char)(high * 16 + low);
                index += 2;
            }
            else if (character == '+')
            {
                character = ' ';
            }

            if (character is < ' ' or >= '\x7f' or ';' or '\\' || length == decoded.Length)
                return false;
            decoded[length++] = character;
        }
        if (length > 0 && decoded[length - 1] == '/')
            length--;
        var rest = decoded[..length];
        if (rest.StartsWith("connect/", StringComparison.Ordinal))
            rest[7] = ' ';

        string? address = null, password = null;
        ReadOnlySpan<char> remaining = rest.TrimStart(' ');
        while (!remaining.IsEmpty)
        {
            if (!ReadToken(ref remaining, 31, out var operation) || !ReadToken(ref remaining, 255, out var value))
                return false;

            if (operation.StartsWith("+", StringComparison.Ordinal))
                operation = operation[1..];
            if (operation.SequenceEqual("connect") && address is null && ValidAddress(value))
                address = value.ToString();
            else if (operation.SequenceEqual("password") && password is null && value.Length < 128 && !value.Contains('"'))
                password = value.ToString();
            else
                return false;

            remaining = remaining.TrimStart(' ');
        }

        if (address is null)
            return false;
        link = new(address, password, url);
        return true;
    }

    internal static bool ValidAddress(ReadOnlySpan<char> address)
    {
        int colon = address.IndexOf(':');
        var host = colon < 0 ? address : address[..colon];
        if (host.Length is < 1 or > 253)
            return false;
        foreach (char character in host)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.'))
                return false;
        if (colon < 0)
            return true;

        var port = address[(colon + 1)..];
        int number = 0;
        foreach (char character in port)
        {
            if (!char.IsAsciiDigit(character))
                return false;
            number = number * 10 + character - '0';
            if (number > 65535)
                return false;
        }
        return number > 0;
    }

    private static bool ReadToken(ref ReadOnlySpan<char> remaining, int limit, out ReadOnlySpan<char> token)
    {
        remaining = remaining.TrimStart(' ');
        bool quoted = remaining.StartsWith("\"", StringComparison.Ordinal);
        if (quoted)
            remaining = remaining[1..];
        int stop = remaining.IndexOf(quoted ? '"' : ' ');
        token = stop < 0 ? remaining : remaining[..stop];
        if (token.Length > limit || !quoted && token.IsEmpty || quoted &&
            (stop < 0 || stop + 1 < remaining.Length && remaining[stop + 1] != ' '))
            return false;
        remaining = stop < 0 ? [] : remaining[(stop + (quoted ? 1 : 0))..];
        return true;
    }

    private static int? Hex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => null
    };
}
