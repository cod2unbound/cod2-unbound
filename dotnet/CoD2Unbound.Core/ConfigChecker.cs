using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core;

public enum ConfigCheckSeverity { Broken, Different, Note }
public enum ConfigCheckMark { Broken, Different, Note, Removed, Changed }

public sealed record ConfigCheckFix(string? Replacement, string Label);
public sealed record ConfigCheckFinding(ByteSpan Range, int Line, ConfigCheckSeverity Kind, string Title, string Detail,
    string Note, ConfigCheckFix? Fix)
{
    public int Id => Range.Start;
}
public sealed record ConfigCheckLine(int Id, ByteSpan Range, string Text);
public sealed record ConfigCheckShownLine(int Id, string Text, ConfigCheckMark? Mark, string Note);

public sealed record ConfigCheckReport(ImmutableArray<byte> Bytes, ImmutableArray<ConfigCheckLine> Lines,
    ImmutableArray<ConfigCheckFinding> Findings, int Commands)
{
    public IEnumerable<ConfigCheckFinding> Fixable => Findings.Where(item => item.Kind != ConfigCheckSeverity.Note && item.Fix is not null);

    public ImmutableArray<byte> Apply(IReadOnlySet<int> ids)
    {
        if (ids.Count == 0)
            return Bytes;
        var edits = Findings.Where(item => item.Fix is not null && ids.Contains(item.Id)).ToLookup(item => item.Line);
        var output = ImmutableArray.CreateBuilder<byte>(Bytes.Length);
        foreach (var line in Lines)
        {
            var fixes = edits[line.Id];
            if (!fixes.Any())
                output.AddRange(Bytes.AsSpan()[line.Range.Start..Math.Min(line.Range.End + 1, Bytes.Length)]);
            else if (Edit(line, fixes) is { } edited && !Blank(edited))
            {
                output.AddRange(edited);
                if (line.Range.End < Bytes.Length)
                    output.Add(Bytes[line.Range.End]);
            }
        }
        return output.ToImmutable();
    }

    public ImmutableArray<ConfigCheckShownLine> Show(IReadOnlySet<int> applied)
    {
        var byLine = Findings.ToLookup(item => item.Line);
        return [.. Lines.Select(line =>
        {
            var here = byLine[line.Id];
            var selected = here.Where(item => applied.Contains(item.Id) && item.Fix is not null).ToArray();
            if (selected.Length == 0)
            {
                var first = here.MinBy(item => item.Kind);
                return new ConfigCheckShownLine(line.Id, line.Text, (ConfigCheckMark?)first?.Kind, first?.Note ?? "");
            }
            var edited = Edit(line, selected);
            if (Blank(edited))
                return new(line.Id, line.Text, ConfigCheckMark.Removed, "Removed");
            string? label = selected.Length == 1 ? selected[0].Fix!.Label : null;
            string note = label?.StartsWith("Set to ", StringComparison.Ordinal) == true ? "Changed to " + label[7..] :
                label == "Rename" ? "Renamed" : "Changed";
            string text = line.Text.EndsWith(" \"••••••••\"", StringComparison.Ordinal)
                ? line.Text : ConfigChecker.Display(edited);
            return new(line.Id, text, ConfigCheckMark.Changed, note);
        })];
    }

    private byte[] Edit(ConfigCheckLine line, IEnumerable<ConfigCheckFinding> fixes)
    {
        var content = new List<byte>(Bytes.AsSpan()[line.Range.Start..line.Range.End].ToArray());
        foreach (var finding in fixes.OrderByDescending(item => item.Range.Start))
        {
            int offset = finding.Range.Start - line.Range.Start;
            if (offset < 0 || offset + finding.Range.Length > content.Count)
                continue;
            content.RemoveRange(offset, finding.Range.Length);
            if (finding.Fix?.Replacement is { } replacement)
                content.InsertRange(offset, ConfigDocument.Encode(replacement));
        }
        return [.. content];
    }

    private static bool Blank(byte[] content) => content.All(value => value is 9 or 13 or 32 or 59);
}

public static class ConfigChecker
{
    public static async ValueTask<ConfigCheckReport> CheckAsync(ImmutableArray<byte> data, ConfigDialect dialect,
        string? ruleset, ConfigCatalog catalog, ConfigRules? rules, CancellationToken cancellationToken = default)
    {
        var adapter = new ConfigImportCatalogAdapter(catalog);
        var bundle = await new ConfigImportLoader(adapter).LoadAsync(new ConfigImportInput.Paste(data), dialect, cancellationToken)
            .ConfigureAwait(false);
        var bytes = bundle.Files.First(file => file.Path == bundle.EntryPath).Document.Serialized;
        ConfigCatalogMode mode = dialect == ConfigDialect.Cod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock;
        string game = mode == ConfigCatalogMode.Cod2x ? "CoD2x" : "the original 1.3";
        string other = mode == ConfigCatalogMode.Cod2x ? "the original 1.3" : "CoD2x";

        var ranges = new List<ByteSpan>();
        int start = 0;
        for (int offset = 0; offset < bytes.Length; offset++)
        {
            if (bytes[offset] != 10)
                continue;
            ranges.Add(new(start, offset));
            start = offset + 1;
        }
        if (start < bytes.Length)
            ranges.Add(new(start, bytes.Length));
        int LineOf(int offset)
        {
            int low = 0, high = ranges.Count - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (ranges[middle].Start <= offset) low = middle;
                else high = middle - 1;
            }
            return low + 1;
        }

        var candidates = new ConfigImportSafety(adapter).Review(bundle).Candidates
            .Where(item => item.Location.File == bundle.EntryPath && item.Location.Context.IsEmpty && !item.Command.Syntax.Tokens.IsEmpty)
            .ToArray();
        var last = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in candidates)
            if (Assignment(item.Command) is { } assignment)
                last[assignment.Name] = item.Location.Span.Start;

        ConfigRuleset? policy = ruleset is null ? null : rules?.RuleSet(ruleset);
        var constraints = policy is null ? [] : rules!.ResolvedRules(policy.Id);
        var findings = ImmutableArray.CreateBuilder<ConfigCheckFinding>();
        var secrets = new Dictionary<int, string>();
        foreach (var candidate in candidates)
        {
            var range = candidate.Location.Span;
            int line = LineOf(range.Start);
            var reasons = candidate.Reasons;
            var tokens = candidate.Command.Syntax.Tokens;
            string verb = tokens[0].Text;
            var set = Assignment(candidate.Command);
            if (reasons.Contains(ConfigImportRisk.Credential))
                secrets[line] = string.Join(' ', tokens.Take(2).Select(token => token.Text)) + " \"••••••••\"";

            void Add(ConfigCheckSeverity kind, string title, string detail, string note) =>
                findings.Add(new(range, line, kind, title, detail, note, new(null, "Remove")));
            void Change(ConfigCheckSeverity kind, string title, string detail, string note, string? value, string? label = null,
                string? name = null)
            {
                string? written = value is null ? null : $"{verb} {name ?? set?.Name} \"{value}\"";
                // Display accepts source text, but a generated edit must still be one plain assignment.
                bool safe = value is not null && !value.AsSpan().ContainsAny("\"\r\n\0") && value.All(character => character <= 255) &&
                    !reasons.Contains(ConfigImportRisk.Credential);
                findings.Add(new(range, line, kind, title, detail, note, safe ? new(written, label ?? $"Set to {value}") : null));
            }

            if (reasons.Contains(ConfigImportRisk.Malformed) || candidate.Command.Syntax.Failure is not null)
                Add(ConfigCheckSeverity.Broken, "The game can’t read this line",
                    "A quote is left open or the line is too long, so the game would read something else than what is written.", "Can’t be read");
            else if (set is { } unknown && (reasons.Contains(ConfigImportRisk.UnknownSetting) ||
                reasons.Contains(ConfigImportRisk.InertName) || catalog.Setting(unknown.Name)?.Variant(mode) is null))
            {
                string name = unknown.Name;
                if (catalog.Setting(name) is { Variants.Count: > 0 } known && known.Variant(mode) is null)
                    Add(ConfigCheckSeverity.Broken, $"“{name}” isn’t a setting of {game}",
                        $"Only {other} has it. This line does nothing here.", $"Not in {(mode == ConfigCatalogMode.Cod2x ? "CoD2x" : "1.3")}");
                else if (Nearest(name, catalog, mode) is { } near)
                    Change(ConfigCheckSeverity.Broken, $"Unknown setting “{name}”",
                        $"Probably {near}, which is a setting of this game. As written, the line does nothing.",
                        $"Unknown setting. Did you mean {near}?", unknown.Value, "Rename", near);
                else
                    Add(ConfigCheckSeverity.Broken, $"“{name}” isn’t a Call of Duty 2 setting",
                        "It may belong to another game or to a mod. Without that, the line does nothing.", "Not a CoD2 setting");
            }
            else if (set is { } duplicate && last[duplicate.Name] != range.Start)
            {
                int winner = LineOf(last[duplicate.Name]);
                Add(ConfigCheckSeverity.Different, $"{duplicate.Name} is set twice",
                    $"Line {winner} sets it again and the later line wins, so this one changes nothing.", $"Set again on line {winner}");
            }
            else if (reasons.Contains(ConfigImportRisk.Credential))
                Add(ConfigCheckSeverity.Note, "A password is stored on this line",
                    "Anyone you share this file with would see it.", "Contains a password");
            else if (set is { } assignment && catalog.Setting(assignment.Name)?.Variant(mode) is { } variant)
            {
                string name = assignment.Name, value = assignment.Value;
                var flags = variant.FlagNames;
                double? number = ConfigRules.Number(value), minimum = Literal(variant.Minimum), maximum = Literal(variant.Maximum);
                if (!flags.Contains("archive") && (flags.Contains("readOnly") || flags.Contains("writeProtected")))
                    Add(ConfigCheckSeverity.Different, $"The game sets {name} itself",
                        "It is read-only, so the game ignores this line.", "Read-only");
                else if (flags.Contains("cheatProtected") && Standard(variant.DefaultValue) is { } standard &&
                    !ConfigRules.ValuesEqual(standard, value))
                    Add(ConfigCheckSeverity.Different, $"{name} only changes with cheats on",
                        $"On a server with cheats off the game puts it back to {standard}.", "Only works with cheats on");
                else if (number < minimum || number > maximum)
                {
                    bool below = number < minimum;
                    string bound = Text((below ? minimum : maximum)!.Value);
                    string direction = below ? "below" : "above";
                    Change(ConfigCheckSeverity.Different, $"{name} is {direction} the limit",
                        $"{name} takes {Span(minimum, maximum, false)}. The game refuses a value outside that and keeps the one it had.",
                        $"{(below ? "Below" : "Above")} the limit of {bound}", bound);
                }
                else if (policy is not null && constraints.FirstOrDefault(rule => !rule.Advisory &&
                    rule.Constraint.Kind != ConfigConstraintKind.Ownership && rule.Setting.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } rule &&
                    rules!.Evaluate(name, value, [policy.Id], mode).Status == ConfigRuleStatus.Violation)
                {
                    var constraint = rule.Constraint;
                    string title = policy.Title.Split(" / ")[^1];
                    switch (constraint.Kind)
                    {
                        case ConfigConstraintKind.Range:
                            double? target = number;
                            if (target < constraint.Minimum) target = constraint.Minimum;
                            if (target > constraint.Maximum) target = constraint.Maximum;
                            string span = Span(constraint.Minimum, constraint.Maximum, true);
                            Change(ConfigCheckSeverity.Note, $"{title} limits {name}", $"Those rules keep it {span}. Yours is {value}.",
                                $"{title} keeps this {span}", target is { } fixedNumber ? Text(fixedNumber) : null);
                            break;
                        case ConfigConstraintKind.Exact:
                            Change(ConfigCheckSeverity.Note, $"{title} sets {name}",
                                $"Those rules need {constraint.Value ?? "their own value"}. Yours is {value}.",
                                $"{title} needs {constraint.Value ?? "its own value"}", constraint.Value);
                            break;
                        default:
                            Change(ConfigCheckSeverity.Note, $"{title} doesn’t allow this value",
                                $"Yours is {value}." + (constraint.ResetValue is { } reset ? $" A server with those rules uses {reset}." : ""),
                                $"{title} doesn’t allow {value}", constraint.ResetValue);
                            break;
                    }
                }
            }
        }
        var lines = ranges.Select((range, index) => new ConfigCheckLine(index + 1, range,
            secrets.GetValueOrDefault(index + 1) ?? Display(bytes.AsSpan()[range.Start..range.End])));
        return new(bytes, [.. lines], [.. findings.OrderBy(item => item.Id)],
            candidates.Count(item => item.Command.Kind is ConfigCommandKind.Setting or ConfigCommandKind.Bind));
    }

    public static string Display(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes).TrimEnd('\r').Replace("\t", "    ", StringComparison.Ordinal);

    internal static (string Name, string Value)? Assignment(ConfigCommand command) =>
        command.Kind == ConfigCommandKind.Setting && command.Syntax.Tokens.Length > 2
            ? (command.Syntax.Tokens[1].Text, string.Join(' ', command.Syntax.Tokens.Skip(2).Select(token => token.Text)))
            : null;

    internal static double? Literal(ConfigCatalogFact? fact) =>
        fact is { Status: "literal", Value.ValueKind: JsonValueKind.Number } ? fact.Value.Value.GetDouble() : null;

    private static string? Standard(ConfigCatalogFact? fact) => fact is { Status: "literal", Value: { } item } ? item.ValueKind switch
    {
        JsonValueKind.Number => Text(item.GetDouble()),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.String => item.GetString(),
        _ => null
    } : null;

    private static string Text(double number) => (number == Math.Round(number) && Math.Abs(number) < 1e15
        ? number.ToString("0", CultureInfo.InvariantCulture) : number.ToString("g6", CultureInfo.InvariantCulture));

    private static string Span(double? low, double? high, bool policy) => (low, high) switch
    {
        ({ } min, { } max) => policy ? $"between {Text(min)} and {Text(max)}" : $"{Text(min)} to {Text(max)}",
        ({ } min, null) => (policy ? "at " : "") + Text(min) + " or more",
        (null, { } max) => (policy ? "at " : "") + Text(max) + " or less",
        _ => "in its own range"
    };

    public static string? Nearest(string name, ConfigCatalog catalog, ConfigCatalogMode mode)
    {
        byte[] typed = Encoding.UTF8.GetBytes(name.ToLowerInvariant());
        if (typed.Length < 4)
            return null;
        int allowed = typed.Length < 7 ? 1 : 2, bestDistance = allowed + 1;
        string? best = null;
        foreach (var setting in catalog.Settings.Where(item => item.Variant(mode) is not null))
        {
            byte[] other = Encoding.UTF8.GetBytes(setting.Name.ToLowerInvariant());
            if (Math.Abs(other.Length - typed.Length) > allowed)
                continue;
            int distance = Edits(typed, other, allowed);
            if (distance < bestDistance)
            {
                best = setting.Name;
                bestDistance = distance;
            }
            else if (distance == bestDistance)
                best = null;
        }
        return bestDistance is > 0 && bestDistance <= allowed ? best : null;
    }

    private static int Edits(byte[] typed, byte[] other, int limit)
    {
        var previous2 = new int[other.Length + 1];
        var previous = Enumerable.Range(0, other.Length + 1).ToArray();
        var current = new int[other.Length + 1];
        for (int i = 1; i <= typed.Length; i++)
        {
            current[0] = i;
            int smallest = i;
            for (int j = 1; j <= other.Length; j++)
            {
                int cost = typed[i - 1] == other[j - 1] ? 0 : 1;
                current[j] = Math.Min(previous[j - 1] + cost, Math.Min(previous[j] + 1, current[j - 1] + 1));
                if (i > 1 && j > 1 && typed[i - 1] == other[j - 2] && typed[i - 2] == other[j - 1])
                    current[j] = Math.Min(current[j], previous2[j - 2] + 1);
                smallest = Math.Min(smallest, current[j]);
            }
            if (smallest > limit)
                return limit + 1;
            (previous2, previous, current) = (previous, current, previous2);
        }
        return previous[^1];
    }
}
