using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public enum ConfigRuleStatus { Ok, Warning, Violation, Unknown, Conflict }
public enum ConfigRuleEnforcement { Code, ModScript, Paper }
public enum ConfigConstraintKind { Range, Exact, ForbiddenValues, DisabledCommand, Ownership }
public sealed record ConfigRuleSource(string SourceURL, string SourceDate, string QuoteReference, string Quote,
    ConfigRuleEnforcement EnforcedBy, string? ArchiveTimestamp);
public sealed record ConfigRuleConstraint(ConfigConstraintKind Kind, double? Minimum, double? Maximum, string? Value,
    ImmutableArray<string>? ForbiddenValues, string? ResetValue, bool? Integer);
public sealed record ConfigSettingRule(string Id, string Setting, ConfigRuleConstraint Constraint, bool ServerOwned, bool Advisory,
    string Reason, ImmutableArray<ConfigRuleSource> Sources);
public sealed record ConfigRuleset(string Id, string Title, string Version, ConfigCatalogMode Mode, string? BaseID, string Scope,
    ImmutableArray<ConfigSettingRule> Rules, ImmutableArray<ConfigRuleSource> Sources);
public sealed record ConfigRuleConflict(string Id, string Setting, ImmutableArray<string> RulesetIDs, string Reason,
    ImmutableArray<ConfigRuleSource> Sources);
public sealed record ConfigRuleEvaluation(string Setting, string? Value, ConfigRuleStatus Status, string Reason, bool ServerOwned,
    ImmutableArray<ConfigRuleSource> Sources);

public sealed partial class ConfigRules
{
    private readonly ImmutableDictionary<string, ConfigRuleset> policies;
    private readonly ImmutableDictionary<string, ImmutableArray<ConfigSettingRule>> resolved;
    public int SchemaVersion { get; }
    public string Version { get; }
    public string ResearchedOn { get; }
    public ImmutableArray<ConfigRuleset> Rulesets { get; }
    public ImmutableArray<ConfigRuleConflict> Conflicts { get; }

    private ConfigRules(ConfigRulesResource input)
    {
        if (input.SchemaVersion != 1 || string.IsNullOrEmpty(input.Version) || string.IsNullOrEmpty(input.ResearchedOn) ||
            input.Rulesets.IsDefaultOrEmpty || input.Conflicts.IsDefault ||
            input.Rulesets.Any(item => item is null || string.IsNullOrEmpty(item.Id)) ||
            input.Conflicts.Any(item => item is null || string.IsNullOrEmpty(item.Id)) ||
            !Unique(input.Rulesets.Select(policy => policy.Id)) || !Unique(input.Conflicts.Select(conflict => conflict.Id)))
            throw new JsonException("The ruleset resource has invalid version or identities.");
        SchemaVersion = input.SchemaVersion;
        Version = input.Version;
        ResearchedOn = input.ResearchedOn;
        Rulesets = input.Rulesets;
        Conflicts = input.Conflicts;
        policies = Rulesets.ToImmutableDictionary(item => item.Id, StringComparer.Ordinal);
        var resolutions = ImmutableDictionary.CreateBuilder<string, ImmutableArray<ConfigSettingRule>>(StringComparer.Ordinal);
        foreach (var policy in Rulesets)
        {
            if (!Enum.IsDefined(policy.Mode) || string.IsNullOrEmpty(policy.Title) || string.IsNullOrEmpty(policy.Version) ||
                string.IsNullOrEmpty(policy.Scope) || !ValidSources(policy.Sources) || policy.Rules.IsDefault ||
                policy.Rules.Any(rule => !ValidRule(rule)) || !Unique(policy.Rules.Select(rule => rule.Id)) ||
                !Unique(policy.Rules.Select(rule => rule.Setting.ToLowerInvariant())))
                throw new JsonException($"Invalid ruleset {policy.Id}.");

            var branch = new Stack<ConfigRuleset>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var current = policy; ; )
            {
                if (current.Mode != policy.Mode || !visited.Add(current.Id) || current.Rules.IsDefault ||
                    current.Rules.Any(rule => rule is null || string.IsNullOrEmpty(rule.Setting)))
                    throw new JsonException($"Invalid inheritance for {policy.Id}.");
                branch.Push(current);
                if (current.BaseID is null)
                    break;
                current = RuleSet(current.BaseID) ?? throw new JsonException($"Unknown base of {current.Id}.");
            }
            var rules = new List<ConfigSettingRule>();
            foreach (var item in branch)
            {
                var overridden = item.Rules.Select(rule => rule.Setting).ToHashSet(StringComparer.OrdinalIgnoreCase);
                rules.RemoveAll(rule => overridden.Contains(rule.Setting));
                rules.AddRange(item.Rules);
            }
            resolutions.Add(policy.Id, [.. rules]);
        }
        resolved = resolutions.ToImmutable();

        foreach (var conflict in Conflicts)
            if (string.IsNullOrEmpty(conflict.Setting) || string.IsNullOrEmpty(conflict.Reason) ||
                conflict.RulesetIDs.IsDefault || conflict.RulesetIDs.Length < 2 ||
                conflict.RulesetIDs.Any(id => id is null || !policies.ContainsKey(id)) || !ValidSources(conflict.Sources))
                throw new JsonException($"Invalid rule conflict {conflict.Id}.");
    }

    public static ConfigRules LoadBundled()
    {
        using var stream = typeof(ConfigRules).Assembly.GetManifestResourceStream("ConfigRulesets.json") ??
            throw new InvalidOperationException("Missing ConfigRulesets.json.");
        return new(JsonSerializer.Deserialize(stream, RulesJson.Default.ConfigRulesResource) ?? throw new JsonException());
    }

    public static ConfigRules Read(ReadOnlySpan<byte> bytes) =>
        new(JsonSerializer.Deserialize(bytes, RulesJson.Default.ConfigRulesResource) ?? throw new JsonException());

    public ConfigRuleset? RuleSet(string id) => policies.GetValueOrDefault(id);
    public ImmutableArray<ConfigSettingRule> ResolvedRules(string id) =>
        resolved.TryGetValue(id, out var rules) ? rules : [];
    public bool KnowsSelection(IReadOnlyCollection<string> ids, ConfigCatalogMode mode) =>
        ids.Count != 0 && ids.All(id => RuleSet(id)?.Mode == mode);
    public bool IsServerOwned(string setting, IEnumerable<string> ids) =>
        ids.SelectMany(id => ResolvedRules(id)).Any(rule => SameName(rule.Setting, setting) && rule.ServerOwned);

    public ConfigRuleEvaluation Evaluate(string setting, string? value, IReadOnlyCollection<string> ids, ConfigCatalogMode mode)
    {
        var matched = ids.SelectMany(id => ResolvedRules(id)).Where(rule => SameName(rule.Setting, setting)).ToArray();
        ImmutableArray<ConfigRuleSource> sources = [.. matched.SelectMany(rule => rule.Sources)];
        bool owned = matched.Any(rule => rule.ServerOwned);
        ConfigRuleEvaluation Result(ConfigRuleStatus status, string text) => new(setting, value, status, text, owned, sources);
        if (!KnowsSelection(ids, mode) || matched.Length == 0)
            return Result(ConfigRuleStatus.Unknown, "No verified rule for this setting and target. Compatibility is not assessed.");
        var conflict = Conflicts.FirstOrDefault(item => SameName(item.Setting, setting) && item.RulesetIDs.All(ids.Contains));
        if (conflict is not null)
            return Result(ConfigRuleStatus.Conflict, conflict.Reason);
        if (value is null)
            return Result(ConfigRuleStatus.Unknown, "No direct value is available. Runtime or script state is not assessed.");
        var constraints = matched.Where(rule => rule.Constraint.Kind != ConfigConstraintKind.Ownership).ToArray();
        if (constraints.Length == 0)
            return Result(ConfigRuleStatus.Unknown, "This setting is controlled by the server; its runtime value is not assessed.");
        var violated = constraints.FirstOrDefault(rule => !rule.Advisory && !Accepts(value, rule.Constraint));
        if (violated is not null)
            return Result(ConfigRuleStatus.Violation, violated.Reason);
        var warning = constraints.FirstOrDefault(rule => rule.Advisory);
        return warning is not null ? Result(ConfigRuleStatus.Warning, warning.Reason) :
            Result(ConfigRuleStatus.Ok, "Matches the cited setting rules. " + string.Join(' ', constraints.Select(rule => rule.Reason)));
    }

    public static double? Number(string text)
    {
        string trimmed = text.Trim(' ', '\t', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003',
            '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u202f', '\u205f', '\u3000');
        return PlainDecimal().IsMatch(trimmed) && double.TryParse(trimmed,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value) ? value : null;
    }

    public static bool ValuesEqual(string left, string right)
    {
        if (left == right)
            return true;
        string[] a = Whitespace().Split(left.Trim());
        string[] b = Whitespace().Split(right.Trim());
        return a.Length == b.Length && a.Zip(b).All(pair => Number(pair.First) is double value && value == Number(pair.Second));
    }

    private static bool Accepts(string value, ConfigRuleConstraint constraint) => constraint.Kind switch
    {
        ConfigConstraintKind.Range => Number(value) is double n && n >= constraint.Minimum && n <= constraint.Maximum &&
            (constraint.Integer != true || n == Math.Truncate(n)),
        ConfigConstraintKind.Exact => constraint.Value is { } exact && ValuesEqual(value, exact),
        ConfigConstraintKind.ForbiddenValues => !(constraint.ForbiddenValues ?? []).Any(other => ValuesEqual(value, other)),
        ConfigConstraintKind.Ownership => true,
        _ => false
    };

    private static bool ValidRule(ConfigSettingRule rule)
    {
        if (rule is null || string.IsNullOrEmpty(rule.Id) || string.IsNullOrEmpty(rule.Setting) ||
            string.IsNullOrEmpty(rule.Reason) || !ValidSources(rule.Sources) || rule.Constraint is not { } c)
            return false;
        return c.Kind switch
        {
            ConfigConstraintKind.Range => c.Minimum is double low && c.Maximum is double high &&
                double.IsFinite(low) && double.IsFinite(high) && low <= high &&
                (c.Integer != true || (low == Math.Truncate(low) && high == Math.Truncate(high))) &&
                (c.ResetValue is null || (Number(c.ResetValue) is double reset && reset >= low && reset <= high)),
            ConfigConstraintKind.Exact => !string.IsNullOrEmpty(c.Value),
            ConfigConstraintKind.ForbiddenValues => c.ForbiddenValues is { IsDefaultOrEmpty: false } values && values.All(value => value is not null),
            ConfigConstraintKind.Ownership => rule.ServerOwned,
            ConfigConstraintKind.DisabledCommand => true,
            _ => false
        };
    }

    private static bool ValidSources(ImmutableArray<ConfigRuleSource> sources) => !sources.IsDefaultOrEmpty &&
        sources.All(source => source is not null && Uri.TryCreate(source.SourceURL, UriKind.Absolute, out _) &&
            !string.IsNullOrEmpty(source.SourceDate) && !string.IsNullOrEmpty(source.QuoteReference) &&
            !string.IsNullOrEmpty(source.Quote) && Enum.IsDefined(source.EnforcedBy));
    private static bool Unique(IEnumerable<string> ids) => ids.All(new HashSet<string>(StringComparer.Ordinal).Add);
    private static bool SameName(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\A[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainDecimal();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

internal sealed record ConfigRulesResource(int SchemaVersion, string Version, string ResearchedOn,
    ImmutableArray<ConfigRuleset> Rulesets, ImmutableArray<ConfigRuleConflict> Conflicts);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConfigRulesResource))]
internal sealed partial class RulesJson : JsonSerializerContext;
