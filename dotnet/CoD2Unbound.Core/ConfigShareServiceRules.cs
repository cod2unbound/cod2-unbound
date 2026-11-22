using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

internal static partial class ConfigShareServiceRules
{
    internal static string? Refusal(ReadOnlySpan<byte> line, out ImmutableArray<string> tokens)
    {
        tokens = [];
        if (!line.IsEmpty && line[^1] == 13)
            line = line[..^1];
        if (line.Length > ConfigShare.MaximumLineBytes)
            return "notAccepted";
        foreach (byte value in line)
            if (value != 9 && value is < 32 or > 126)
                return "notAccepted";

        string text = Encoding.ASCII.GetString(line);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (Tokenize(text) is not { IsEmpty: false } parsed)
            return "notAccepted";

        tokens = parsed;
        return tokens[0].ToLowerInvariant() switch
        {
            "set" or "seta" or "sets" or "setu" when tokens.Length >= 3 && IsSettingName(tokens[1]) =>
                Setting(tokens[1]) ?? Script(string.Join(' ', tokens.Skip(2))),
            "bind" when tokens.Length >= 2 && Key(tokens[1]) => Script(string.Join(' ', tokens.Skip(2))),
            "unbind" when tokens.Length == 2 && Key(tokens[1]) => null,
            "unbindall" when tokens.Length == 1 => null,
            _ => "notAccepted"
        };
    }

    internal static bool IsSettingName(string name) =>
        name.Length > 0 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static bool Key(string token) => token.Length is >= 1 and <= 32 && token.All(ch => ch is >= '!' and <= '~');

    private static string? Setting(string name)
    {
        string lower = name.ToLowerInvariant();
        if (ConfigClean.Hosting(lower))
            return "hosting";
        if (lower is "name" or "cl_guid")
            return "identity";
        if (lower.Contains("password", StringComparison.Ordinal) || lower.Contains("cdkey", StringComparison.Ordinal) ||
            lower.Split('_').Any(word => word is "key" or "keys"))
            return "credential";
        return null;
    }

    private static string? Script(string text)
    {
        foreach (Match word in Words().Matches(text.ToLowerInvariant()))
        {
            string value = word.Value;
            string? reason = value switch
            {
                "say" or "say_team" or "sayteam" => "chat",
                "connect" or "reconnect" => "connect",
                "wait" => "macro",
                "exec" => "unresolvedExec",
                "quit" => "quit",
                _ when value.StartsWith("rcon", StringComparison.Ordinal) => "remoteConsole",
                _ => Setting(value)
            };
            if (reason is not null)
                return reason;
        }
        return null;
    }

    // The service accepts one command per line and rejects external comments or separators.
    private static ImmutableArray<string>? Tokenize(string line)
    {
        var tokens = ImmutableArray.CreateBuilder<string>();
        for (int i = 0; i < line.Length;)
        {
            if (line[i] is ' ' or '\t')
            {
                i++;
                continue;
            }
            if (line[i] == '"')
            {
                int closing = line.IndexOf('"', i + 1);
                if (closing < 0)
                    return null;
                tokens.Add(line[(i + 1)..closing]);
                i = closing + 1;
                continue;
            }
            if (line[i] == ';' || CommentAt(line, i))
                return null;
            int start = i;
            while (i < line.Length && line[i] is not (' ' or '\t' or '"' or ';') && !CommentAt(line, i))
                i++;
            tokens.Add(line[start..i]);
        }
        return tokens.ToImmutable();
    }

    private static bool CommentAt(string line, int index) =>
        line[index] == '/' && index + 1 < line.Length && line[index + 1] is '/' or '*';

    [GeneratedRegex("[a-z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
