using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public sealed partial class ProblemReportPrivacy(string profile, string localAppData, IEnumerable<string> privateValues)
{
    private const string OmittedPath = @"%LOCALAPPDATA%\…";
    private readonly string[] names = privateValues.Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => new[] { value.Trim(), QuakeColors.Plain(value.Trim()) })
        .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(value => value.Length).ToArray();

    public string Fact(string value)
    {
        value = OneLine(value);
        return MayContainKey(value) ? "[details omitted]" : Scrub(value);
    }

    internal string? Log(string value)
    {
        if (MayContainKey(value))
            return null;
        string? redacted = CrashReporting.Redact(value, "");
        if (redacted is null)
            return null;
        return Scrub(string.Join('\n', redacted.Split('\n').Where(line => !CrashReporting.IsPrivate(line, [])))).Trim();
    }

    private static string OneLine(string value) =>
        new(value.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());

    private string Scrub(string value)
    {
        string plain = QuakeColors.Plain(value);
        foreach (string root in new[] { localAppData, profile }.Where(path => path.Length > 3))
        {
            string prefix = string.Join(@"[\\/]", root.TrimEnd('\\', '/').Split('\\', '/').Select(Regex.Escape));
            plain = Regex.Replace(plain, prefix + PathTail, OmittedPath, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        plain = ProfilePath().Replace(plain, OmittedPath);
        foreach (string name in names)
        {
            // Match whole tokens for short names so a user named "Al" does not remove "Radeon".
            string pattern = name.Length <= 3 ? @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])" : Regex.Escape(name);
            plain = Regex.Replace(plain, pattern, "[name omitted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return plain;
    }

    private static bool MayContainKey(string value)
    {
        string plain = QuakeColors.Plain(value);
        return CrashReporting.ContainsKey(plain) || KeyShape().IsMatch(plain);
    }

    private const string PathTail = """(?:[\\/][^\r\n"'<>|,;]*)?""";

    [GeneratedRegex("""(?:[A-Z]:|[\\]{2}[^\\/\s]+[\\/][^\\/\s]+)[\\/]Users[\\/][^\\/\r\n"'<>|,;]+""" + PathTail,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfilePath();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Za-z0-9]{4}[- ]?){4}[A-Fa-f0-9]{4}(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyShape();
}

public sealed record ProblemReport(string Details, string IssueUrl)
{
    public static ProblemReport Create(string version, string build, string editions, string os, string renderer,
        string display, string adapters, string attached, ProblemReportPrivacy privacy)
    {
        string release = privacy.Fact(build.Length == 0 ? version : $"{version} ({build})");
        editions = privacy.Fact(editions);
        os = privacy.Fact(os);
        string graphics = privacy.Fact(renderer);
        display = privacy.Fact(display);
        adapters = privacy.Fact(adapters);
        attached = privacy.Fact(attached);
        string details = $"CoD2 Unbound {release}\n{editions}\n{os}\nGraphics: {graphics} · Display: {display}\n" +
            $"Graphics adapters: {adapters}\nDisplay attached to: {attached}";
        string[] fields = [Field("version", release), Field("game_versions", editions), Field("windows", os),
            Field("graphics_display", $"{graphics} · {display}\nGraphics adapters: {adapters}\nDisplay attached to: {attached}")];
        return new(details, "https://github.com/cod2unbound/cod2-unbound/issues/new?template=windows_bug_report.yml&" +
            string.Join('&', fields));

        static string Field(string key, string value) => key + "=" + Uri.EscapeDataString(value);
    }
}

public sealed record ProblemLog(string Text, DateTimeOffset Modified);

public sealed record ProblemReportLogs(string Text, DateTimeOffset? GameCopied, DateTimeOffset? ServerCopied,
    bool GameRefused, bool ServerRefused)
{
    public static ProblemReportLogs Build(ProblemLog? game, ProblemLog? server, ProblemReportPrivacy privacy)
    {
        string? client = game is null ? null : privacy.Log(game.Text);
        string? host = server is null ? null : privacy.Log(server.Text);
        bool copyGame = !string.IsNullOrWhiteSpace(client), copyHost = !string.IsNullOrWhiteSpace(host);
        return new(copyGame && copyHost ? $"Game log\n\n{client}\n\nServer log\n\n{host}" : copyGame ? client! : copyHost ? host! : "",
            copyGame ? game!.Modified : null, copyHost ? server!.Modified : null,
            game is not null && client is null, server is not null && host is null);
    }
}
