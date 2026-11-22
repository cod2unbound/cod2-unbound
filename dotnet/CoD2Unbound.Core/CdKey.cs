using System.Globalization;

namespace CoD2Unbound.Core;

public enum CdKeyState { Blank, Typing, Verified, Mismatch }

public static class CdKey
{
    public static string Normalize(string input) =>
        Uppercase(input.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal));

    public static bool IsValid(string input)
    {
        string key = Normalize(input);
        if (key.Length != 20)
            return false;
        uint checksum = 0;
        foreach (char ch in key.AsSpan(0, 16))
        {
            if (!char.IsAsciiLetterOrDigit(ch))
                return false;
            checksum ^= ch;
            for (int bit = 0; bit < 8; bit++)
                checksum = (checksum & 1) != 0 ? (checksum >> 1) ^ 0xa001 : checksum >> 1;
        }

        return key.AsSpan(16).Equals(checksum.ToString("X4", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    public static string Display(string input) => string.Join(' ',
        Uppercase(input).Where(char.IsAsciiLetterOrDigit).Take(20).Chunk(4).Select(group => new string(group)));

    public static (CdKeyState State, int Left) Check(string input)
    {
        int length = Uppercase(input).Where(char.IsAsciiLetterOrDigit).Take(20).Count();
        if (length == 0)
            return (CdKeyState.Blank, 0);
        if (length < 20)
            return (CdKeyState.Typing, 20 - length);
        return (IsValid(input) ? CdKeyState.Verified : CdKeyState.Mismatch, 0);
    }

    // Swift uppercasing expands these letters before filtering the key alphabet.
    private static string Uppercase(string input) => input.Replace("ß", "SS", StringComparison.Ordinal)
        .Replace("ﬀ", "FF", StringComparison.Ordinal).Replace("ﬁ", "FI", StringComparison.Ordinal)
        .Replace("ﬂ", "FL", StringComparison.Ordinal).Replace("ﬃ", "FFI", StringComparison.Ordinal)
        .Replace("ﬄ", "FFL", StringComparison.Ordinal).Replace("ﬅ", "ST", StringComparison.Ordinal)
        .Replace("ﬆ", "ST", StringComparison.Ordinal).ToUpperInvariant();
}
