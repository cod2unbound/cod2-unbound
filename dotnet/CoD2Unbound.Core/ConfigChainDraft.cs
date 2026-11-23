using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public enum ChainEditorKind { Several, Toggle, Cycle }
public enum ChainStepKind { Name, Say, Team, Action, Console }

public sealed record ChainEditorStep(ChainStepKind Kind, string Text)
{
    internal string Command()
    {
        string value = Text.Trim();
        if (Kind is ChainStepKind.Action or ChainStepKind.Console)
            return value;
        if (value.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 || value.EndsWith('\\'))
            throw new ConfigChainException(ConfigChainFailure.UnsafeQuotes);
        string verb = Kind switch { ChainStepKind.Name => "name", ChainStepKind.Say => "say", _ => "say_team" };
        return value.Any(c => char.IsWhiteSpace(c) || c == ';') ? $"{verb} \"{value}\"" : $"{verb} {value}";
    }

    internal static ChainEditorStep From(string command)
    {
        int split = command.IndexOf(' ');
        if (split < 0)
            return new(ChainStepKind.Action, command);
        string text = command[(split + 1)..].Trim();
        return command[..split].ToLowerInvariant() switch
        {
            "name" => new(ChainStepKind.Name, Unquote(text)),
            "say" => new(ChainStepKind.Say, Unquote(text)),
            "say_team" => new(ChainStepKind.Team, Unquote(text)),
            _ => new(ChainStepKind.Console, command)
        };

        static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1] : value;
    }
}

public sealed class ConfigChainDraft(string id)
{
    public string Id { get; } = id;
    public string Name { get; set; } = "";
    public EngineKey? Key { get; set; }
    public ChainEditorKind Kind { get; set; }
    public string Setting { get; set; } = "";
    public List<ChainEditorStep> Several { get; } = [new(ChainStepKind.Action, "+gostand")];
    public List<ChainEditorStep> Cycle { get; } = [new(ChainStepKind.Say, ""), new(ChainStepKind.Say, "")];
    public List<string> Values { get; } = ["0", "1"];

    public static ConfigChainDraft New(IEnumerable<ConfigChain> chains)
    {
        var ids = chains.Select(chain => chain.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int index = ids.Count + 1;
        while (ids.Contains($"bind_{index}"))
            index++;
        return new($"bind_{index}");
    }

    public static ConfigChainDraft From(ConfigChain chain)
    {
        var draft = new ConfigChainDraft(chain.Id) { Name = chain.Name ?? "", Key = EngineKey.Parse(chain.Key) };
        switch (chain.Behavior)
        {
            case ConfigChainBehavior.Commands commands:
                draft.Several.Clear();
                draft.Several.AddRange(commands.Steps.Select(ChainEditorStep.From));
                break;
            case ConfigChainBehavior.SettingCycle setting:
                draft.Kind = ChainEditorKind.Toggle;
                draft.Setting = setting.Setting;
                draft.Values.Clear();
                draft.Values.AddRange(setting.Values);
                break;
            case ConfigChainBehavior.CommandCycle cycle:
                draft.Kind = ChainEditorKind.Cycle;
                draft.Cycle.Clear();
                draft.Cycle.AddRange(cycle.States.Select(state => state.Length == 1 ? ChainEditorStep.From(state[0]) :
                    new(ChainStepKind.Console, string.Join("; ", state))));
                break;
        }
        return draft;
    }

    public ConfigChain Build()
    {
        if (Key is not { } key)
            throw new InvalidDataException("Choose a key for this bind.");
        var behavior = Kind switch
        {
            ChainEditorKind.Several => new ConfigChainBehavior.Commands(Commands(Several, 1)),
            ChainEditorKind.Toggle when !string.IsNullOrWhiteSpace(Setting) && Values.Count >= 2 && Values.All(value => !string.IsNullOrWhiteSpace(value)) =>
                (ConfigChainBehavior)new ConfigChainBehavior.SettingCycle(Setting, [.. Values.Select(value => value.Trim())]),
            ChainEditorKind.Toggle => throw new InvalidDataException("Choose a setting and enter at least two values."),
            _ => new ConfigChainBehavior.CommandCycle([.. Commands(Cycle, 2).Select(command => ImmutableArray.Create(command))])
        };
        return new(Id, key.Spelling, behavior, string.IsNullOrWhiteSpace(Name) ? null : Name.Trim());

        static ImmutableArray<string> Commands(List<ChainEditorStep> steps, int minimum)
        {
            var lines = steps.Where(step => !string.IsNullOrWhiteSpace(step.Text)).Select(step => step.Command()).ToImmutableArray();
            return lines.Length >= minimum ? lines : throw new InvalidDataException(minimum == 1
                ? "Add what this key should do." : "A cycle needs at least two steps.");
        }
    }
}

public sealed record ConfigChainPreview(ConfigChainEdit Edit, string Description, string Lines, bool Blocked,
    ImmutableArray<string> Risks)
{
    public bool NeedsApproval => !Edit.ConflictingKeys.IsEmpty || !Risks.IsEmpty;

    public static ConfigChainPreview Create(ConfigChainDraft draft, ConfigProfileRead profile, ConfigCatalog catalog,
        ConfigCatalogMode mode)
    {
        var chain = draft.Build();
        var others = ConfigChains.OwnedChains(profile);
        var replacement = others.Any(other => other.Id == chain.Id)
            ? others.Select(other => other.Id == chain.Id ? chain : other).ToImmutableArray() : others.Add(chain);
        var edit = ConfigChains.Prepare(replacement, profile);
        var relevant = ConfigChains.SafetyReview(edit, new ConfigImportCatalogAdapter(catalog)).Candidates
            .Where(candidate => candidate.Location.Span.Start > edit.Document.Serialized.Length ||
                candidate.Command is { Kind: ConfigCommandKind.Bind, Tokens.Length: >= 2 } command &&
                EngineKey.Parse(command.Tokens[1].Text) == draft.Key).ToArray();
        bool blocked = relevant.Any(candidate => candidate.Classification == ConfigImportClassification.Blocked);
        var risks = relevant.Where(candidate => candidate.Classification != ConfigImportClassification.Safe)
            .SelectMany(candidate => candidate.Reasons.Concat(candidate.Effects.SelectMany(effect => effect.Reasons)))
            .Where(risk => risk is not (ConfigImportRisk.Script or ConfigImportRisk.UnknownSetting)).Distinct()
            .Select(risk => risk.Summary()).ToImmutableArray();
        string configLines = string.Join("\n", edit.Document.Commands.Where(command =>
            command is { Kind: ConfigCommandKind.Bind, Tokens.Length: >= 2 } && EngineKey.Parse(command.Tokens[1].Text) == draft.Key)
            .TakeLast(1).Select(command => Encoding.Latin1.GetString(edit.Document.Serialized.AsSpan(command.Span.Start, command.Span.Length))));
        string script = Encoding.Latin1.GetString(edit.Script.AsSpan()).TrimEnd('\r', '\n');
        string lines = script.Length > 0 ? configLines + "\n\n" + script : configLines;
        var actions = ConfigBindingDetails.ForWindows(profile.Document, catalog, mode).Actions;
        string Describe(string command) => actions.FirstOrDefault(action => action.Name == command)?.Title ?? command;

        string description = chain.Behavior switch
        {
            ConfigChainBehavior.Commands commands => "One press: " + string.Join(", then ", commands.Steps.Select(Describe)),
            ConfigChainBehavior.SettingCycle cycle => $"Each press changes {catalog.Setting(cycle.Setting)?.Title ?? cycle.Setting}: " + string.Join(" → ", cycle.Values),
            ConfigChainBehavior.CommandCycle cycle => "Each press: " + string.Join(" → ", cycle.States.Select(state => string.Join(", then ", state.Select(Describe)))),
            _ => ""
        };
        return new(edit, description, lines, blocked, risks);
    }
}
