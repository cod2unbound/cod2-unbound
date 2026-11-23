namespace CoD2Unbound.Core;

public sealed record ConfigCheckupStanding(string? Status, ConfigCheckupTone Tone, string EmptyTitle, string EmptyDetail)
{
    public static ConfigCheckupStanding For(ConfigCheckupReport report, string rulesTitle)
    {
        if (report.RulesetIds.IsEmpty)
            return new(null, ConfigCheckupTone.Neutral, "No rules", "Choose the rules you play under to get advice for them.");

        int Count(ConfigRuleStatus status) => report.Policy.Count(item => item.Status == status);
        int violations = Count(ConfigRuleStatus.Violation);
        int conflicts = Count(ConfigRuleStatus.Conflict);
        int unknown = Count(ConfigRuleStatus.Unknown);
        int warnings = Count(ConfigRuleStatus.Warning);
        int total = report.Policy.Length;
        string tune = TuneTitle(report.TuneFor).ToLowerInvariant();
        if (violations > 0)
            return new(violations == 1 ? "1 setting breaks these rules" : $"{violations} settings break these rules",
                ConfigCheckupTone.Negative, "No suggestions",
                $"Check-up has no change to suggest for {tune} play. Show rules lists the settings that need attention.");
        if (conflicts > 0)
            return new("The chosen rules disagree", ConfigCheckupTone.Caution, "No suggestions",
                $"Check-up has no change to suggest for {tune} play. Show rules lists where the rules disagree.");
        if (unknown > 0 || total == 0)
        {
            string detail = total == 0 ? "These rules don't cover any setting Check-up can assess here."
                : $"Check-up couldn't assess {unknown} of the {total} settings these rules cover. Show rules lists them.";
            return new(unknown == total ? "Not assessed" : $"{unknown} of {total} settings not assessed",
                ConfigCheckupTone.Neutral, "No suggestions", detail);
        }
        if (warnings > 0)
            return new($"Within the limits, {warnings} advisory", ConfigCheckupTone.Caution, "No suggestions",
                $"No change to suggest for {tune} play. Show rules lists the advisory settings.");
        return new($"Follows {rulesTitle}", ConfigCheckupTone.Positive, "Nothing to suggest",
            $"These settings already match every suggestion for {tune} play under these rules.");
    }

    public static string TuneTitle(ConfigTuneFor tune) => tune == ConfigTuneFor.BestLooking ? "Best looking" : tune.ToString();
}

public static class ConfigCheckupPresentation
{
    public static ConfigCheckupGauge Aim(ConfigProfileRead profile, ConfigCatalog catalog, ConfigCatalogMode mode)
    {
        double dpi = ConfigMouseDpi.For(profile);
        string? fallback = catalog.Setting("sensitivity") is { } sensitivity ? ConfigSettingSpec.Create(sensitivity, mode)?.DefaultText : null;
        double? distance = ConfigDiscover.LoadoutTurnDistance(profile.Document, dpi, fallback);
        const string figures = "Mouse polling is not measured.";
        return distance is { } cm
            ? new(ConfigCheckupArea.Aim, $"{cm:F1} cm", "From settings", ConfigCheckupTone.Information,
                $"How far you move the mouse for a full turn, at {ConfigValues.Format(dpi)} DPI.", figures)
            : new(ConfigCheckupArea.Aim, "—", "Not measured", ConfigCheckupTone.Neutral,
                "Enter your mouse's DPI in the loadout's Aim & input settings to see how far you move it for a full turn.", figures);
    }

    public static ConfigCheckupGauge Visibility(ConfigCheckupReport report, ConfigCatalog catalog, string rulesTitle)
    {
        var policies = report.Policy.Where(policy => catalog.Setting(policy.Setting)?.Group == "vis").ToArray();
        var statuses = policies.Select(item => item.Status).ToHashSet();
        if (statuses.Contains(ConfigRuleStatus.Violation))
            return Gauge("Outside rules", "Needs a change", ConfigCheckupTone.Negative, $"Brightness or texture detail breaks {rulesTitle}.");
        if (statuses.Contains(ConfigRuleStatus.Conflict))
            return Gauge("—", "Rules disagree", ConfigCheckupTone.Caution, "The chosen rules disagree about these settings.");
        if (statuses.Contains(ConfigRuleStatus.Warning))
            return Gauge("Advisory", "Watch", ConfigCheckupTone.Caution, $"{rulesTitle} recommends a different value.");
        if (statuses.Contains(ConfigRuleStatus.Unknown))
            return Gauge("—", "Not assessed", ConfigCheckupTone.Neutral, "Some values are missing, set by the server, or changed by a config script.");
        return policies.Length == 0
            ? Gauge("—", "Not assessed", ConfigCheckupTone.Neutral, "The chosen rules say nothing about brightness or texture detail.")
            : Gauge("Within rules", "Good", ConfigCheckupTone.Positive, $"Brightness and texture detail follow {rulesTitle}.");

        ConfigCheckupGauge Gauge(string value, string status, ConfigCheckupTone tone, string detail) =>
            new(ConfigCheckupArea.Visibility, value, status, tone, detail, policies.Length == 0 ? null :
                string.Join(" · ", policies.Select(item => $"{catalog.Setting(item.Setting)?.Title ?? item.Setting} {item.Value ?? "(not set)"}")));
    }

    public static ConfigCheckupTone RuleTone(ConfigRuleStatus status) => status switch
    {
        ConfigRuleStatus.Ok => ConfigCheckupTone.Positive,
        ConfigRuleStatus.Violation => ConfigCheckupTone.Negative,
        ConfigRuleStatus.Conflict or ConfigRuleStatus.Warning => ConfigCheckupTone.Caution,
        _ => ConfigCheckupTone.Neutral
    };

    public static string RuleStatus(ConfigRuleStatus status) => status switch
    {
        ConfigRuleStatus.Ok => "Within the rules",
        ConfigRuleStatus.Violation => "Breaks the rules",
        ConfigRuleStatus.Conflict => "The rules disagree",
        ConfigRuleStatus.Warning => "Advisory",
        _ => "Not assessed"
    };
}
