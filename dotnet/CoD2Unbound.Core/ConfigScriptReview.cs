namespace CoD2Unbound.Core;

/// <summary>Bound the review to identifiable commands and flag settings a bind can change.</summary>
internal sealed class ConfigScriptReview
{
    private static readonly HashSet<string> Unmodeled = new(StringComparer.Ordinal)
    {
        "setrecommended", "sizeup", "sizedown", "match", "cinematic", "map", "devmap", "map_rotate", "maprotate",
        "disconnect", "vid_restart", "snd_restart", "cg_shellshock_load", "r_loadsun", "cg_hudradarcalibrate", "addip", "removeip"
    };
    private static readonly Dictionary<string, string[]> KnownTargets = new(StringComparer.Ordinal)
    {
        ["sizeup"] = ["cg_viewsize"], ["sizedown"] = ["cg_viewsize"], ["match"] = ["match_login"],
        ["cinematic"] = ["nextmap", "com_introplayed"], ["map"] = ["sv_cheats"], ["devmap"] = ["sv_cheats"],
        ["map_rotate"] = ["sv_maprotationcurrent", "g_gametype"], ["disconnect"] = ["fs_game"],
        ["addip"] = ["g_banips"], ["removeip"] = ["g_banips"]
    };
    private readonly HashSet<string> commands = new(StringComparer.Ordinal);
    public HashSet<string> ChangedSettings { get; } = new(StringComparer.Ordinal);
    public bool Unresolved { get; private set; }

    public ConfigScriptReview(ConfigProfileRead profile, ConfigCatalog catalog)
    {
        var document = profile.Document;
        Review(document, catalog, topLevel: true);
        ConfigChainEdit? owned = null;
        if (profile.Metadata.ChainDefinitions is not null)
        {
            try { owned = ConfigChains.Prepare(ConfigChains.OwnedChains(profile), profile); }
            catch (Exception error) when (error is ConfigChainException or ConfigEditException or LaunchPlanException)
            {
                Unresolved = true;
            }
        }
        else if (profile.Version.Chain is { } bytes)
        {
            var chain = new ConfigDocument(bytes.AsSpan(), document.Dialect);
            Unresolved |= !chain.Diagnostics.IsEmpty || chain.Commands.Any(command => !command.Tokens.IsEmpty);
        }
        var keys = document.Commands.Where(command => command.Kind == ConfigCommandKind.Bind && command.Tokens.Length > 1)
            .Select(command => ConfigDocument.KeyNumber(command.Tokens[1].Text)).OfType<int>().Distinct();
        foreach (int key in keys)
        {
            string spelling = ConfigDocument.KeySpelling(key);
            if (document.EffectiveBindBytes(spelling) is not { } binding)
                continue;
            var chain = owned?.Chains.FirstOrDefault(item => ConfigDocument.KeyNumber(item.Key) == key);
            if (chain is null || owned?.Document.EffectiveBindBytes(spelling) is not { } trigger ||
                !binding.AsSpan().SequenceEqual(trigger.AsSpan()))
            {
                Review(new(binding.AsSpan(), document.Dialect), catalog, topLevel: false);
                continue;
            }
            if (chain.Behavior is ConfigChainBehavior.SettingCycle cycle)
                ChangedSettings.Add(cycle.Setting.ToLowerInvariant());
            else
                foreach (string text in ConfigChains.Texts(chain.Behavior))
                    Review(new(ConfigDocument.Encode(text), document.Dialect), catalog, topLevel: false, managed: true);
        }
    }

    public bool Uses(string command) => commands.Contains(command.ToLowerInvariant());

    private void Review(ConfigDocument document, ConfigCatalog catalog, bool topLevel, bool managed = false)
    {
        Unresolved |= !document.Diagnostics.IsEmpty || document.UnresolvedCommands.Any(command =>
            !managed || command.Tokens[0].Text.ToLowerInvariant() is not ("toggle" or "togglep" or "reset"));
        foreach (var command in document.Commands)
        {
            var tokens = command.Tokens;
            if (tokens.IsEmpty)
                continue;
            string name = tokens[0].Text.ToLowerInvariant();
            commands.Add(name);
            if (command.Kind == ConfigCommandKind.Bind)
                Unresolved |= !topLevel;
            else if (command.Kind == ConfigCommandKind.Setting)
            {
                if (!topLevel && tokens.Length >= 2)
                    ChangedSettings.Add(tokens[1].Text.ToLowerInvariant());
            }
            else if (name is "toggle" or "togglep" or "reset" or "setfromdvar" or "increase" or "decrease" or
                "dvar_bool" or "dvar_int" or "dvar_float")
            {
                if (tokens.Length < 2)
                    Unresolved = true;
                else
                    ChangedSettings.Add(tokens[1].Text.ToLowerInvariant());
            }
            else if (catalog.Setting(name) is not null)
            {
                if (!topLevel && tokens.Length >= 2)
                    ChangedSettings.Add(name);
            }
            else if (Unmodeled.Contains(name) || (name == "cg_shellshock" && tokens.Length > 2) || catalog.Command(name) is null)
            {
                Unresolved = true;
                if (KnownTargets.TryGetValue(name, out var changed))
                    ChangedSettings.UnionWith(changed);
            }
        }
    }
}
