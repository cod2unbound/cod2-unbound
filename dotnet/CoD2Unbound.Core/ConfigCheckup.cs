using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public enum ConfigTuneFor { Competitive, Balanced, BestLooking }
public enum ConfigCheckupArea { Movement, Aim, Connection, Visibility }
public enum ConfigOptimizerPredicate { OutsideCompetitiveBand, Different, GreaterThan }
public enum ConfigEvidenceKind { Configuration, Policy, Inference, Measurement }

public sealed record ConfigCheckupMeasurements(double? FrameTimeP99MS = null, double? MousePollingHz = null,
    double? PingMS = null, double? PacketLossPercent = null, string? Source = null);
public sealed record ConfigCheckupDial(ConfigCheckupArea Area, double? Value, string? Unit, string? Source)
{
    public string StatusText => Value is null ? "Not measured" : "Measured";
}
public sealed record ConfigCheckupChange(string Setting, string? OldValue, string NewValue);
public sealed record ConfigCheckupEvidence(ConfigEvidenceKind Kind, string Detail);
public sealed record ConfigCheckupSuggestion(string Id, ConfigCheckupArea Area, string Title, string Why, string Notice,
    ImmutableArray<ConfigCheckupChange> Changes, ImmutableArray<ConfigCheckupEvidence> Evidence, ImmutableArray<ConfigRuleSource> Sources);
public sealed record ConfigOptimizerChange(string Setting, ImmutableDictionary<string, string> Suggest);
public sealed record ConfigOptimizerRule(string Id, ConfigCheckupArea Area, ConfigOptimizerPredicate When, string Setting,
    ImmutableDictionary<string, string> Suggest, ImmutableArray<ConfigOptimizerChange>? AdditionalChanges,
    string Title, string Why, string Notice, string Evidence, ImmutableArray<ConfigRuleSource> Sources, ImmutableArray<ConfigTuneFor> TuneFor);

public sealed class ConfigOptimizer
{
    private ConfigOptimizer(ConfigOptimizerData data)
    {
        if (data.SchemaVersion != 1 || string.IsNullOrEmpty(data.Version) || data.Rules.IsDefaultOrEmpty ||
            data.Rules.Any(rule => rule is null || !Valid(rule)) || data.Rules.Select(rule => rule.Id).Distinct().Count() != data.Rules.Length)
            throw new JsonException("The optimizer resource is invalid.");
        SchemaVersion = data.SchemaVersion;
        Version = data.Version;
        Rules = data.Rules;
    }

    public int SchemaVersion { get; }
    public string Version { get; }
    public ImmutableArray<ConfigOptimizerRule> Rules { get; }

    public static ConfigOptimizer LoadBundled()
    {
        using var stream = typeof(ConfigOptimizer).Assembly.GetManifestResourceStream("ConfigOptimizer.json")
            ?? throw new InvalidDataException("The bundled optimizer resource is missing.");
        return new(JsonSerializer.Deserialize(stream, OptimizerJson.Default.ConfigOptimizerData)
            ?? throw new JsonException("Expected an optimizer resource."));
    }

    private static bool Valid(ConfigOptimizerRule rule)
    {
        if (new[] { rule.Id, rule.Title, rule.Why, rule.Notice, rule.Evidence, rule.Setting }.Any(string.IsNullOrEmpty) ||
            !Enum.IsDefined(rule.Area) || !Enum.IsDefined(rule.When) || rule.TuneFor.IsDefaultOrEmpty ||
            rule.TuneFor.Any(tune => !Enum.IsDefined(tune)) || !ValidValues(rule.Suggest) ||
            rule.Sources.IsDefaultOrEmpty || rule.Sources.Any(source => source is null ||
                !Uri.TryCreate(source.SourceURL, UriKind.Absolute, out _) ||
                string.IsNullOrEmpty(source.SourceDate) || string.IsNullOrEmpty(source.QuoteReference) || string.IsNullOrEmpty(source.Quote)))
            return false;
        var extra = rule.AdditionalChanges ?? [];
        return extra.All(change => change is not null && !string.IsNullOrEmpty(change.Setting) && ValidValues(change.Suggest)) &&
            extra.Select(change => change.Setting).Append(rule.Setting).Distinct(StringComparer.OrdinalIgnoreCase).Count() == extra.Length + 1;
    }

    private static bool ValidValues(ImmutableDictionary<string, string>? values) =>
        values is { Count: > 0 } && values.All(pair => pair.Key is "stock" or "cod2x" && pair.Value is not null && ConfigRules.Number(pair.Value) is not null);
}

public sealed record ConfigCheckupReport(string ProfileId, ConfigProfileVersion ExpectedVersion, ImmutableArray<string> RulesetIds,
    ConfigTuneFor TuneFor, ConfigCatalogMode Mode, ImmutableArray<ConfigCheckupSuggestion> Suggestions,
    ImmutableArray<ConfigCheckupDial> Dials, ImmutableArray<ConfigRuleEvaluation> Policy, ImmutableArray<string> Limitations,
    bool UnresolvedScripts);
public sealed record ConfigCheckupReceipt(string ProfileId, ConfigHistoryEntry HistoryEntry, ConfigProfileVersion AfterVersion,
    ImmutableArray<ConfigCheckupChange> Changes);

public sealed class ConfigCheckup(ConfigCatalog catalog, ConfigRules rules, ConfigOptimizer optimizer)
{
    public ConfigCheckupReport Evaluate(ConfigProfileRead profile, ImmutableArray<string> rulesetIds,
        ConfigTuneFor tuneFor, ConfigCatalogMode? targetMode = null, ConfigCheckupMeasurements? measurements = null,
        IReadOnlySet<string>? dismissedSuggestionIds = null)
    {
        measurements ??= new();
        var mode = targetMode ?? Mode(profile.Document.Dialect);
        bool known = rules.KnowsSelection(rulesetIds, mode);
        var script = new ConfigScriptReview(profile, catalog);
        var limitations = ImmutableArray.CreateBuilder<string>();
        limitations.Add("Advice uses direct config values and cited code. Performance and compatibility beyond the listed rules are not assessed.");
        if (!known) limitations.Add("Unknown rules or a client-mode mismatch: compatibility is not assessed.");
        if (script.Unresolved) limitations.Add("Scripts, external configs or parser diagnostics prevent a complete static review. Resolve them before applying advice.");
        if (script.ChangedSettings.Count > 0) limitations.Add("Config commands or binds can change the listed settings; their runtime values are not assessed.");
        if (measurements.PacketLossPercent is null) limitations.Add("Packet loss: Not measured.");
        var selected = rulesetIds.SelectMany(id => rules.ResolvedRules(id)).ToLookup(rule => rule.Setting.ToLowerInvariant(), StringComparer.Ordinal);
        string modeKey = ConfigCatalog.ModeKey(mode);
        var suggestions = ImmutableArray.CreateBuilder<ConfigCheckupSuggestion>();
        if (known && !script.Unresolved)
        {
            foreach (var rule in optimizer.Rules)
            {
                string? current = profile.Document.EffectiveSetting(rule.Setting);
                if (!rule.TuneFor.Contains(tuneFor) || dismissedSuggestionIds?.Contains(rule.Id) == true ||
                    !rule.Suggest.TryGetValue(modeKey, out var proposed) || !CanChange(rule.Setting, proposed) ||
                    current is null || ConfigRules.Number(current) is not { } number || ConfigRules.ValuesEqual(current, proposed))
                    continue;
                if (rule.When == ConfigOptimizerPredicate.GreaterThan && number <= ConfigRules.Number(proposed))
                    continue;
                if (rule.When == ConfigOptimizerPredicate.OutsideCompetitiveBand)
                {
                    var band = selected[rule.Setting.ToLowerInvariant()].FirstOrDefault(item =>
                        item.Constraint.Kind == ConfigConstraintKind.Range && item.Constraint.Maximum == 250)?.Constraint;
                    if (band?.Minimum is not { } min || (number >= min && number <= 250 && Math.Truncate(number) == number))
                        continue;
                }

                var changes = ImmutableArray.CreateBuilder<ConfigCheckupChange>();
                changes.Add(new(rule.Setting, current, proposed));
                bool eligible = true;
                foreach (var extra in rule.AdditionalChanges ?? [])
                {
                    string? old = profile.Document.EffectiveSetting(extra.Setting);
                    if (!extra.Suggest.TryGetValue(modeKey, out var value) || script.ChangedSettings.Contains(extra.Setting.ToLowerInvariant()))
                    {
                        eligible = false;
                        break;
                    }
                    if (old is not null && ConfigRules.ValuesEqual(old, value))
                        continue;
                    if (!CanChange(extra.Setting, value))
                    {
                        eligible = false;
                        break;
                    }
                    changes.Add(new(extra.Setting, old, value));
                }
                if (eligible)
                    suggestions.Add(new(rule.Id, rule.Area, rule.Title, rule.Why, rule.Notice, changes.ToImmutable(),
                        [.. changes.Select(change => new ConfigCheckupEvidence(ConfigEvidenceKind.Configuration,
                            $"{change.Setting} = {change.OldValue ?? "not present"}")),
                            new(ConfigEvidenceKind.Policy, string.Join(", ", rulesetIds)), new(ConfigEvidenceKind.Inference, rule.Evidence)],
                        rule.Sources));
            }
        }
        var policy = ImmutableArray.CreateBuilder<ConfigRuleEvaluation>();
        foreach (var group in selected.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            string name = group.Key;
            bool disabled = group.Any(item => item.Constraint.Kind == ConfigConstraintKind.DisabledCommand);
            string? value = disabled ? (script.Uses(name) ? "used" : null) : profile.Document.EffectiveSetting(name);
            var evaluation = rules.Evaluate(name, value, rulesetIds, mode);
            if (!disabled && script.ChangedSettings.Contains(name))
                evaluation = evaluation with { Status = ConfigRuleStatus.Unknown,
                    Reason = "A config or bind command can change this setting; its runtime value is not assessed." };
            else if (script.Unresolved && evaluation.Status is not (ConfigRuleStatus.Violation or ConfigRuleStatus.Conflict))
                evaluation = evaluation with { Status = ConfigRuleStatus.Unknown,
                    Reason = "Only direct assignments are visible; unresolved scripts or parser diagnostics prevent a policy judgment." };
            policy.Add(evaluation);
        }

        return new(profile.Profile.Id, profile.Version, rulesetIds, tuneFor, mode, suggestions.ToImmutable(), Dials(measurements),
            policy.ToImmutable(), limitations.ToImmutable(), script.Unresolved);

        bool CanChange(string name, string proposed) => !profile.Metadata.Locks.Contains(name, StringComparer.OrdinalIgnoreCase) &&
            !script.ChangedSettings.Contains(name.ToLowerInvariant()) && !rules.IsServerOwned(name, rulesetIds) &&
            catalog.Setting(name) is { } setting && setting.IsEditable(mode) &&
            setting.Variant(mode) is { ServerControlled: false } variant && ConfigReadyCheck.ValidNumeric(proposed, variant) &&
            rules.Evaluate(name, proposed, rulesetIds, mode).Status == ConfigRuleStatus.Ok;
    }

    public ConfigCheckupReceipt Apply(ConfigCheckupReport report, IReadOnlySet<string> suggestionIds, ConfigProfileStore store)
    {
        if (suggestionIds.Count == 0)
            throw new InvalidOperationException("Choose at least one suggestion.");
        if (!rules.KnowsSelection(report.RulesetIds, report.Mode))
            throw new InvalidOperationException("The selected rules are unknown.");
        var current = store.Read(report.ProfileId);
        if (!current.Version.Matches(report.ExpectedVersion))
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        var reviewed = Evaluate(current, report.RulesetIds, report.TuneFor, report.Mode);
        var chosen = reviewed.Suggestions.Where(item => suggestionIds.Contains(item.Id)).ToArray();
        if (chosen.Length != suggestionIds.Count || chosen.Any(item => !report.Suggestions.Any(previous => Same(previous, item))))
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        ImmutableArray<ConfigCheckupChange> changes = [.. chosen.SelectMany(item => item.Changes)];
        if (changes.Select(item => item.Setting).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Length)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        var document = current.Document;
        foreach (var change in changes)
            document = document.SetSetting(change.Setting, change.NewValue);
        var entry = store.Update(report.ProfileId, document, current.Version, null, string.Join("; ", chosen.Select(item => item.Title)),
            ConfigHistorySource.Checkup);
        return new(report.ProfileId, entry, entry.After, changes);
    }

    public ConfigHistoryEntry Undo(ConfigCheckupReceipt receipt, ImmutableArray<string> rulesetIds, ConfigProfileStore store,
        ConfigCatalogMode? targetMode = null)
    {
        var current = store.Read(receipt.ProfileId);
        if (!current.Version.Matches(receipt.AfterVersion))
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        var mode = targetMode ?? Mode(store.Dialect);
        if (!rules.KnowsSelection(rulesetIds, mode))
            throw new InvalidOperationException("The selected rules are unknown.");
        foreach (var change in receipt.Changes)
            if (current.Metadata.Locks.Contains(change.Setting, StringComparer.OrdinalIgnoreCase) ||
                rules.IsServerOwned(change.Setting, rulesetIds) || catalog.Setting(change.Setting)?.IsEditable(mode) != true)
                throw new InvalidOperationException($"Unlock {change.Setting} before changing this setting.");
        return store.Restore(receipt.ProfileId, receipt.HistoryEntry.Id, current.Version, ConfigSnapshotRevision.Before);
    }

    private static bool Same(ConfigCheckupSuggestion previous, ConfigCheckupSuggestion current) =>
        previous.Id == current.Id && previous.Area == current.Area && previous.Title == current.Title && previous.Why == current.Why &&
        previous.Notice == current.Notice && previous.Changes.SequenceEqual(current.Changes) &&
        previous.Evidence.SequenceEqual(current.Evidence) && previous.Sources.SequenceEqual(current.Sources);

    private static ConfigCatalogMode Mode(ConfigDialect dialect) =>
        dialect == ConfigDialect.Cod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock;

    private static ImmutableArray<ConfigCheckupDial> Dials(ConfigCheckupMeasurements data)
    {
        string? source = string.IsNullOrWhiteSpace(data.Source) ? null : data.Source;
        return [new(ConfigCheckupArea.Movement, Measured(data.FrameTimeP99MS, true), "ms (p99)", source),
            new(ConfigCheckupArea.Aim, Measured(data.MousePollingHz, true), "Hz", source),
            new(ConfigCheckupArea.Connection, Measured(data.PingMS, false), "ms (ping)", source),
            new(ConfigCheckupArea.Visibility, null, null, null)];

        double? Measured(double? value, bool positive) =>
            source is not null && value is { } measured && double.IsFinite(measured) && (positive ? measured > 0 : measured >= 0)
                ? measured : null;
    }
}

internal sealed record ConfigOptimizerData(int SchemaVersion, string Version, ImmutableArray<ConfigOptimizerRule> Rules);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConfigOptimizerData))]
internal sealed partial class OptimizerJson : JsonSerializerContext;
