using System.Collections.Immutable;
using System.Text;
using static CoD2Unbound.Core.ConfigImportRisk;

namespace CoD2Unbound.Core;

/// <summary>Conservatively reviews possible commands and saved representations without executing text.</summary>
public sealed class ConfigImportSafety(IConfigImportCatalog catalog, int maximumEffects = 4096, int maximumDepth = 32)
{
    private const string Redacted = "[credential-bearing command removed]";

    public ConfigImportReview Review(ConfigImportBundle bundle)
    {
        var graph = new ConfigImportGraph(catalog);
        var files = new Dictionary<string, ConfigImportFile>(StringComparer.Ordinal);
        foreach (var file in bundle.Files)
            files.TryAdd(file.Path, file);
        bool invalidBundle = bundle.Files.Length > 64 ||
            bundle.Files.Sum(file => (long)file.Document.Serialized.Length) > 262_140 ||
            bundle.Files.Any(file => file.Document.Serialized.Length > 65_535);
        graph.Build(bundle.Files);

        var reviewer = new Reviewer(catalog, graph, files, maximumDepth);
        var candidates = ImmutableArray.CreateBuilder<ConfigImportCandidate>();
        foreach (var file in bundle.Files)
        {
            foreach (var command in file.Document.Commands.Where(command => command.Span.Length > 0))
            {
                int budget = maximumEffects;
                var location = new ConfigImportLocation(file.Path, [], command.Span);
                var result = reviewer.Assess(command, file.Document, location, [], ref budget, previewStoredRoot: true);
                if (invalidBundle || graph.Limited)
                    result.Block(Limit);

                candidates.Add(new(location, command, result.Level, result.Importable, [.. result.Reasons],
                    [.. result.Effects], [.. result.Dependencies], Details(result, command, file.Document)));
            }
        }
        return new(bundle, candidates.ToImmutable());
    }

    private static string Details(Assessment result, ConfigCommand command, ConfigDocument document) =>
        result.Reasons.Contains(Credential) ? Redacted : ConfigImportScript.Text(command.Span.Slice(document.Serialized.AsSpan()));

    private sealed class Assessment
    {
        public ConfigImportClassification Level { get; private set; }
        public bool Importable { get; set; } = true;
        public List<ConfigImportRisk> Reasons { get; } = [];
        public List<ConfigImportEffect> Effects { get; } = [];
        public List<ConfigImportDependency> Dependencies { get; } = [];

        public void Block(ConfigImportRisk risk) => Add(risk, ConfigImportClassification.Blocked, false);

        public void Add(ConfigImportRisk risk, ConfigImportClassification level = ConfigImportClassification.NeedsOk, bool importable = true)
        {
            if (!Reasons.Contains(risk))
                Reasons.Add(risk);
            if (level > Level)
                Level = level;
            Importable &= importable;
        }

        public void Merge(Assessment child)
        {
            foreach (var reason in child.Reasons)
                Add(reason, child.Level, child.Importable);
            if (child.Level > Level)
                Level = child.Level;
            Importable &= child.Importable;
            Effects.AddRange(child.Effects);
            Dependencies.AddRange(child.Dependencies);
        }
    }

    private sealed class Reviewer(IConfigImportCatalog catalog, ConfigImportGraph graph,
        Dictionary<string, ConfigImportFile> files, int maximumDepth)
    {
        public Assessment Assess(ConfigCommand command, ConfigDocument document, ConfigImportLocation location,
            ImmutableArray<string> stack, ref int budget, bool detectCycles = true, bool previewStoredRoot = false)
        {
            var result = new Assessment();
            if (budget <= 0 || stack.Length > maximumDepth)
            {
                result.Block(Limit);
                return result;
            }
            budget--;
            var tokens = command.Tokens;
            var dialect = document.Dialect;
            if (document.IgnoredSuffix is not null || command.SplitAtLimit || command.Syntax.Failure is not null ||
                tokens.Any(token => !token.Closed))
                result.Block(Malformed);
            if (tokens.IsEmpty)
            {
                result.Importable = false;
                return result;
            }

            string name = tokens[0].Text.ToLowerInvariant();
            if (ConfigImportScript.Assignment(command, document, catalog, graph.Values.ContainsKey) is { } assignment)
            {
                AssessSetting(assignment.Name, assignment.Value, dialect, result);
                if (name is "sets" or "setu")
                    result.Add(Network);
                if (graph.Referenced.Contains(assignment.Name) || ConfigImportScript.LooksExecutable(assignment.Value, dialect))
                {
                    result.Add(Script);
                    // Nested setters store the text for a later use. Only a root definition previews immediate execution.
                    bool inspectExecution = detectCycles && previewStoredRoot;
                    string key = "variable:" + assignment.Name;
                    Expand(assignment.Value, dialect, location, key, inspectExecution ? [key] : stack, ref budget, result, inspectExecution);
                }
                return result;
            }
            if (command.Kind == ConfigCommandKind.Setting)
            {
                result.Block(Malformed);
                return result;
            }

            switch (name)
            {
                case "bind":
                    AssessBinding(command, document, location, stack, ref budget, result, detectCycles && previewStoredRoot);
                    break;
                case "unbind":
                    if (tokens.Length != 2 || ConfigDocument.KeyNumber(tokens[1].Text, folding: false) is null)
                        result.Add(Malformed, importable: false);
                    break;
                case "unbindall":
                    result.Add(UnbindAll);
                    break;
                case "exec":
                    AssessExec(tokens, dialect, stack, ref budget, result, detectCycles);
                    break;
                case "vstr":
                    AssessVariable(tokens, dialect, location, stack, ref budget, result, detectCycles);
                    break;
                case "toggle":
                case "togglep":
                case "reset":
                case "increase":
                case "decrease":
                case "setfromdvar":
                    AssessMutation(name, tokens, dialect, location, stack, ref budget, result);
                    break;
                case "wait":
                    if (tokens.Length == 2 && tokens[1].Text.Trim().StartsWith('-'))
                        result.Block(NegativeWait);
                    else
                        result.Add(SideEffect);
                    break;
                case "connect":
                case "reconnect":
                case "disconnect":
                    result.Add(Connect);
                    break;
                case "quit":
                    result.Add(Quit);
                    break;
                case "rcon":
                    if (tokens.Skip(1).Select(token => token.Text.ToLowerInvariant()).Any(text =>
                        ConfigFetchRequest.CredentialName(text) || text.Split(' ').Contains("login")))
                        result.Block(Credential);
                    else
                        result.Add(RemoteConsole);
                    break;
                case "openscriptmenu":
                case "match":
                case "mr":
                    if (tokens.Skip(1).Any(token => token.Text.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                        token.Text.Contains("password", StringComparison.OrdinalIgnoreCase)))
                        result.Block(Credential);
                    else
                        result.Add(Chat);
                    break;
                case "name":
                    result.Add(Identity);
                    break;
                case "say":
                case "say_team":
                case "tell":
                case "cmd":
                case "callvote":
                case "vote":
                case "voicechat":
                case "voiceteamchat":
                case "mp_quickmessage":
                    result.Add(Chat);
                    break;
                case "writeconfig":
                case "writedefaults":
                case "condump":
                case "record":
                case "screenshot":
                case "screenshotjpeg":
                    result.Add(FileWrite);
                    if (tokens.Skip(1).Any(token => PathValue(token.Text)))
                        result.Block(FilesystemPath);
                    break;
                default:
                    AssessOther(name, dialect, result);
                    break;
            }
            return result;
        }

        private void AssessBinding(ConfigCommand command, ConfigDocument document, ConfigImportLocation location,
            ImmutableArray<string> stack, ref int budget, Assessment result, bool detectCycles)
        {
            var tokens = command.Tokens;
            if (tokens.Length < 3 || ConfigDocument.KeyNumber(tokens[1].Text) is not { } key ||
                ConfigImportScript.Binding(command, document) is not { } binding)
            {
                result.Block(Malformed);
                return;
            }
            var dialect = document.Dialect;
            if (ConfigImportScript.BindingWasTruncated(command))
                result.Block(Limit);
            if (!PreservesSavedValue(binding, "bind", ConfigDocument.KeySpelling(key), escapeQuotes: true, dialect))
                result.Block(Malformed);
            if (!binding.IsEmpty && binding[0] == '+')
            {
                if (binding.Contains((byte)';'))
                    result.Add(KeyRelease);

                // The engine appends key and time to the saved action on both press and release.
                byte[] eventBytes = [.. binding, .. Encoding.UTF8.GetBytes(FormattableString.Invariant($" {key} 1\n"))];
                var parsed = new ConfigDocument(eventBytes.AsSpan(0, Math.Min(eventBytes.Length, 1023)), dialect);
                foreach (var generated in parsed.Commands.Where(generated => !generated.Tokens.IsEmpty &&
                    generated.Tokens[0].Span.Start >= binding.Length))
                {
                    result.Add(UnknownForwardedCommand, importable: false);
                    result.Effects.Add(new(new(location.File, location.Context.Add("key event (illustrative time)"), generated.Span),
                        generated, ConfigImportClassification.NeedsOk, [UnknownForwardedCommand],
                        ConfigImportScript.Text(generated.Span.Slice(eventBytes))));
                }
            }
            Expand(binding, dialect, location, "bind:" + tokens[1].Text, stack, ref budget, result, detectCycles);
        }

        private void AssessExec(ImmutableArray<ConfigToken> tokens, ConfigDialect dialect, ImmutableArray<string> stack,
            ref int budget, Assessment result, bool detectCycles)
        {
            result.Importable = false;
            result.Add(Script);
            if (tokens.Length != 2 || ConfigImportScript.ExecPath(tokens[1].Value.AsSpan()) is not { } path)
            {
                result.Add(UnresolvedExec);
                return;
            }

            bool found = files.TryGetValue(path, out var target);
            result.Dependencies.Add(new(ConfigImportDependencyKind.Exec, path, found, []));
            if (!found)
            {
                result.Add(UnresolvedExec);
                return;
            }
            string key = "file:" + path;
            if (stack.Contains(key))
            {
                if (detectCycles)
                    result.Block(Cycle);
                return;
            }

            Expand(target!.Document.Serialized, dialect, new(path, [], new(0, 0)), null, stack.Add(key), ref budget, result, detectCycles);
        }

        private void AssessVariable(ImmutableArray<ConfigToken> tokens, ConfigDialect dialect, ConfigImportLocation location,
            ImmutableArray<string> stack, ref int budget, Assessment result, bool detectCycles)
        {
            if (tokens.Length != 2)
            {
                result.Add(Malformed, importable: false);
                return;
            }

            string variable = tokens[1].Text.ToLowerInvariant();
            string key = "variable:" + variable;
            var values = graph.Values.GetValueOrDefault(variable);
            bool resolved = values is { Count: > 0 };
            result.Dependencies.Add(new(ConfigImportDependencyKind.Vstr, variable, resolved,
                graph.Origins.TryGetValue(variable, out var origins) ? [.. origins] : []));
            if (stack.Contains(key))
            {
                if (detectCycles)
                    result.Block(Cycle);
            }
            else if (!resolved)
            {
                result.Add(UnresolvedVariable, importable: false);
            }
            else
            {
                var expandedStack = stack.Add(key);
                foreach (var value in values!.Order(ConfigScriptBytes.Instance))
                {
                    if (value.Length > 1022)
                        result.Block(Limit);
                    else
                        Expand(value, dialect, location, key, expandedStack, ref budget, result, detectCycles);
                    if (budget <= 0)
                    {
                        result.Block(Limit);
                        break;
                    }
                }
            }
        }

        private void AssessMutation(string name, ImmutableArray<ConfigToken> tokens, ConfigDialect dialect,
            ConfigImportLocation location, ImmutableArray<string> stack, ref int budget, Assessment result)
        {
            if (name is "increase" or "decrease" && catalog.Command(name, dialect) is null)
            {
                result.Add(UnknownForwardedCommand, importable: false);
                return;
            }
            if (tokens.Length < 2)
            {
                result.Add(Malformed, importable: false);
                return;
            }

            string destination = tokens[1].Text.ToLowerInvariant();
            IEnumerable<ImmutableArray<byte>> values = tokens.Length > 2 ? tokens.Skip(2).Select(token => token.Value) : [ImmutableArray<byte>.Empty];
            foreach (var value in values)
            {
                AssessSetting(destination, value, dialect, result);
                if (name != "setfromdvar" && ConfigImportScript.LooksExecutable(value, dialect))
                {
                    result.Add(Script);
                    Expand(value, dialect, location, "value:" + destination, stack, ref budget, result, detectCycles: false);
                }
            }

            if (name != "setfromdvar")
                return;
            if (tokens.Length != 3)
            {
                result.Add(Malformed, importable: false);
                return;
            }

            string source = tokens[2].Text.ToLowerInvariant();
            var copied = graph.Values.GetValueOrDefault(source);
            if (copied is not { Count: > 0 })
                result.Add(UnresolvedVariable, importable: false);
            else
            {
                foreach (var value in copied.Order(ConfigScriptBytes.Instance))
                {
                    AssessSetting(destination, value, dialect, result);
                    if (ConfigImportScript.LooksExecutable(value, dialect) || graph.Referenced.Contains(destination))
                    {
                        result.Add(Script);
                        Expand(value, dialect, location, "copied:" + source, stack, ref budget, result, detectCycles: false);
                    }
                    if (budget <= 0)
                    {
                        result.Block(Limit);
                        break;
                    }
                }
            }
            if (ConfigFetchRequest.CredentialName(source))
                result.Block(Credential);
        }

        private void AssessOther(string name, ConfigDialect dialect, Assessment result)
        {
            if (ConfigFetchRequest.CredentialName(name) || name.Contains("login", StringComparison.Ordinal))
                result.Block(Credential);
            else if (PathName(name))
                result.Block(FilesystemPath);
            else if (InertName(name))
            {
                result.Add(ConfigImportRisk.InertName, importable: false);
                result.Add(UnknownForwardedCommand, importable: false);
            }
            else if (catalog.Command(name, dialect) is { } info)
            {
                if (!info.Bindable || !SafeAction(name))
                    result.Add(SideEffect);
            }
            else if (catalog.Setting(name, dialect) is not null)
                result.Importable = false;
            else
                result.Add(UnknownForwardedCommand, importable: false);
        }

        private void Expand(ImmutableArray<byte> data, ConfigDialect dialect, ConfigImportLocation location, string? label,
            ImmutableArray<string> stack, ref int budget, Assessment result, bool detectCycles)
        {
            var document = new ConfigDocument(data.AsSpan(), dialect);
            var context = label is null ? ImmutableArray<string>.Empty : location.Context.Add(label);
            if (label?.StartsWith("bind:", StringComparison.Ordinal) == true)
                stack = stack.Add(label);
            foreach (var command in document.Commands.Where(command =>
                !command.Tokens.IsEmpty || command.Syntax.Failure is not null || command.SplitAtLimit))
            {
                if (budget <= 0)
                {
                    result.Block(Limit);
                    break;
                }
                var childLocation = new ConfigImportLocation(location.File, context, command.Span);
                var child = Assess(command, document, childLocation, stack, ref budget, detectCycles);
                child.Effects.Insert(0, new(childLocation, command, child.Level, [.. child.Reasons], Details(child, command, document)));
                result.Merge(child);
            }
        }

        private void AssessSetting(string name, ImmutableArray<byte> value, ConfigDialect dialect, Assessment result)
        {
            var info = catalog.Setting(name, dialect);
            if ((graph.ArchivedNames.Contains(name) || info?.Archived == true) &&
                !PreservesSavedValue(value, "seta", name, escapeQuotes: false, dialect))
                result.Block(Malformed);

            string text = ConfigImportScript.Text(value.AsSpan());
            if (ConfigFetchRequest.CredentialName(name))
                result.Block(Credential);
            if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" &&
                (url.UserInfo.Length > 0 || ConfigFetchRequest.HasCredentialQuery(url)))
                result.Block(Credential);
            if (PathName(name) || PathValue(text))
                result.Block(FilesystemPath);
            if (name.StartsWith("cg_fov", StringComparison.Ordinal))
                result.Block(ProtectedSetting);
            if (info is not null)
            {
                if (info.Inert)
                    result.Add(ConfigImportRisk.InertName);
                if (info.CheatProtected || info.ReadOnly || info.WriteProtected)
                    result.Block(ProtectedSetting);
                else if (!info.Inert && !info.Archived)
                    result.Add(SideEffect);
            }
            else if (InertName(name))
                result.Add(ConfigImportRisk.InertName);
            else
                result.Add(UnknownSetting);
            if (name is "name" or "cl_anonymous")
                result.Add(Identity);
            if (name.Contains("download", StringComparison.Ordinal) || name.Contains("redirect", StringComparison.Ordinal) ||
                name.Contains("upload", StringComparison.Ordinal) || name.Contains("www", StringComparison.Ordinal) ||
                StartsWithAny(name, NetworkPrefixes))
                result.Add(Network);
            if (name is "activeaction" or "com_writeconfig" or "cl_avidemo" or "developer" or "developer_script" or "logfile" or "g_log" or "r_logfile")
                result.Add(SideEffect);
        }
    }

    private static readonly string[] NetworkPrefixes = ["net_", "server", "cl_master"];
    private static readonly string[] InertPrefixes = ["cg_sprintmeter", "cg_scoresping_", "compass", "r_texfilter", "b_"];

    private static bool PreservesSavedValue(ImmutableArray<byte> value, string command, string name, bool escapeQuotes, ConfigDialect dialect)
    {
        var saved = new List<byte>(value.Length + 32);
        saved.AddRange(Encoding.UTF8.GetBytes($"{command} {name} \""));
        foreach (byte item in value)
        {
            if (escapeQuotes && item == '"')
                saved.Add((byte)'\\');
            saved.Add(item);
        }
        saved.AddRange([(byte)'"', 10]);
        var parsed = new ConfigDocument(saved.ToArray(), dialect);
        if (parsed.IgnoredSuffix is not null || parsed.Commands.Length != 1)
            return false;
        var reloaded = parsed.Commands[0];
        return !reloaded.SplitAtLimit && reloaded.Syntax.Failure is null && reloaded.Tokens.Length == 3 &&
            reloaded.Tokens.All(token => token.Closed) &&
            reloaded.Tokens[0].Value.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(command)) &&
            reloaded.Tokens[1].Value.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(name)) &&
            reloaded.Tokens[2].Value.AsSpan().SequenceEqual(value.AsSpan());
    }

    private static bool PathName(string name) =>
        name.StartsWith("fs_", StringComparison.Ordinal) || name.StartsWith("sys_", StringComparison.Ordinal) ||
        name.Contains("path", StringComparison.Ordinal) || name.Contains("directory", StringComparison.Ordinal) ||
        name is "com_playerprofile" or "g_log";

    private static bool PathValue(string value)
    {
        string text = value.Trim();
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return false;
        return text.StartsWith('~') || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains('/') || text.Contains('\\');
    }

    private static bool SafeAction(string name) =>
        name.StartsWith('+') || name.StartsWith('-') || name is "weapnext" or "weapprev" or "weaponslot" or "toggleads" or
        "togglescores" or "gocrouch" or "goprone" or "gostand" or "togglecrouch" or "toggleprone" or "centerview" or
        "togglemenu" or "openmenu" or "closemenu" or "toggleconsole" or "chatmodepublic" or "chatmodeteam" or "clear";

    private static bool InertName(string name) =>
        name is "cl_timenudge" or "r_vsync" or "cl_run" or "pmove_fixed" or "cvarlist" or "r_primitives" or "r_smp" or "handicap" or "head" or
        "cg_stereoseparation" or "cg_laserlight" or "r_dof_enable" or "ragdoll_enable" || StartsWithAny(name, InertPrefixes);

    private static bool StartsWithAny(string name, string[] prefixes) =>
        prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
}
