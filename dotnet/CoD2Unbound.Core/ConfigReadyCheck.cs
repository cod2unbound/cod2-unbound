using System.Collections.Immutable;
using System.Globalization;

namespace CoD2Unbound.Core;

public enum ConfigReadyStatus { MatchesKnownRules, NeedsChanges, Unknown }
public sealed record ConfigReadyReport(string? Address, string ProfileId, ConfigProfileVersion ExpectedVersion,
    ImmutableArray<byte>? AdvancedChain, ConfigCatalogMode Mode, ImmutableArray<string> RulesetIds, ConfigReadyStatus Status,
    string Summary, ImmutableArray<ConfigRuleEvaluation> Details, ImmutableDictionary<string, string> Facts,
    ImmutableDictionary<string, string> Fixes, ImmutableArray<string> Limitations);

public sealed class ConfigReadyCheck(ConfigCatalog catalog, ConfigRules rules, TimeProvider clock)
{
    private static readonly ImmutableHashSet<string> ObservedKeys = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "protocol", "shortversion", "fs_game", "mod", "_zpam", "g_competitive", "sv_fps", "sv_minrate", "sv_maxrate",
        "sv_ruleset", "sv_maxfps", "g_maxfps", "com_maxfps_limit");

    public ConfigReadyReport Evaluate(GameServer? server, ConfigProfileRead profile, ConfigCatalogMode mode,
        ConfigDocument? advancedChain = null, bool requireFresh = true)
    {
        var facts = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        bool ambiguous = false;
        if (server is not null)
        {
            foreach (var (key, value) in server.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string name = key.ToLowerInvariant();
                ambiguous |= facts.TryGetValue(name, out var previous) && previous != value;
                facts[name] = value;
            }
        }

        string? address = null;
        if (server is not null)
        {
            try { address = LaunchLink.CanonicalAddress(server.Address); }
            catch (FormatException) { }
        }
        var (ids, reason) = Identify(facts);
        var limitations = ImmutableArray.CreateBuilder<string>();
        limitations.Add("Only the cited, listed config rules are checked; full match, league and live compatibility are not certified.");
        limitations.Add("Frame pacing, mouse polling and packet loss: Not measured. Server status ping is a query round trip.");
        bool fresh = server?.ObservedAt is { } time && (!requireFresh || (clock.GetUtcNow() - time).TotalSeconds is >= 0 and <= 60);
        bool known = rules.KnowsSelection(ids, mode);
        bool unknown = !fresh || address is null || ambiguous || reason is not null || !known;
        if (!fresh)
            limitations.Add("No fresh status observation is available. Refresh before reviewing a connection fix.");
        var conflicts = server is { QueryConflicts.IsDefault: false } ? server.QueryConflicts : [];
        if (conflicts.Any(ObservedKeys.Contains))
        {
            unknown = true;
            limitations.Add("Status and info replies advertise conflicting rule facts.");
        }
        if (reason is not null) limitations.Add(reason);
        if (ambiguous) limitations.Add("Conflicting case variants of advertised fields prevent a verified review.");
        if (!ids.IsEmpty && !known) limitations.Add("The advertised rules do not match this client mode.");

        var chain = advancedChain?.Serialized ?? profile.Version.Chain;
        var reviewedProfile = profile with { Version = profile.Version with { Chain = chain } };
        var script = new ConfigScriptReview(reviewedProfile, catalog);
        if (script.Unresolved)
        {
            unknown = true;
            limitations.Add("External configs, dynamic commands or parser diagnostics prevent a complete static review.");
        }
        foreach (string key in new[] { "sv_fps", "sv_minrate", "sv_maxrate" })
        {
            if (facts.TryGetValue(key, out var text) &&
                (ConfigRules.Number(text) is not { } integer || integer < 0 || Math.Truncate(integer) != integer))
            {
                unknown = true;
                limitations.Add($"The advertised {key} value is not a valid nonnegative integer.");
            }
        }
        double? min = Number(facts.GetValueOrDefault("sv_minrate")), max = Number(facts.GetValueOrDefault("sv_maxrate"));
        if (max > 0 && min > max)
        {
            unknown = true;
            limitations.Add("The advertised rate bounds contradict each other.");
        }
        if (facts.ContainsKey("sv_maxfps") || facts.ContainsKey("g_maxfps"))
        {
            unknown = true;
            limitations.Add("The advertised custom FPS limit has no verified rule mapping.");
        }
        if (facts.TryGetValue("com_maxfps_limit", out string? legacy) && legacy != "0")
        {
            unknown = true;
            limitations.Add("The legacy FPS-limit flag has no verified advertised policy mapping.");
        }

        var details = ImmutableArray.CreateBuilder<ConfigRuleEvaluation>();
        var fixes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var matchedRules = ids.SelectMany(id => rules.ResolvedRules(id)).ToLookup(rule => rule.Setting.ToLowerInvariant(), StringComparer.Ordinal);
        unknown |= conflicts.Any(matchedRules.Contains);
        foreach (var matched in matchedRules.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            string name = matched.Key;
            if (matched.Any(rule => rule.Constraint.Kind == ConfigConstraintKind.DisabledCommand))
            {
                if (script.Uses(name))
                    details.Add(new(name, null, ConfigRuleStatus.Violation,
                        $"This server disables {name}. Binds that pause with wait will not work here; review the bind before joining.",
                        false, [.. matched.SelectMany(rule => rule.Sources)]));
                continue;
            }
            string? value = profile.Document.EffectiveSetting(name);
            var result = rules.Evaluate(name, value, ids, mode);
            if (script.ChangedSettings.Contains(name))
            {
                unknown = true;
                details.Add(result with { Status = ConfigRuleStatus.Unknown,
                    Reason = "A config or bind command can change this setting; its runtime value is not assessed." });
                continue;
            }
            if (facts.TryGetValue(name, out var advertised) && matched.Any(rule => rule.Constraint.Kind != ConfigConstraintKind.Ownership) &&
                rules.Evaluate(name, advertised, ids, mode).Status != ConfigRuleStatus.Ok)
            {
                unknown = true;
                limitations.Add($"The advertised {name} value contradicts or cannot verify the cited rule.");
            }
            string explanation = result.Reason;
            if (name == "com_maxfps" && Number(value) == 333 && matched.Any(rule => (rule.Constraint.Maximum ?? 1000) <= 250))
                explanation = "333 fps is unavailable here. " + explanation;
            if (result.ServerOwned)
                explanation += " The server controls this value; no temporary override is offered.";
            details.Add(result with { Reason = explanation });
            unknown |= result.Status == ConfigRuleStatus.Unknown && !result.ServerOwned;
            if (result.Status == ConfigRuleStatus.Violation && !result.ServerOwned &&
                !profile.Metadata.Locks.Contains(name, StringComparer.OrdinalIgnoreCase) && ConfigConnectionLaunch.AllowedSettings.Contains(name) &&
                catalog.Setting(name) is { } setting && setting.IsEditable(mode) &&
                setting.Variant(mode) is { ServerControlled: false } variant &&
                Replacement(value, matched) is { } proposed && ValidNumeric(proposed, variant, requireBooleanBounds: true) &&
                rules.Evaluate(name, proposed, ids, mode).Status == ConfigRuleStatus.Ok)
                fixes[name] = proposed;
        }

        if (profile.Document.EffectiveSetting("rate") is { } rate && Number(rate) is { } current &&
            ((min > 0 && current < min) || (max > 0 && current > max)))
            details.Add(new("rate", rate, ConfigRuleStatus.Warning,
                "Your saved rate is outside the server's advertised bounds. These bounds are claims; their runtime effect is not measured.",
                true, []));

        var status = unknown ? ConfigReadyStatus.Unknown
            : details.Any(item => item.Status is ConfigRuleStatus.Violation or ConfigRuleStatus.Warning or ConfigRuleStatus.Conflict)
                ? ConfigReadyStatus.NeedsChanges : ConfigReadyStatus.MatchesKnownRules;
        string summary = status switch
        {
            ConfigReadyStatus.Unknown => "We cannot verify this server's rules against this loadout.",
            ConfigReadyStatus.NeedsChanges => "Review the listed differences before joining.",
            _ => "The directly saved values match the listed, cited rules."
        };
        return new(address, profile.Profile.Id, profile.Version, chain, mode, ids, status, summary, details.ToImmutable(),
            facts.Where(pair => ObservedKeys.Contains(pair.Key) || matchedRules.Contains(pair.Key)).ToImmutableDictionary(StringComparer.Ordinal),
            unknown ? ImmutableDictionary<string, string>.Empty : fixes.ToImmutable(), limitations.ToImmutable());
    }

    private (ImmutableArray<string> Ids, string? Reason) Identify(ImmutableDictionary<string, string>.Builder fields)
    {
        string? protocol = fields.GetValueOrDefault("protocol");
        if (protocol is not ("118" or "120"))
            return ([], "No supported protocol was advertised.");
        bool cod2x = protocol == "120" || fields.GetValueOrDefault("shortversion")?.StartsWith("1.4.", StringComparison.Ordinal) == true;
        string? mod = fields.GetValueOrDefault("fs_game")?.ToLowerInvariant();
        string? version = fields.GetValueOrDefault("_zpam");
        if (mod is null && fields.GetValueOrDefault("mod") != "0" && version is null)
            return ([], "The server didn’t say which mod it runs, or that it runs Original 1.3.");
        string? competitive = fields.GetValueOrDefault("g_competitive");
        if (competitive is not (null or "0" or "1"))
            return ([], "The competitive flag is unknown.");
        if (fields.ContainsKey("zpam_version") || fields.ContainsKey("_pam"))
            return ([], "An unverified mod identity key was advertised.");
        string? folderVersion = mod switch { "zpam336" => "3.36", "zpam408" => "4.08", _ => null };
        if (version is not null && folderVersion is not null && version != folderVersion)
            return ([], "The zPAM identities contradict each other.");

        string basis;
        if ((version ?? folderVersion) is { } zpam)
        {
            if (mod is not (null or "" or "zpam") && folderVersion is null)
                return ([], "The zPAM version does not identify this mod folder.");
            switch (zpam)
            {
                case "3.36":
                    if (cod2x || competitive == "1")
                        return ([], "The zPAM 3.36 rules are checked only for Original 1.3 players.");
                    basis = "zpam-3.36";
                    break;
                case "4.08":
                    if (!cod2x || competitive == "0")
                        return ([], "zPAM 4.08 requires CoD2x competitive rules, including pub.");
                    basis = "zpam-4.08";
                    break;
                default: return ([], "This zPAM version has no cited policy.");
            }
        }
        else
        {
            if (mod is not (null or ""))
                return ([], "This mod has no verified policy mapping.");
            if (cod2x && competitive is null)
                return ([], "CoD2x status does not advertise whether competitive rules apply.");
            if (!cod2x && competitive == "1")
                return ([], "The server says it runs Original 1.3 but also turns on CoD2x competitive rules.");
            basis = !cod2x ? "stock-1.3" : competitive == "1" ? "cod2x-competitive" : "public-cod2x";
        }
        if (!fields.TryGetValue("sv_ruleset", out string? extra) || extra == basis)
            return ([basis], null);
        return rules.RuleSet(extra) is { } selected && selected.Mode == rules.RuleSet(basis)?.Mode
            ? ([basis, extra], null) : ([], "The advertised extra ruleset has no compatible cited policy.");
    }

    private static double? Number(string? text) => text is null ? null : ConfigRules.Number(text);

    private static string? Replacement(string? value, IEnumerable<ConfigSettingRule> matched)
    {
        foreach (var rule in matched.Where(item => !item.Advisory))
        {
            var constraint = rule.Constraint;
            if (constraint.Kind == ConfigConstraintKind.Exact)
                return constraint.Value;
            if (constraint.Kind != ConfigConstraintKind.Range)
                continue;
            if (constraint.ResetValue is { } reset)
                return reset;
            if (Number(value) is { } n && constraint.Minimum is { } min && constraint.Maximum is { } max)
            {
                double bounded = Math.Clamp(n, min, max);
                string text = (constraint.Integer == true ? Math.Floor(bounded) : bounded).ToString("G", CultureInfo.InvariantCulture);
                return text.Contains('.') || text.Contains('E') ? text.ToLowerInvariant() : text + ".0";
            }
        }
        return null;
    }

    internal static bool ValidNumeric(string value, ConfigSettingVariant variant, bool requireBooleanBounds = false)
    {
        if (Number(value) is not { } number)
            return false;
        if (variant.Type == "bool")
        {
            if (number is not (0 or 1))
                return false;
            if (!requireBooleanBounds)
                return true;
        }
        else if (variant.Type is not ("int" or "float") || (variant.Type == "int" && Math.Truncate(number) != number))
            return false;
        return ConfigChecker.Literal(variant.Minimum) is { } min && number >= min &&
            ConfigChecker.Literal(variant.Maximum) is { } max && number <= max;
    }
}

public sealed class ConfigServerConnection
{
    private ConfigReadyReport? review;
    public string? Address { get; private set; }
    public string? ProfileId { get; private set; }
    public ImmutableDictionary<string, string> Overrides { get; private set; } = ImmutableDictionary<string, string>.Empty;

    public void Select(string? address)
    {
        string? normalized = address is null ? null : LaunchLink.CanonicalAddress(address);
        if (normalized != Address)
        {
            Address = normalized;
            Clear();
        }
    }

    public void Clear()
    {
        ProfileId = null;
        Overrides = ImmutableDictionary<string, string>.Empty;
        review = null;
    }

    public void UseProfile(string id)
    {
        ConfigStudioLaunch.ValidateProfileId(id);
        if (Address is null)
            throw Stale();
        Clear();
        ProfileId = id;
    }

    public void Fix(ConfigReadyReport report)
    {
        if (report.Status == ConfigReadyStatus.Unknown || report.Fixes.IsEmpty)
            throw new InvalidOperationException("There is no verified temporary fix for this server and loadout.");
        if (Address is null || Address != report.Address || (ProfileId is not null && ProfileId != report.ProfileId))
            throw Stale();
        review = report;
        Overrides = report.Fixes;
    }

    public ConfigConnectionLaunch? Launch(LaunchLink link, ConfigProfileRead profile, ConfigReadyReport report)
    {
        if (LaunchLink.CanonicalAddress(link.Address) != Address || Overrides.IsEmpty)
            return null;
        if (review is null || review.ProfileId != profile.Profile.Id || !review.ExpectedVersion.Matches(profile.Version) ||
            report.ProfileId != profile.Profile.Id || report.Address != Address || !report.ExpectedVersion.Matches(profile.Version) ||
            report.Mode != review.Mode || !report.RulesetIds.SequenceEqual(review.RulesetIds) ||
            !Same(report.Facts, review.Facts) || !Same(report.Fixes, Overrides) ||
            report.AdvancedChain.HasValue != review.AdvancedChain.HasValue ||
            (report.AdvancedChain is { } chain && !chain.AsSpan().SequenceEqual(review.AdvancedChain!.Value.AsSpan())) ||
            report.Status == ConfigReadyStatus.Unknown)
            throw Stale();
        return new(Address!, Overrides);
    }

    private static bool Same(ImmutableDictionary<string, string> left, ImmutableDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static InvalidOperationException Stale() => new("The server rules or loadout changed. Review this connection again.");
}
