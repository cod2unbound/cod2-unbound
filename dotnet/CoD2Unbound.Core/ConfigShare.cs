using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CoD2Unbound.Core;

public enum ConfigShareProblem { NothingToShare, TooLarge }

public sealed record ConfigShareLeftOut(int Id, string Shown, ImmutableArray<string> Reasons)
{
    public string Because => ConfigShareReasons.Words(Reasons);
}

public sealed record ConfigSharePrepared(ImmutableArray<byte> Bytes, int Settings, int Binds, ImmutableArray<ConfigShareLeftOut> LeftOut,
    ImmutableArray<string> Reasons, int Lines)
{
    public string Digest => Convert.ToHexStringLower(SHA256.HashData(Bytes.AsSpan()));
    public string Text => Encoding.UTF8.GetString(Bytes.AsSpan());
    public ConfigShareProblem? Problem => Lines < ConfigShare.MinimumLines
        ? ConfigShareProblem.NothingToShare
        : Bytes.Length > ConfigShare.MaximumBytes || Lines > ConfigShare.MaximumLines ? ConfigShareProblem.TooLarge : null;
}

public sealed record ConfigShareLeft(int Lines, ImmutableArray<string> Reasons);
public sealed record ConfigShareRequest(int V, string Name, string Config, string Game, string App, ConfigShareLeft Left);

public static class ConfigShare
{
    public const int MinimumLines = 10;
    public const int MaximumBytes = 65_535;
    public const int MaximumLines = 3000;
    public const int MaximumLineBytes = 1024;

    public static ImmutableArray<byte> LoadoutText(ConfigProfileRead profile)
    {
        var bytes = profile.Version.Config;
        if (profile.Version.Chain is not { IsEmpty: false } chain)
            return bytes;
        return !bytes.IsEmpty && bytes[^1] is not (10 or 13) ? [.. bytes, 10, .. chain] : [.. bytes, .. chain];
    }

    public static async ValueTask<ConfigSharePrepared> PrepareAsync(ImmutableArray<byte> source, IConfigImportCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var clean = await ConfigClean.CleanAsync(source, catalog, cancellationToken).ConfigureAwait(false);
        var left = ImmutableArray.CreateBuilder<ConfigShareLeftOut>();
        foreach (var row in clean.LeftOut)
            left.Add(new(left.Count, Shown(row.Text.AsSpan(), row.Tokens, row.Reasons), row.Reasons));

        using var kept = new MemoryStream(clean.Bytes.Length);
        int count = 0, settings = 0, binds = 0;
        ReadOnlySpan<byte> bytes = clean.Bytes.AsSpan();
        for (int offset = 0; offset < bytes.Length;)
        {
            int end = bytes[offset..].IndexOf((byte)10);
            if (end < 0)
                break;
            var line = bytes.Slice(offset, end);
            offset += end + 1;
            string? refusal = ConfigShareServiceRules.Refusal(line, out var tokens);
            if (refusal is not null)
            {
                left.Add(new(left.Count, Shown(line, tokens, [refusal]), [refusal]));
                continue;
            }
            kept.Write(line);
            kept.WriteByte(10);
            count++;
            string? command = tokens.FirstOrDefault()?.ToLowerInvariant();
            if (command is "set" or "seta" or "sets" or "setu")
                settings++;
            if (command == "bind")
                binds++;
        }

        var reasons = left.SelectMany(item => item.Reasons).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        return new(ImmutableArray.Create(kept.ToArray()), settings, binds, left.ToImmutable(), reasons, count);
    }

    public static ConfigShareRequest Request(ConfigSharePrepared prepared, string name, ConfigDialect dialect, string app) =>
        new(1, name, prepared.Text, dialect == ConfigDialect.Stock ? "stock" : "cod2x", AppVersion(app),
            new(Math.Min(prepared.LeftOut.Length, 5000), [.. prepared.Reasons.Take(16)]));

    public static string AppVersion(string version)
    {
        string[] parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(3)
            .Select(part => new string(part.TakeWhile(char.IsAsciiDigit).ToArray())).ToArray();
        return parts.Length == 3 && parts.All(part => part.Length is >= 1 and <= 4) ? string.Join('.', parts) : "0.0.0";
    }

    internal static ConfigShareReplyKind? ValidateRequest(ConfigShareRequest request)
    {
        if (Encoding.UTF8.GetByteCount(request.Config) > MaximumBytes)
            return ConfigShareReplyKind.TooLarge;
        if (request.V != 1 || ConfigShareName.Valid(request.Name) != request.Name ||
            request.Game is not ("stock" or "cod2x") || AppVersion(request.App) != request.App ||
            request.Left.Lines is < 0 or > 5000 || request.Left.Reasons.Length > 16 ||
            request.Left.Reasons.Any(reason => reason.Length is < 1 or > 32 || !reason.All(char.IsAsciiLetter)))
            return ConfigShareReplyKind.Unreachable;

        var lines = request.Config.Split('\n');
        int count = lines.Count(line => !string.IsNullOrWhiteSpace(line));
        if (count > MaximumLines)
            return ConfigShareReplyKind.TooLarge;
        if (count < MinimumLines || lines.Any(line => ConfigShareServiceRules.Refusal(Encoding.UTF8.GetBytes(line), out _) is not null))
            return ConfigShareReplyKind.Unreachable;
        return null;
    }

    private static string Shown(ReadOnlySpan<byte> bytes, ImmutableArray<string> tokens, ImmutableArray<string> reasons)
    {
        if (reasons.Contains("credential"))
        {
            if (tokens.IsEmpty)
                return "…";
            string command = tokens[0].ToLowerInvariant();
            if (tokens.Length >= 2 && (command is "set" or "seta" or "sets" or "setu") &&
                ConfigShareServiceRules.IsSettingName(tokens[1]))
                return $"{command} {tokens[1]} …";
            if (tokens.Length >= 2 && command == "bind" && ConfigDocument.KeyNumber(tokens[1]) is not null)
                return $"{command} {tokens[1]} …";
            return command.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') ? command + " …" : "…";
        }
        string line = Encoding.UTF8.GetString(bytes).Replace("\r", "").Replace('\n', ' ');
        int[] characters = StringInfo.ParseCombiningCharacters(line);
        return characters.Length <= 200 ? line : line[..characters[200]] + "…";
    }
}

public static class ConfigShareName
{
    private const string Marks = "-_.|[]()!^~*'+=";
    private const string InvalidCharacters = "A name can hold letters, digits, spaces and - _ . | [ ] ( ) ! ^ ~ * ' + =.";

    public static string? Valid(string name)
    {
        string? clean = Cleaned(name);
        return clean is { Length: > 0 } && ProblemForCleaned(clean) is null ? clean : null;
    }

    public static string? Problem(string name) => Cleaned(name) is { } clean ? ProblemForCleaned(clean) : InvalidCharacters;

    private static string? Cleaned(string name)
    {
        try { return string.Join(' ', name.Normalize().Split(' ', StringSplitOptions.RemoveEmptyEntries)); }
        catch (ArgumentException) { return null; }
    }

    private static string? ProblemForCleaned(string name)
    {
        if (name.EnumerateRunes().Count() > 32)
            return "A name can be up to 32 characters.";
        foreach (Rune rune in name.EnumerateRunes())
            if (!Rune.IsLetter(rune) && !Rune.IsNumber(rune) && rune.Value != ' ' &&
                !(rune.IsAscii && Marks.Contains((char)rune.Value)))
                return InvalidCharacters;
        return null;
    }
}

internal static class ConfigShareReasons
{
    private static readonly (string Key, string English)[] Reasons =
    [
        ("identity", "Your name or who you are in the game"),
        ("credential", "A password, key or login"),
        ("chat", "Chat or a message to other players"),
        ("connect", "Joins or leaves a server"),
        ("remoteConsole", "A command for a server’s admin"),
        ("filesystemPath", "A file path on your Mac"),
        ("hosting", "A setting for hosting a server"),
        ("macro", "A key that runs timed steps"),
        ("quit", "Closes the game"),
        ("unresolvedExec", "Runs another file"),
        ("unresolvedVariable", "Runs text that can’t be read ahead"),
        ("cycle", "Runs itself in a loop"),
        ("negativeWait", "Can stall the game"),
        ("protectedSetting", "A setting the game protects"),
        ("inertName", "Does nothing in CoD2"),
        ("unknownSetting", "A helper or a setting CoD2 doesn’t have"),
        ("malformed", "The game can’t read it reliably"),
        ("limit", "Too long for the game"),
        ("looseCommand", "Not a setting or a key bind"),
        ("notAccepted", "Player Setups takes only settings and key binds"),
        ("notImportable", "The import review doesn’t take it")
    ];

    internal static string Words(ImmutableArray<string> keys)
    {
        foreach (var (key, english) in Reasons)
            if (keys.Contains(key))
                return english;
        foreach (string key in keys)
            if (Enum.TryParse<ConfigImportRisk>(key, ignoreCase: true, out var reason) && reason.Key() == key)
                return reason.Summary();
        return "The import review doesn’t take it";
    }
}
