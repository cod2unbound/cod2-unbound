using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace CoD2Unbound.Core;

public abstract record ConfigChainBehavior
{
    private ConfigChainBehavior() { }
    public sealed record Commands(ImmutableArray<string> Steps) : ConfigChainBehavior;
    public sealed record SettingCycle(string Setting, ImmutableArray<string> Values) : ConfigChainBehavior;
    public sealed record CommandCycle(ImmutableArray<ImmutableArray<string>> States) : ConfigChainBehavior;
}
public sealed record ConfigChain(string Id, string Key, ConfigChainBehavior Behavior, string? Name = null);
public enum ConfigChainFailure
{
    InvalidIdentity, DuplicateKey, InvalidCommands, UnsafeQuotes, HeldActionInCycle, HelperTooLong,
    UnsafeExecution, OpaqueOwnedFile, ConflictNeedsReview, Locked, PortableDependency, PortablePreviewLimit
}
public sealed class ConfigChainException(ConfigChainFailure failure, string? setting = null)
    : InvalidOperationException(failure switch
    {
        ConfigChainFailure.InvalidIdentity => "Use a unique chain identifier with ASCII letters, numbers and underscores.",
        ConfigChainFailure.DuplicateKey => "Two chains use the same game key. Choose a different key.",
        ConfigChainFailure.InvalidCommands => "Each step needs a complete command within the game's line and token limits.",
        ConfigChainFailure.UnsafeQuotes => "The game's quote rules change this script. Remove embedded quotes or use a direct multi-command bind. Windows export cannot preserve quotes inside archived helper values.",
        ConfigChainFailure.HeldActionInCycle => "A vstr cycle cannot release a held + action. Use a direct bind starting with that action.",
        ConfigChainFailure.HelperTooLong => "This step exceeds the game's 1,022-byte executable command limit. Split it into shorter steps.",
        ConfigChainFailure.UnsafeExecution => "This chain repeats without another keypress, or exceeds the preview's execution limits. Remove the recursive exec/vstr call.",
        ConfigChainFailure.OpaqueOwnedFile => "This chain file or its saved definitions changed outside Config Studio. Preserve it and review the text before replacing it.",
        ConfigChainFailure.ConflictNeedsReview => "This key already has a different binding. Review and confirm replacing it.",
        ConfigChainFailure.Locked => $"Unlock {setting} before changing this chain.",
        ConfigChainFailure.PortableDependency => "This loadout requires an exec file, an unresolved variable, or an unarchived script helper. Include and review those commands in the loadout before exporting for Windows.",
        ConfigChainFailure.PortablePreviewLimit => "This imported script has more states than the 128-press export preview can verify. Simplify the cycle or review and export its dependencies separately.",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    })
{
    public ConfigChainFailure Failure { get; } = failure;
}

public enum ConfigChainKind { Empty, Direct, MultiCommand, SettingCycle, Opaque }
public sealed record ConfigChainInspection(ConfigChainKind Kind, ImmutableArray<byte>? Binding, ConfigChainTrace Press, ConfigChainTrace Release);
public sealed record ConfigChainEdit(string ProfileId, ConfigProfileVersion Before, ConfigDocument Document, ConfigStudioMetadata Metadata,
    ImmutableArray<byte> Script, ImmutableArray<string> RuntimeVariables, ImmutableArray<ConfigChainIssue> Issues,
    ImmutableArray<string> ConflictingKeys, ImmutableArray<ConfigChain> Chains)
{
    public int RuntimeVariablePoolCapacity => Document.Dialect == ConfigDialect.Cod2x ? 4096 : 1280;
    public bool HasChanges => !Before.Matches(new(Document.Serialized, ConfigProfileFormat.Encode(Metadata), Script.IsEmpty ? null : Script));
}

public static class ConfigChains
{
    private static readonly bool[] BothWaitModes = [false, true];
    private static readonly HashSet<string> SettingCommands = new(StringComparer.Ordinal)
    {
        "set", "seta", "sets", "setu", "toggle", "togglep", "reset", "setfromdvar"
    };

    private sealed class Compilation
    {
        public ImmutableArray<byte> Script { get; set; } = [];
        public SortedDictionary<string, ImmutableArray<byte>> Bindings { get; } = new(StringComparer.Ordinal);
        public List<string> Variables { get; } = [];
        public HashSet<ConfigChainIssue> Issues { get; } = [];
    }

    public static ConfigChainInspection Inspect(string key, ConfigDocument document,
        IReadOnlyDictionary<string, ImmutableArray<byte>>? files = null, bool waitDisabled = false)
    {
        var binding = document.EffectiveBindBytes(key);
        var machine = new ConfigChainMachine(document.Dialect, files, waitDisabled);
        _ = machine.Execute(document.Serialized);
        int number = ConfigDocument.KeyNumber(key) ?? 0;
        var press = machine.KeyEvent(binding ?? [], number, true, 100);
        var release = machine.KeyEvent(binding ?? [], number, false, 200);
        var parsed = new ConfigDocument((binding ?? []).AsSpan(), document.Dialect);
        var live = parsed.Commands.Where(command => !command.Tokens.IsEmpty).ToArray();
        var kind = binding is null || binding.Value.IsEmpty ? ConfigChainKind.Empty :
            !parsed.Diagnostics.IsEmpty || parsed.Commands.Any(command => !command.Comments.IsEmpty) ||
            live.Any(command => command.Kind is ConfigCommandKind.Vstr or ConfigCommandKind.Exec or ConfigCommandKind.Setting or
                ConfigCommandKind.Bind or ConfigCommandKind.Unbind or ConfigCommandKind.Unbindall) ? ConfigChainKind.Opaque :
            live.Length == 1 && live[0].Tokens[0].Text.Equals("toggle", StringComparison.OrdinalIgnoreCase) && live[0].Tokens.Length >= 4
                ? ConfigChainKind.SettingCycle : live.Length > 1 ? ConfigChainKind.MultiCommand : ConfigChainKind.Direct;
        return new(kind, binding, press, release);
    }

    public static ImmutableArray<ConfigChain> OwnedChains(ConfigProfileRead read)
    {
        if (read.Metadata.ChainDefinitions is not { } definitions)
            return read.Version.Chain is null ? [] : throw new ConfigChainException(ConfigChainFailure.OpaqueOwnedFile);
        ImmutableArray<ConfigChain> chains;
        try { chains = Decode(definitions); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ConfigChainException(ConfigChainFailure.OpaqueOwnedFile);
        }
        var compiled = Compile(chains, read.Profile.Id, read.Document.Dialect);
        if (!Matches(read.Version.Chain, compiled.Script.IsEmpty ? null : compiled.Script))
            throw new ConfigChainException(ConfigChainFailure.OpaqueOwnedFile);
        return chains;
    }

    public static ConfigChainEdit Prepare(ImmutableArray<ConfigChain> chains, ConfigProfileRead read)
    {
        var previous = OwnedChains(read);
        var old = Compile(previous, read.Profile.Id, read.Document.Dialect);
        var next = Compile(chains, read.Profile.Id, read.Document.Dialect);
        var document = read.Document;
        var conflicts = ImmutableArray.CreateBuilder<string>();
        foreach (var (key, binding) in old.Bindings)
        {
            if (next.Bindings.ContainsKey(key) || !Matches(document.EffectiveBindBytes(key), binding))
                continue;
            CheckExistingLocks(key, binding, read);
            document = document.RemoveBind(key);
        }
        foreach (var (key, binding) in next.Bindings)
        {
            var existing = document.EffectiveBindBytes(key);
            if (Matches(existing, binding))
                continue;
            CheckLocks(key, ParseCommands(binding, document.Dialect), read);
            if (existing is { IsEmpty: false } bytes)
            {
                CheckExistingLocks(key, bytes, read);
                if (!old.Bindings.TryGetValue(key, out var oldBinding) || !Matches(bytes, oldBinding))
                    conflicts.Add(key);
            }
            document = document.SetBind(key, binding.AsSpan());
        }

        var definitions = chains.IsEmpty ? (ImmutableArray<byte>?)null : Encode(chains);
        bool changed = !Matches(previous.IsEmpty ? null : Encode(previous), definitions);
        if (changed)
        {
            foreach (var chain in previous.Concat(chains))
                CheckLocks(chain.Key, Texts(chain.Behavior).SelectMany(text =>
                    ParseCommands([.. ConfigDocument.Encode(text)], document.Dialect)), read);
        }
        var metadata = read.Metadata with { ChainDefinitions = definitions };
        var ownedNames = next.Variables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (read.Document.Commands.Any(command => command.Kind == ConfigCommandKind.Setting && command.Tokens.Length > 1 &&
            ownedNames.Contains(command.Tokens[1].Text)))
            throw new ConfigChainException(ConfigChainFailure.OpaqueOwnedFile);

        foreach (bool disabled in WaitModes(document.Dialect))
        {
            var machine = new ConfigChainMachine(document.Dialect, waitDisabled: disabled);
            _ = machine.Execute(document.Serialized);
            _ = machine.Execute(next.Script);
            foreach (var (key, binding) in next.Bindings)
                PreviewUntilRepeat(machine, key, binding, trace =>
                {
                    if (trace.Issues.Any(item => item.BlocksCompilation))
                        throw new ConfigChainException(ConfigChainFailure.UnsafeExecution);
                    if (changed)
                        CheckLocks(key, trace.Commands, read);
                    next.Issues.UnionWith(trace.Issues);
                }, changed && read.Metadata.Locks.Count > 0 ? ConfigChainFailure.UnsafeExecution : null);
        }
        return new(read.Profile.Id, read.Version, document, metadata, next.Script, [.. next.Variables], [.. next.Issues],
            conflicts.ToImmutable(), chains);
    }

    public static ConfigHistoryEntry Apply(ConfigChainEdit edit, ConfigProfileStore store, bool replaceConflicts = false)
    {
        var current = store.Read(edit.ProfileId);
        if (!edit.Before.Matches(current.Version) || current.Document.Dialect != edit.Document.Dialect)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        var verified = Prepare(edit.Chains, current);
        if (!verified.ConflictingKeys.IsEmpty && !replaceConflicts)
            throw new ConfigChainException(ConfigChainFailure.ConflictNeedsReview);
        return store.UpdateChain(edit.ProfileId, verified.Document, edit.Before, verified.Metadata,
            verified.Script.IsEmpty ? null : verified.Script, "Changed advanced binds");
    }

    public static ImmutableArray<byte> WindowsExport(ConfigProfileRead read)
    {
        var dialect = read.Document.Dialect;
        var portable = Compile(OwnedChains(read), read.Profile.Id, dialect, portable: true);
        var output = read.Document.Serialized.ToBuilder();
        if (output.Count > 0 && output[^1] is not (10 or 13))
            output.Add(10);
        output.AddRange(portable.Script);
        if (output.Count > 65_535)
            throw new ConfigEditException(ConfigEditFailure.CommandBufferOverflow);
        var bytes = output.ToImmutable();
        var parsed = new ConfigDocument(bytes.AsSpan(), dialect);
        if (!parsed.Diagnostics.IsEmpty)
            throw new ConfigChainException(ConfigChainFailure.UnsafeQuotes);
        var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in parsed.Commands.Where(item => item.Tokens.Length >= 3 &&
            item.Tokens[0].Text.Equals("seta", StringComparison.OrdinalIgnoreCase)))
        {
            string name = command.Tokens[1].Text;
            var assignment = new ConfigDocument(command.Span.Slice(bytes.AsSpan()), dialect).EffectiveSettingBytes(name) ?? [];
            if (assignment.Contains((byte)34) || assignment.LastOrDefault() == 92)
                throw new ConfigChainException(ConfigChainFailure.UnsafeQuotes);
            archived.Add(name);
        }
        var machine = new ConfigChainMachine(dialect);
        Check(machine.Execute(bytes));
        foreach (var key in Enumerable.Range(0, 256).Select(ConfigDocument.KeySpelling).Where(name => ConfigDocument.KeyNumber(name) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (parsed.EffectiveBindBytes(key) is { } binding)
                PreviewUntilRepeat(machine, key, binding, Check, ConfigChainFailure.PortablePreviewLimit);
        }
        if (!machine.ReferencedVariables.IsSubsetOf(archived))
            throw new ConfigChainException(ConfigChainFailure.PortableDependency);
        return bytes;

        void Check(ConfigChainTrace trace)
        {
            if (!trace.Complete)
                throw new ConfigChainException(ConfigChainFailure.PortableDependency);
            if (machine.UnsafeArchivedVariables.Count > 0)
                throw new ConfigChainException(ConfigChainFailure.UnsafeQuotes);
        }
    }

    public static ConfigImportReview SafetyReview(ConfigChainEdit edit, IConfigImportCatalog catalog)
    {
        const string name = "profile.cfg";
        ImmutableArray<byte> bytes = [.. edit.Document.Serialized, 10, .. edit.Script];
        return new ConfigImportSafety(catalog).Review(new(new(ConfigImportSourceKind.Paste), name,
            [new(name, new(bytes.AsSpan(), edit.Document.Dialect))], []));
    }

    private static Compilation Compile(ImmutableArray<ConfigChain> chains, string profile, ConfigDialect dialect,
        bool portable = false)
    {
        _ = ConfigStudioLaunch.ChainBasename(profile);
        var result = new Compilation();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<int>();
        var script = ImmutableArray.CreateBuilder<byte>();
        int preview = 3;
        foreach (var chain in chains)
        {
            if (chain.Id.Length > 40 || !ValidName(chain.Id) || !ids.Add(chain.Id))
                throw new ConfigChainException(ConfigChainFailure.InvalidIdentity);
            if (ConfigDocument.KeyNumber(chain.Key) is not { } number || number is 96 or 126)
                throw new ConfigEditException(ConfigEditFailure.InvalidKey);
            if (!keys.Add(number))
                throw new ConfigChainException(ConfigChainFailure.DuplicateKey);
            var trigger = chain.Behavior switch
            {
                ConfigChainBehavior.Commands commands => Joined(commands.Steps, dialect),
                ConfigChainBehavior.SettingCycle cycle => SettingCycle(cycle, dialect),
                ConfigChainBehavior.CommandCycle cycle => CommandCycle(chain.Id, cycle, dialect, portable, result, script),
                _ => throw new ConfigChainException(ConfigChainFailure.InvalidCommands)
            };
            if (chain.Behavior is ConfigChainBehavior.CommandCycle states)
                preview = Math.Max(preview, states.States.Length);
            if (trigger.Length > 1023 || (trigger[0] == 43 && trigger.Length + " 255 -2147483648\n".Length > 1023))
                throw new ConfigEditException(ConfigEditFailure.BindTooLong);
            string key = ConfigDocument.KeySpelling(number);
            _ = new ConfigDocument([], dialect).SetBind(key, trigger.AsSpan());
            result.Bindings.Add(key, trigger);
        }
        if (script.Count > 65_535)
            throw new ConfigEditException(ConfigEditFailure.CommandBufferOverflow);
        result.Script = script.ToImmutable();
        foreach (bool wait in WaitModes(dialect))
        {
            var machine = new ConfigChainMachine(dialect, waitDisabled: wait);
            Observe(machine.Execute(result.Script));
            foreach (var (key, bytes) in result.Bindings)
            {
                int number = ConfigDocument.KeyNumber(key)!.Value;
                for (int turn = 0; turn < preview; turn++)
                {
                    Observe(machine.KeyEvent(bytes, number, true, 100));
                    Observe(machine.KeyEvent(bytes, number, false, 200));
                }
            }
        }
        return result;

        void Observe(ConfigChainTrace trace)
        {
            if (trace.Issues.Any(issue => issue.BlocksCompilation))
                throw new ConfigChainException(ConfigChainFailure.UnsafeExecution);
            result.Issues.UnionWith(trace.Issues);
        }
    }

    private static ImmutableArray<byte> SettingCycle(ConfigChainBehavior.SettingCycle cycle, ConfigDialect dialect)
    {
        if (!ValidName(cycle.Setting) || cycle.Values.Length < 2)
            throw new ConfigChainException(ConfigChainFailure.InvalidCommands);
        var values = cycle.Values.Select(value =>
        {
            byte[] bytes = ConfigDocument.Encode(value);
            if (bytes.AsSpan().ContainsAny((byte)0, (byte)10, (byte)13) || bytes.Contains((byte)34) || bytes.LastOrDefault() == 92)
                throw new ConfigChainException(ConfigChainFailure.UnsafeQuotes);
            return bytes.Length > 0 && bytes.All(item => item is >= 48 and <= 57 or 43 or 45 or 46) ? value : "\"" + value + "\"";
        });
        ImmutableArray<byte> trigger = [.. ConfigDocument.Encode($"toggle {cycle.Setting} {string.Join(' ', values)}")];
        _ = Validate(trigger, dialect);
        return trigger;
    }

    private static ImmutableArray<byte> CommandCycle(string id, ConfigChainBehavior.CommandCycle cycle,
        ConfigDialect dialect, bool portable, Compilation compilation, ImmutableArray<byte>.Builder script)
    {
        if (cycle.States.Length is < 2 or > 128)
            throw new ConfigChainException(ConfigChainFailure.InvalidCommands);
        string prefix = "cs_b2_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant())).AsSpan(0, 20));
        string pointer = prefix + "_next";
        string[] names = [.. Enumerable.Range(0, cycle.States.Length).Select(index => FormattableString.Invariant($"{prefix}_s{index}"))];
        string verb = portable ? "seta" : "set";
        for (int index = 0; index < names.Length; index++)
        {
            var body = Joined(cycle.States[index], dialect);
            if (ParseCommands(body, dialect).Any(tokens => tokens[0].Value is [43, ..]))
                throw new ConfigChainException(ConfigChainFailure.HeldActionInCycle);
            string next = names[(index + 1) % names.Length];
            ImmutableArray<byte> value = [.. body, .. ConfigDocument.Encode($"; {verb} {pointer} vstr {next}")];
            Append(names[index], value);
        }
        ImmutableArray<byte> initial = [.. ConfigDocument.Encode("vstr " + names[0])];
        Append(pointer, initial);
        compilation.Variables.AddRange(names);
        compilation.Variables.Add(pointer);
        return [.. ConfigDocument.Encode("vstr " + pointer)];

        void Append(string name, ImmutableArray<byte> value)
        {
            if (value.Length > 1022)
                throw new ConfigChainException(ConfigChainFailure.HelperTooLong);
            if ((portable && value.Contains((byte)34)) || value.LastOrDefault() == 92)
                throw new ConfigChainException(ConfigChainFailure.UnsafeQuotes);
            var line = ImmutableArray.CreateBuilder<byte>();
            line.AddRange(ConfigDocument.Encode($"{verb} {name} \""));
            foreach (byte item in value)
            {
                if (item == 34) line.Add(92);
                line.Add(item);
            }
            line.Add(34);
            var parsed = Validate(line.ToImmutable(), dialect);
            if (parsed.Commands is not [{ Tokens: [var first, var second, var third] }] ||
                first.Text != verb || second.Text != name || !Matches(third.Value, value))
                throw new ConfigChainException(ConfigChainFailure.UnsafeQuotes);
            if (line.Count + 1 > 65_535 - script.Count)
                throw new ConfigEditException(ConfigEditFailure.CommandBufferOverflow);
            script.AddRange(line);
            script.Add(10);
        }
    }

    private static ImmutableArray<byte> Joined(ImmutableArray<string> commands, ConfigDialect dialect)
    {
        if (commands.IsDefaultOrEmpty)
            throw new ConfigChainException(ConfigChainFailure.InvalidCommands);
        var expected = new List<ImmutableArray<ConfigToken>>();
        var result = ImmutableArray.CreateBuilder<byte>();
        foreach (string text in commands)
        {
            ImmutableArray<byte> bytes = [.. ConfigDocument.Encode(text)];
            var parsed = Validate(bytes, dialect);
            if (parsed.Commands is not [{ Tokens.IsEmpty: false, Comments.IsEmpty: true } command])
                throw new ConfigChainException(ConfigChainFailure.InvalidCommands);
            expected.Add(command.Tokens);
            if (result.Count > 0)
                result.AddRange("; "u8);
            if (bytes.Length > 4094 - result.Count)
                throw new ConfigChainException(ConfigChainFailure.InvalidCommands);
            result.AddRange(bytes);
        }
        var output = result.ToImmutable();
        var actual = Validate(output, dialect).Commands;
        if (actual.Length != expected.Count || actual.Where((command, index) => command.Tokens.Length != expected[index].Length ||
            command.Tokens.Where((token, tokenIndex) => !Matches(token.Value, expected[index][tokenIndex].Value)).Any()).Any())
            throw new ConfigChainException(ConfigChainFailure.UnsafeQuotes);
        return output;
    }

    private static ConfigDocument Validate(ImmutableArray<byte> bytes, ConfigDialect dialect)
    {
        if (bytes.Length > 4094 || bytes.AsSpan().ContainsAny((byte)0, (byte)10, (byte)13))
            throw new ConfigChainException(ConfigChainFailure.InvalidCommands);
        var parsed = new ConfigDocument(bytes.AsSpan(), dialect);
        if (!parsed.Diagnostics.IsEmpty)
            throw new ConfigChainException(ConfigChainFailure.InvalidCommands);
        var quoted = parsed.Commands.SelectMany(command => command.Tokens).Where(token => token.Kind == ConfigTokenKind.Quoted).ToArray();
        for (int index = 0; index < bytes.Length; index++)
            if (bytes[index] >= 128 && !quoted.Any(token => index >= token.Span.Start && index < token.Span.End))
                throw new ConfigChainException(ConfigChainFailure.InvalidCommands);
        return parsed;
    }

    internal static IEnumerable<string> Texts(ConfigChainBehavior behavior) => behavior switch
    {
        ConfigChainBehavior.Commands commands => commands.Steps,
        ConfigChainBehavior.CommandCycle cycle => cycle.States.SelectMany(state => state),
        ConfigChainBehavior.SettingCycle cycle => ["toggle " + cycle.Setting],
        _ => []
    };

    private static IEnumerable<ImmutableArray<ConfigToken>> ParseCommands(ImmutableArray<byte> value, ConfigDialect dialect) =>
        new ConfigDocument(value.AsSpan(), dialect).Commands.Select(command => command.Tokens).Where(tokens => !tokens.IsEmpty);

    private static void CheckExistingLocks(string key, ImmutableArray<byte> binding, ConfigProfileRead read)
    {
        if (read.Metadata.Locks.Count == 0)
            return;
        CheckLocks(key, ParseCommands(binding, read.Document.Dialect), read);
        var machine = new ConfigChainMachine(read.Document.Dialect);
        _ = machine.Execute(read.Document.Serialized);
        if (read.Version.Chain is { } script)
            _ = machine.Execute(script);
        PreviewUntilRepeat(machine, key, binding, trace =>
        {
            if (trace.Issues.Any(issue => issue.BlocksCompilation))
                throw new ConfigChainException(ConfigChainFailure.UnsafeExecution);
            CheckLocks(key, trace.Commands, read);
        }, ConfigChainFailure.UnsafeExecution);
    }

    private static void CheckLocks(string key, IEnumerable<ImmutableArray<ConfigToken>> commands, ConfigProfileRead read)
    {
        if (read.Metadata.Locks.Count == 0)
            return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            string name = command[0].Text.ToLowerInvariant();
            names.Add(name);
            if (command.Length > 1 && SettingCommands.Contains(name))
                names.Add(command[1].Text);
        }
        foreach (string value in read.Metadata.Locks.Order(StringComparer.Ordinal))
        {
            string name = value.ToLowerInvariant();
            string action = name.StartsWith("action:", StringComparison.Ordinal) ? name[7..] : name;
            if ((name.StartsWith("bind:", StringComparison.Ordinal) && ConfigDocument.KeyNumber(name[5..]) == ConfigDocument.KeyNumber(key))
                || names.Contains(action))
                throw new ConfigChainException(ConfigChainFailure.Locked, value);
        }
    }

    private static void PreviewUntilRepeat(ConfigChainMachine machine, string key, ImmutableArray<byte> binding,
        Action<ConfigChainTrace> observe, ConfigChainFailure? failure)
    {
        int number = ConfigDocument.KeyNumber(key)!.Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int turn = 0; turn <= 128; turn++)
        {
            if (!seen.Add(machine.State(includeArchived: true)))
                return;
            if (turn == 128)
                break;
            observe(machine.KeyEvent(binding, number, true, 100));
            observe(machine.KeyEvent(binding, number, false, 200));
        }
        if (failure is { } reason)
            throw new ConfigChainException(reason);
    }

    private static bool Matches(ImmutableArray<byte>? left, ImmutableArray<byte>? right) =>
        left.HasValue == right.HasValue && (left is not { } bytes || bytes.AsSpan().SequenceEqual(right!.Value.AsSpan()));

    private static bool ValidName(string name) => name.Length > 0 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    private static ReadOnlySpan<bool> WaitModes(ConfigDialect dialect) => dialect == ConfigDialect.Cod2x ? BothWaitModes : BothWaitModes.AsSpan(0, 1);

    private static ImmutableArray<byte> Encode(ImmutableArray<ConfigChain> chains)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (var chain in chains)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("behavior");
                writer.WriteStartObject();
                switch (chain.Behavior)
                {
                    case ConfigChainBehavior.Commands commands:
                        writer.WriteStartObject("commands");
                        writer.WritePropertyName("_0");
                        WriteTexts(writer, commands.Steps);
                        break;
                    case ConfigChainBehavior.CommandCycle cycle:
                        writer.WriteStartObject("commandCycle");
                        writer.WritePropertyName("steps");
                        writer.WriteStartArray();
                        foreach (var step in cycle.States)
                            WriteTexts(writer, step);
                        writer.WriteEndArray();
                        break;
                    case ConfigChainBehavior.SettingCycle cycle:
                        writer.WriteStartObject("settingCycle");
                        writer.WriteString("name", cycle.Setting);
                        writer.WritePropertyName("values");
                        WriteTexts(writer, cycle.Values);
                        break;
                    default: throw new ConfigChainException(ConfigChainFailure.InvalidCommands);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteString("id", chain.Id);
                writer.WriteString("key", chain.Key);
                if (chain.Name is not null)
                    writer.WriteString("name", chain.Name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return [.. output.ToArray()];

        static void WriteTexts(Utf8JsonWriter writer, IEnumerable<string> values)
        {
            writer.WriteStartArray();
            foreach (string value in values)
                writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
    }

    private static ImmutableArray<ConfigChain> Decode(ImmutableArray<byte> bytes)
    {
        using var json = JsonDocument.Parse(bytes.AsMemory());
        return [.. json.RootElement.EnumerateArray().Select(item =>
        {
            var tags = item.GetProperty("behavior").EnumerateObject().ToArray();
            if (tags.Length != 1)
                throw new JsonException();
            var tag = tags[0];
            ConfigChainBehavior behavior = tag.Name switch
            {
                "commands" => new ConfigChainBehavior.Commands(ReadTexts(tag.Value.GetProperty("_0"))),
                "settingCycle" => new ConfigChainBehavior.SettingCycle(RequiredText(tag.Value.GetProperty("name")), ReadTexts(tag.Value.GetProperty("values"))),
                "commandCycle" => new ConfigChainBehavior.CommandCycle([.. tag.Value.GetProperty("steps").EnumerateArray().Select(ReadTexts)]),
                _ => throw new JsonException()
            };
            return new ConfigChain(RequiredText(item.GetProperty("id")), RequiredText(item.GetProperty("key")), behavior,
                item.TryGetProperty("name", out var name) ? name.GetString() : null);
        })];

        static string RequiredText(JsonElement item) => item.GetString() ?? throw new JsonException();
        static ImmutableArray<string> ReadTexts(JsonElement item) => [.. item.EnumerateArray().Select(RequiredText)];
    }
}
