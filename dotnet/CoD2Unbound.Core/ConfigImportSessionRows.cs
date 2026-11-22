using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public sealed partial class ConfigImportSession
{
    private static readonly HashSet<string> SettingMutations = new(StringComparer.Ordinal)
        { "toggle", "togglep", "reset", "setfromdvar", "increase", "decrease" };
    private static readonly HashSet<string> DisplayDvars = new(StringComparer.Ordinal)
        { "r_mode", "r_customwidth", "r_customheight", "r_fullscreen", "r_borderless", "r_displayrefresh", "r_aspectratio", "r_swapinterval" };
    private static readonly HashSet<string> WindowsDeviceDvars = new(StringComparer.Ordinal)
        { "in_mouse", "in_joystick", "m_rawinput", "cl_mouseaccel_windows", "snd_provider", "s_provider" };

    private void RefreshRows()
    {
        var baseline = Baseline;
        priorReview = baseline is null ? [] : ReviewDocument(new(baseline.Version.Config.AsSpan(), Dialect), "target.cfg");
        rows = Review.Candidates.Select(candidate => MakeRow(candidate, baseline)).ToImmutableArray();
        ResetSelection();
    }

    private ConfigImportRow MakeRow(ConfigImportCandidate candidate, ConfigProfileRead? baseline)
    {
        var document = files[candidate.Location.File].Document;
        var command = candidate.Command;
        bool secret = candidate.Reasons.Contains(ConfigImportRisk.Credential);
        string title, group;
        string? previous = null, next = null;

        if (ConfigImportScript.Assignment(command, document, catalog, static _ => false) is { } assignment)
        {
            title = "Set " + assignment.Name;
            group = "Settings";
            if (!secret)
            {
                bool oldSecret = baseline is not null && priorReview.Any(prior =>
                    prior.Reasons.Contains(ConfigImportRisk.Credential) &&
                    ConfigImportScript.Assignment(prior.Command, baseline.Document, catalog, static _ => false)?.Name == assignment.Name);
                if (!oldSecret)
                    previous = baseline?.Document.EffectiveSetting(assignment.Name);
                next = ConfigImportScript.Text(assignment.Value.AsSpan());
            }
        }
        else if (command.Kind == ConfigCommandKind.Bind && ConfigImportScript.Binding(command, document) is { } binding)
        {
            string key = command.Tokens[1].Text;
            title = "Bind " + key;
            group = "Bindings";
            bool oldSecret = priorReview.Any(prior => prior.Command.Kind == ConfigCommandKind.Bind &&
                prior.Command.Tokens.Length > 1 && ConfigDocument.KeyNumber(prior.Command.Tokens[1].Text) == ConfigDocument.KeyNumber(key) &&
                prior.Reasons.Contains(ConfigImportRisk.Credential));
            if (!oldSecret)
                previous = baseline?.Document.EffectiveBind(key);
            if (!secret)
                next = ConfigImportScript.Text(binding.AsSpan());
        }
        else
        {
            title = candidate.Reasons.IsEmpty ? "Config command" : candidate.Reasons[0].Summary();
            group = "Commands";
        }

        return new(candidate, title, group, previous, next, LockedNames(candidate, baseline), PlatformNote(candidate));
    }

    private string? PlatformNote(ConfigImportCandidate candidate)
    {
        bool windowsDestination = DestinationPlatform == ConfigImportPlatform.Windows;
        foreach (var command in AllCommands(candidate))
        {
            var tokens = command.Tokens;
            if (tokens.IsEmpty)
                continue;
            string first = tokens[0].Text.ToLowerInvariant();
            string name = WritesSetting(command, first) && tokens.Length > 1 ? tokens[1].Text.ToLowerInvariant() : first;
            if (DisplayDvars.Contains(name))
                return windowsDestination
                    ? "Resolution and display modes are chosen for this PC. Imported ones are omitted."
                    : "Resolution and display modes are chosen for this Mac: the resolution in this loadout, the mode in Settings › Display. Imported ones are omitted.";
            if (GameSettings.MachineDvarNames.Contains(name) || name == "r_rendererpreference")
                return windowsDestination
                    ? "This value belongs to this PC's launcher settings. Choose it there instead of importing a machine-specific value."
                    : "This value belongs to this Mac's launcher settings. Choose it there instead of importing a machine-specific value.";
            if ((SourcePlatform == ConfigImportPlatform.Windows || windowsDestination) && WindowsDeviceDvars.Contains(name))
                return windowsDestination
                    ? "This input or driver choice is specific to a PC. Keep the local device settings."
                    : "This Windows input or driver choice has no verified Mac translation. Keep the native device settings.";
            if (command.Kind is ConfigCommandKind.Bind or ConfigCommandKind.Unbind && tokens.Length > 1)
            {
                if (ConfigDocument.KeyNumber(tokens[1].Text) is not > 0)
                    return "This key has no supported native mapping. Choose a key in Binds.";
                if ((SourcePlatform == ConfigImportPlatform.Windows || windowsDestination) && ConfigDocument.KeyNumber(tokens[1].Text) == 150)
                    return "Command is a Mac key, rather than a verified Windows mapping. Choose the intended key in Binds.";
            }
        }
        return null;
    }

    private ImmutableArray<string> LockedNames(ConfigImportCandidate candidate, ConfigProfileRead? baseline)
    {
        if (baseline is null || baseline.Metadata.Locks.IsEmpty)
            return [];

        var touched = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<int>();
        bool allBinds = false;
        foreach (var command in AllCommands(candidate))
        {
            var tokens = command.Tokens;
            if (tokens.IsEmpty)
                continue;
            string first = tokens[0].Text.ToLowerInvariant();
            touched.Add(first);
            if (WritesSetting(command, first) && tokens.Length > 1)
                touched.Add(tokens[1].Text.ToLowerInvariant());
            if (command.Kind is ConfigCommandKind.Bind or ConfigCommandKind.Unbind && tokens.Length > 1 &&
                ConfigDocument.KeyNumber(tokens[1].Text) is { } key)
            {
                keys.Add(key);
                if (baseline.Document.EffectiveBindBytes(tokens[1].Text) is { } old)
                    foreach (var oldCommand in new ConfigDocument(old.AsSpan(), Dialect).Commands.Where(oldCommand => !oldCommand.Tokens.IsEmpty))
                        touched.Add(oldCommand.Tokens[0].Text.ToLowerInvariant());
            }
            if (command.Kind == ConfigCommandKind.Unbindall)
                allBinds = true;
        }

        // Protect a locked action when a replacement alters any variable an existing bind reaches.
        foreach (var previous in priorReview)
        {
            var command = previous.Command;
            if (command.Kind != ConfigCommandKind.Bind || command.Tokens.Length < 2 ||
                ConfigDocument.KeyNumber(command.Tokens[1].Text) is not { } key)
                continue;

            if (!keys.Contains(key) && !previous.Dependencies.Any(dependency =>
                dependency.Kind == ConfigImportDependencyKind.Vstr && touched.Contains(dependency.Name.ToLowerInvariant())))
                continue;
            keys.Add(key);
            foreach (var effect in previous.Effects.Where(effect => !effect.Command.Tokens.IsEmpty))
                touched.Add(effect.Command.Tokens[0].Text.ToLowerInvariant());
        }
        touched.UnionWith(touched.Where(value => value.StartsWith('-')).Select(value => '+' + value[1..]).ToArray());

        return baseline.Metadata.Locks.Order(StringComparer.Ordinal).Where(IsLocked).ToImmutableArray();

        bool IsLocked(string value)
        {
            string name = value.ToLowerInvariant();
            if (name.StartsWith("bind:", StringComparison.Ordinal) && ConfigDocument.KeyNumber(name[5..]) is { } key)
                return allBinds || keys.Contains(key);
            bool actionLock = name.StartsWith("action:", StringComparison.Ordinal);
            string action = actionLock ? name[7..] : name;
            return touched.Contains(action) || (allBinds && (actionLock || catalog.Command(action, Dialect) is not null));
        }
    }

    private static bool WritesSetting(ConfigCommand command, string name) =>
        command.Kind == ConfigCommandKind.Setting || SettingMutations.Contains(name);

    private static IEnumerable<ConfigCommand> AllCommands(ConfigImportCandidate candidate) =>
        Enumerable.Repeat(candidate.Command, 1).Concat(candidate.Effects.Select(effect => effect.Command));
}
