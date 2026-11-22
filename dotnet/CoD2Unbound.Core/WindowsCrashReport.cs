using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

/// The legacy crash logs and sidecars can contain arbitrary engine text. Accept only the
/// dedicated Windows upload format and rebuild the attachment from validated fields.
public static partial class WindowsCrashReport
{
    public const string FilePrefix = "cod2_unbound_crash_";
    public const string AttachmentName = "cod2_crash_windows.txt";
    public const int ReportsPerExit = 10;
    public const int LedgerLimit = 500;
    private const string Header = "CoD2 Unbound Windows crash 1";
    public const string Example = Header + "\nException: 0xC0000005 at 0x140001234\nFault: engine.exe+0x1234\n" +
        "Frame: engine.exe+0x1234\nModule: engine.exe 0.5.0.0\nEnd\n";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool IsReportName(string name) => FileName().IsMatch(name);

    public static string? Parse(ImmutableArray<byte> bytes, IEnumerable<string> privateValues)
    {
        if (bytes.IsDefaultOrEmpty || bytes.Length > CrashReporting.SizeLimit)
            return null;
        string input;
        try { input = Utf8.GetString(bytes.AsSpan()); }
        catch (DecoderFallbackException) { return null; }
        if (CrashReporting.ContainsKey(input))
            return null;
        string[] lines = input.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length is < 5 or > 165 || lines[0] != Header || lines[^2] != "End" || lines[^1].Length != 0 ||
            !ExceptionLine().IsMatch(lines[1]) || !FrameLine().IsMatch(lines[2]) || !lines[2].StartsWith("Fault: ", StringComparison.Ordinal))
            return null;

        string[] names = privateValues.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        bool modules = false;
        for (int index = 2; index < lines.Length - 2; index++)
        {
            string line = lines[index];
            if (line.Length > 120) return null;
            var field = line.StartsWith("Module: ", StringComparison.Ordinal) ? ModuleLine().Match(line) : FrameLine().Match(line);
            if (!field.Success)
                return null;
            if (line.StartsWith("Module: ", StringComparison.Ordinal))
                modules = true;
            else if (index > 2 && (modules || !line.StartsWith("Frame: ", StringComparison.Ordinal)))
                return null;
            string module = field.Groups["module"].Value;
            if (names.Any(value => Regex.IsMatch(module, @"(?<![a-z0-9])" + Regex.Escape(value) + @"(?![a-z0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                return null;
        }
        return string.Join('\n', lines);
    }

    internal static string Reason(string safeText) => safeText.Split('\n', 3)[1];

    [GeneratedRegex(@"\Acod2_unbound_crash_[0-9A-F]{16}_[0-9]{1,10}\.txt\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();
    [GeneratedRegex(@"\AException: 0x[0-9A-F]{8} at 0x[0-9A-F]{1,16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionLine();
    private const string Module = @"(?<module>unknown|[a-zA-Z0-9][a-zA-Z0-9_.-]{0,59}\.(?:dll|exe|drv))";
    [GeneratedRegex(@"\A(?:Fault|Frame): " + Module + @"\+0x[0-9A-F]{1,16}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FrameLine();
    [GeneratedRegex(@"\AModule: " + Module + @" (?:unknown|[0-9]{1,5}(?:\.[0-9]{1,5}){3})\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ModuleLine();
}
