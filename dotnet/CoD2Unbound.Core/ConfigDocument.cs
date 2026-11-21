using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CoD2Unbound.Core;

/// <summary>A byte document with half-open ranges and transactional edits. Each edit returns a new document.</summary>
public sealed class ConfigDocument
{
    private readonly ImmutableArray<byte> bytes;
    private readonly int activeEnd;
    private const int MaxSafeLine = 4094;
    private const int MaxCommandBuffer = 65535;

    public ConfigDialect Dialect { get; }
    public ImmutableArray<ConfigCommand> Commands { get; }
    public ImmutableArray<ConfigDiagnostic> Diagnostics { get; }
    public ByteSpan? IgnoredSuffix { get; }
    public ImmutableArray<byte> Serialized => bytes;
    public bool IsCanonical => CanonicalLines() is not null;
    public IEnumerable<ConfigCommand> UnresolvedCommands => Commands.Where(command =>
        CommandName(command) is "exec" or "vstr" || IsDynamic(command));

    public ConfigDocument(ReadOnlySpan<byte> input, ConfigDialect dialect)
    {
        Dialect = dialect;
        bytes = ImmutableArray.Create(input);
        activeEnd = ActiveLength(input);
        var diagnostics = ImmutableArray.CreateBuilder<ConfigDiagnostic>();
        var commands = ImmutableArray.CreateBuilder<ConfigCommand>();
        if (activeEnd < input.Length)
        {
            IgnoredSuffix = new(activeEnd, input.Length);
            diagnostics.Add(new(ConfigDiagnosticKind.IgnoredNulSuffix, IgnoredSuffix));
        }
        if (activeEnd > MaxCommandBuffer)
            diagnostics.Add(new(ConfigDiagnosticKind.CommandBufferOverflow));

        for (int start = 0, after; start < activeEnd; start = after)
        {
            int scan = start;
            bool quoted = false;
            while (scan < activeEnd)
            {
                byte value = input[scan];
                if (value == '"')
                    quoted = !quoted;
                if (value is 10 or 13 || value == ';' && !quoted)
                    break;
                scan++;
            }

            int stop = Math.Min(scan, start + MaxSafeLine + 1);
            after = stop < activeEnd ? stop + 1 : stop;
            var span = new ByteSpan(start, stop);
            var separator = new ByteSpan(stop, after);
            bool limited = scan - start > MaxSafeLine;
            var syntax = Tokenize(span.Slice(input), dialect, byteOffset: start);
            commands.Add(new(span, separator, syntax, limited));
            if (limited)
                diagnostics.Add(new(ConfigDiagnosticKind.LineLimit, span, separator));
            if (syntax.Failure is { } failure)
                diagnostics.Add(new(ConfigDiagnosticKind.TokenizationFailure, span, Failure: failure));
            foreach (var token in syntax.Tokens)
                if (token.Kind == ConfigTokenKind.Quoted && !token.Closed)
                    diagnostics.Add(new(ConfigDiagnosticKind.UnterminatedQuote, token.Span));
        }

        Commands = commands.ToImmutable();
        Diagnostics = diagnostics.ToImmutable();
    }

    public static ConfigTokenization Tokenize(ReadOnlySpan<byte> input, ConfigDialect dialect, int tokenLimit = 0, int byteOffset = 0)
    {
        var source = input[..ActiveLength(input)];
        var tokens = ImmutableArray.CreateBuilder<ConfigToken>();
        var comments = ImmutableArray.CreateBuilder<ByteSpan>();
        int position = 0, used = 0;
        const int maxTokenBuffer = 8704;

        ByteSpan Span(int start, int end) => new(start + byteOffset, end + byteOffset);
        ConfigTokenization Result(ConfigTokenizationFailure? error = null) =>
            new(error is null ? tokens.ToImmutable() : [], comments.ToImmutable(), error);

        while (true)
        {
            if (tokens.Count == 512)
                return Result(ConfigTokenizationFailure.TooManyTokens);
            if (tokenLimit > 0 && tokens.Count == tokenLimit - 1)
            {
                if (position == source.Length)
                    return Result();
                if (used + source.Length - position + 1 > maxTokenBuffer)
                    return Result(ConfigTokenizationFailure.TokenBufferOverflow);
                tokens.Add(new(Span(position, source.Length), ImmutableArray.Create(source[position..]), ConfigTokenKind.Remainder, true));
                return Result();
            }

            while (position < source.Length && IsWhitespace(source[position]))
                position++;
            if (position == source.Length)
                return Result();
            if (LineComment(source, position, dialect))
            {
                comments.Add(Span(position, source.Length));
                return Result();
            }
            if (AtPair(source, position, (byte)'/', (byte)'*'))
            {
                int commentStart = position++;
                // The opener and closer can overlap in /*/.
                while (position < source.Length && !AtPair(source, position, (byte)'*', (byte)'/'))
                    position++;
                if (position < source.Length)
                    position += 2;
                comments.Add(Span(commentStart, position));
                continue;
            }

            int start = position;
            bool quoted = source[position] == '"', closed = true;
            ImmutableArray<byte> value;
            if (quoted)
            {
                var decoded = ImmutableArray.CreateBuilder<byte>();
                position++;
                while (position < source.Length && source[position] != '"')
                {
                    if (AtPair(source, position, (byte)'\\', (byte)'"'))
                        position++;
                    decoded.Add(source[position++]);
                }
                closed = position < source.Length;
                if (closed)
                    position++;
                value = decoded.ToImmutable();
            }
            else
            {
                while (position < source.Length && !IsWhitespace(source[position]) && source[position] != '"' &&
                       !LineComment(source, position, dialect) && !AtPair(source, position, (byte)'/', (byte)'*'))
                    position++;
                value = ImmutableArray.Create(source[start..position]);
            }

            used += value.Length + 1;
            if (used > maxTokenBuffer)
                return Result(ConfigTokenizationFailure.TokenBufferOverflow);
            tokens.Add(new(Span(start, position), value, quoted ? ConfigTokenKind.Quoted : ConfigTokenKind.Bare, closed));
            // The engine accepts exactly 512 tokens when the last one reaches EOF.
            if (position == source.Length)
                return Result();
            if (IsWhitespace(source[position]))
                position++;
        }
    }

    public ImmutableArray<byte>? EffectiveSettingBytes(string name)
    {
        if (!ValidName(name))
            return null;
        ImmutableArray<byte>? value = null;
        foreach (var command in Commands)
        {
            int first = AssignmentStart(command, name);
            if (first >= 0)
                value = Combine(command.Tokens, first, binding: false);
            else if (ChangesSetting(command, name))
                value = null;
        }
        return value;
    }

    public string? EffectiveSetting(string name) => Text(EffectiveSettingBytes(name));
    public ImmutableArray<byte>? EffectiveBindBytes(string key) =>
        KeyNumber(key) is int number ? BindingState(number).Value : null;
    public string? EffectiveBind(string key) => Text(EffectiveBindBytes(key));

    public ConfigDocument SetSetting(string name, string value) => SetSetting(name, Encode(value));
    public ConfigDocument SetSetting(string name, ReadOnlySpan<byte> value)
    {
        if (!ValidName(name))
            throw new ConfigEditException(ConfigEditFailure.InvalidSettingName);
        if (value.ContainsAny((byte)0, (byte)10, (byte)13) || value.Contains((byte)'"') || value.EndsWith("\\"u8))
            throw new ConfigEditException(ConfigEditFailure.UnsafeValue);

        byte[] spelling = [(byte)'"', .. value, (byte)'"'];
        for (int index = Commands.Length - 1; index >= 0; index--)
        {
            var command = Commands[index];
            int first = AssignmentStart(command, name);
            if (ChangesSetting(command, name))
                break;
            if (first >= 0)
                return ReplaceValue(index, first, spelling, value.ToArray());
        }
        return Insert([.. Ascii($"seta {name} "), .. spelling], name, null, ["seta"u8.ToArray(), Ascii(name), value.ToArray()]);
    }

    public ConfigDocument SetBind(string key, string binding) => SetBind(key, Encode(binding));
    public ConfigDocument SetBind(string key, ReadOnlySpan<byte> binding)
    {
        int number = KeyNumber(key) ?? throw new ConfigEditException(ConfigEditFailure.InvalidKey);
        if (binding.Length > 1023)
            throw new ConfigEditException(ConfigEditFailure.BindTooLong);
        if (binding.ContainsAny((byte)0, (byte)10, (byte)13) || binding.EndsWith("\\"u8))
            throw new ConfigEditException(ConfigEditFailure.UnsafeBinding);

        List<byte> spelling = [(byte)'"'];
        foreach (byte value in binding)
        {
            if (value == '"')
                spelling.Add((byte)'\\');
            spelling.Add(value);
        }
        spelling.Add((byte)'"');
        if (BindingState(number).Index is int index)
            return ReplaceValue(index, 2, spelling.ToArray(), binding.ToArray());

        string name = KeySpelling(number);
        return Insert([.. Ascii($"bind {name} "), .. spelling], null, number, ["bind"u8.ToArray(), Ascii(name), binding.ToArray()]);
    }

    public ConfigDocument RemoveSetting(string name)
    {
        if (!ValidName(name))
            throw new ConfigEditException(ConfigEditFailure.InvalidSettingName);
        return Remove(Enumerable.Range(0, Commands.Length).Where(index => AssignmentStart(Commands[index], name) >= 0).ToHashSet());
    }

    public ConfigDocument RemoveBind(string key)
    {
        int number = KeyNumber(key) ?? throw new ConfigEditException(ConfigEditFailure.InvalidKey);
        return Remove(Enumerable.Range(0, Commands.Length).Where(index =>
            Commands[index] is { Kind: ConfigCommandKind.Bind, Tokens.Length: >= 3 } command &&
            KeyNumber(command.Tokens[1].Text) == number).ToHashSet());
    }

    private ConfigDocument ReplaceValue(int index, int first, byte[] spelling, byte[] value)
    {
        var command = Commands[index];
        var span = new ByteSpan(command.Tokens[first].Span.Start, command.Tokens[^1].Span.End);
        if (command.SplitAtLimit || command.Comments.Any(comment => comment.Overlaps(span)))
            throw new ConfigEditException(ConfigEditFailure.LossyEdit);
        byte[][] expected = [.. command.Tokens.Take(first).Select(token => token.Value.ToArray()), value];
        return Apply([new(span, spelling)], [index], [expected]);
    }

    private ConfigDocument Insert(byte[] line, string? setting, int? key, byte[][] expected)
    {
        if (line.Length > MaxSafeLine)
            throw new ConfigEditException(ConfigEditFailure.CommandTooLong);
        var split = new ConfigDocument(line, Dialect);
        if (split.Commands.Length != 1 || split.Commands[0].Span.Length != line.Length ||
            split.Commands[0].Syntax.Failure is not null || !SameValues(split.Commands[0], expected))
            throw new ConfigEditException(ConfigEditFailure.LossyEdit);

        int position = activeEnd;
        if (CanonicalLines() is { } rows)
        {
            foreach (var row in rows.Skip(3))
            {
                var command = Commands[row.Index];
                bool before = key is int number
                    ? command.Kind == ConfigCommandKind.Setting || KeyNumber(command.Tokens[1].Text) > number
                    : command.Kind == ConfigCommandKind.Setting &&
                      string.CompareOrdinal(command.Tokens[1].Text.ToLowerInvariant(), setting!.ToLowerInvariant()) > 0;
                if (before)
                {
                    position = row.Span.Start;
                    break;
                }
            }
        }

        byte[] ending = PreferredLineEnding();
        bool needsEnding = position == activeEnd && position > 0 && bytes[position - 1] is not (10 or 13);
        byte[] placement = needsEnding ? [.. ending, .. line, .. ending] : [.. line, .. ending];
        return Apply([new(new(position, position), placement)], [], [expected]);
    }

    private ConfigDocument Remove(HashSet<int> indices)
    {
        if (indices.Count == 0)
            return this;
        var rows = CanonicalLines()?.ToDictionary(row => row.Index, row => row.Span);
        var edits = new List<Edit>();
        foreach (int index in indices.Order())
        {
            var command = Commands[index];
            if (command.SplitAtLimit)
                throw new ConfigEditException(ConfigEditFailure.LossyEdit);
            edits.Add(new(rows?.GetValueOrDefault(index) ?? command.Span, []));
        }
        return Apply(edits, indices, []);
    }

    // Reparse and compare the unaffected commands at their shifted byte offsets before accepting an edit.
    private ConfigDocument Apply(IReadOnlyList<Edit> edits, HashSet<int> affected, IReadOnlyList<byte[][]> expected)
    {
        int size = bytes.Length + edits.Sum(edit => edit.Change);
        var output = new byte[size];
        int source = 0, destination = 0;
        foreach (var edit in edits)
        {
            bytes.AsSpan()[source..edit.Span.Start].CopyTo(output.AsSpan(destination));
            destination += edit.Span.Start - source;
            edit.Replacement.CopyTo(output.AsSpan(destination));
            destination += edit.Replacement.Length;
            source = edit.Span.End;
        }
        bytes.AsSpan()[source..].CopyTo(output.AsSpan(destination));

        var candidate = new ConfigDocument(output, Dialect);
        if (candidate.activeEnd > MaxCommandBuffer)
            throw new ConfigEditException(ConfigEditFailure.CommandBufferOverflow);

        var bySpan = candidate.Commands.ToDictionary(command => command.Span);
        var matched = new HashSet<ByteSpan>();
        int nextEdit = 0, shift = 0;
        for (int index = 0; index < Commands.Length; index++)
        {
            if (affected.Contains(index))
                continue;
            var command = Commands[index];
            while (nextEdit < edits.Count && edits[nextEdit].Span.End <= command.Span.Start)
                shift += edits[nextEdit++].Change;
            // A canonical line deletion can consume the empty command formed by LF in CRLF.
            if (command.Span.Length == 0 && nextEdit < edits.Count && edits[nextEdit].Span.Start <= command.Span.Start)
                continue;
            var shifted = new ByteSpan(command.Span.Start + shift, command.Span.End + shift);
            if (!bySpan.TryGetValue(shifted, out var match) ||
                !command.Span.Slice(bytes.AsSpan()).SequenceEqual(shifted.Slice(output)) ||
                command.Syntax.Failure != match.Syntax.Failure ||
                !SameValues(match, command.Tokens.Select(token => token.Value.ToArray()).ToArray()))
                throw new ConfigEditException(ConfigEditFailure.LossyEdit);
            matched.Add(shifted);
        }

        var changed = candidate.Commands.Where(command => command.Span.Length > 0 && !matched.Contains(command.Span)).ToArray();
        if (changed.Length != expected.Count)
            throw new ConfigEditException(ConfigEditFailure.LossyEdit);
        for (int index = 0; index < changed.Length; index++)
        {
            var command = changed[index];
            if (command.Span.Length > MaxSafeLine)
                throw new ConfigEditException(ConfigEditFailure.CommandTooLong);
            if (command.Syntax.Failure is not null || !SameValues(command, expected[index]))
                throw new ConfigEditException(ConfigEditFailure.LossyEdit);
        }
        return candidate;
    }

    private List<CanonicalLine>? CanonicalLines()
    {
        if (!Diagnostics.IsEmpty)
            return null;
        var rows = new List<CanonicalLine>();
        var source = bytes.AsSpan();
        int commandIndex = 0;
        for (int start = 0, end; start < source.Length; start = end)
        {
            int lineBreak = source[start..].IndexOfAny((byte)10, (byte)13);
            int contentEnd = lineBreak < 0 ? source.Length : start + lineBreak;
            var content = new ByteSpan(start, contentEnd);
            if (commandIndex >= Commands.Length || Commands[commandIndex].Span != content)
                return null;

            end = contentEnd;
            if (end < source.Length)
                end += source[end..].StartsWith("\r\n"u8) ? 2 : 1;
            rows.Add(new(new(start, end), commandIndex));
            commandIndex += end - contentEnd == 2 ? 2 : 1;
        }
        if (rows.Count < 3 ||
            !Commands[rows[0].Index].Span.Slice(source).SequenceEqual("// generated by Call of Duty, do not modify"u8) ||
            !Commands[rows[1].Index].Span.Slice(source).SequenceEqual("unbindall"u8) ||
            !Commands[rows[2].Index].Span.Slice(source).SequenceEqual("unbindall"u8))
            return null;

        int lastKey = -1;
        string? lastName = null;
        foreach (var row in rows.Skip(3))
        {
            var command = Commands[row.Index];
            var tokens = command.Tokens;
            if (tokens.Length != 3 || tokens[0].Kind != ConfigTokenKind.Bare || tokens[1].Kind != ConfigTokenKind.Bare ||
                tokens[2] is not { Kind: ConfigTokenKind.Quoted, Closed: true } || !command.Comments.IsEmpty)
                return null;
            string name = tokens[1].Text.ToLowerInvariant();
            switch (tokens[0].Text)
            {
                case "bind" when lastName is null && KeyNumber(tokens[1].Text) is int key && key > lastKey &&
                                 tokens[2].Value.Length <= 1023:
                    lastKey = key;
                    break;
                case "seta" when ValidName(tokens[1].Text) && string.CompareOrdinal(name, lastName) > 0 &&
                                 !tokens[2].Value.Contains((byte)'"'):
                    lastName = name;
                    break;
                default:
                    return null;
            }
            byte[] exact = [.. Ascii($"{tokens[0].Text} {tokens[1].Text} "), .. tokens[2].Span.Slice(source)];
            if (!command.Span.Slice(source).SequenceEqual(exact))
                return null;
        }
        return rows;
    }

    private byte[] PreferredLineEnding()
    {
        var active = bytes.AsSpan()[..activeEnd];
        int ending = active.IndexOfAny((byte)10, (byte)13);
        if (ending < 0 || active[ending] == 10)
            return [10];
        return active[ending..].StartsWith("\r\n"u8) ? [13, 10] : [13];
    }

    private (ImmutableArray<byte>? Value, int? Index) BindingState(int key)
    {
        for (int index = Commands.Length - 1; index >= 0; index--)
        {
            var command = Commands[index];
            var tokens = command.Tokens;
            if (command.Kind == ConfigCommandKind.Unbindall ||
                tokens.Length == 2 && command.Kind == ConfigCommandKind.Unbind && KeyNumber(tokens[1].Text, folding: false) == key)
                break;
            if (tokens.Length >= 3 && command.Kind == ConfigCommandKind.Bind && KeyNumber(tokens[1].Text) == key)
                return (Combine(tokens, 2, binding: true), index);
        }
        return (null, null);
    }

    private static ImmutableArray<byte> Combine(ImmutableArray<ConfigToken> tokens, int first, bool binding)
    {
        int limit = binding ? 1023 : 4095;
        if (binding && tokens.Length == 3)
            return tokens[2].Value[..Math.Min(tokens[2].Value.Length, limit)];

        List<byte> result = [];
        int valueBytes = 0;
        for (int index = first; index < tokens.Length; index++)
        {
            var value = tokens[index].Value;
            valueBytes += value.Length;
            if (!binding && valueBytes > 4093)
                break;
            bool quoted = binding && value.Contains((byte)' ');
            if (quoted)
                result.Add((byte)'"');
            result.AddRange(value);
            if (quoted)
                result.Add((byte)'"');
            if (index + 1 < tokens.Length)
                result.Add((byte)' ');
        }
        return ImmutableArray.Create(result.ToArray().AsSpan(0, Math.Min(result.Count, limit)));
    }

    private static int AssignmentStart(ConfigCommand command, string name)
    {
        var tokens = command.Tokens;
        if (tokens.Length >= 3 && command.Kind == ConfigCommandKind.Setting && Equal(tokens[1].Text, name))
            return 2;
        return tokens.Length >= 2 && Equal(tokens[0].Text, name) ? 1 : -1;
    }

    private static bool SameValues(ConfigCommand command, byte[][] expected)
    {
        if (command.Tokens.Length != expected.Length)
            return false;
        for (int index = 0; index < expected.Length; index++)
            if (!command.Tokens[index].Value.AsSpan().SequenceEqual(expected[index]))
                return false;
        return true;
    }

    private static int ActiveLength(ReadOnlySpan<byte> input) => input.IndexOf((byte)0) is int end and >= 0 ? end : input.Length;
    private static bool IsWhitespace(byte value) => value is <= 32 or >= 128;
    private static bool AtPair(ReadOnlySpan<byte> bytes, int position, byte first, byte second) =>
        position + 1 < bytes.Length && bytes[position] == first && bytes[position + 1] == second;
    private static bool LineComment(ReadOnlySpan<byte> bytes, int position, ConfigDialect dialect) =>
        AtPair(bytes, position, (byte)'/', (byte)'/') && (dialect == ConfigDialect.Stock || position == 0 || bytes[position - 1] != ':');
    private static string CommandName(ConfigCommand command) => command.Tokens.IsEmpty ? "" : command.Tokens[0].Text.ToLowerInvariant();
    private static bool IsDynamic(ConfigCommand command) => CommandName(command) is "reset" or "toggle" or "togglep" or "setfromdvar";
    private static bool ChangesSetting(ConfigCommand command, string name) =>
        command.Tokens.Length > 1 && IsDynamic(command) && Equal(command.Tokens[1].Text, name);
    private static bool Equal(string left, string right) => left.ToLowerInvariant() == right.ToLowerInvariant();
    private static bool ValidName(string name) => name.Length > 0 && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static byte[] Ascii(string text) => Encoding.UTF8.GetBytes(text);
    private static string? Text(ImmutableArray<byte>? value) => value is { } bytes ? Encoding.Latin1.GetString(bytes.AsSpan()) : null;
    public static byte[] Encode(string text) => text.All(character => character <= 255)
        ? Encoding.Latin1.GetBytes(text) : throw new ConfigEditException(ConfigEditFailure.UnrepresentableText);

    public static int? KeyNumber(string name, bool folding = true)
    {
        if (name.Any(character => character > 255))
            return null;
        int? value = name.Length switch
        {
            1 => name[0] < 128 ? name[0] : null,
            4 when name.StartsWith("0x", StringComparison.Ordinal) => Hex(name[2]) * 16 + Hex(name[3]),
            _ => KeyNames.TryGetValue(name.ToLowerInvariant(), out int key) ? key : null
        };
        return folding && value is >= 65 and <= 90 ? value + 32 : value;
    }

    public static string KeySpelling(int key)
    {
        if (key is > 32 and < 127 and not (34 or 59))
            return ((char)key).ToString();
        foreach (var entry in KeyNames)
            if (entry.Value == key)
                return entry.Key;
        return $"0x{key.ToString("x2", CultureInfo.InvariantCulture)}";
    }

    private static int Hex(char digit) => digit switch
    {
        >= '0' and <= '9' => digit - '0',
        >= 'a' and <= 'f' => digit - 'a' + 10,
        _ => 0
    };

    private static readonly Dictionary<string, int> KeyNames = BuildKeyNames();

    private static Dictionary<string, int> BuildKeyNames()
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["tab"] = 9, ["enter"] = 13, ["escape"] = 27, ["space"] = 32, ["backspace"] = 127,
            ["uparrow"] = 154, ["downarrow"] = 155, ["leftarrow"] = 156, ["rightarrow"] = 157,
            ["alt"] = 158, ["ctrl"] = 159, ["shift"] = 160, ["capslock"] = 151,
            ["ins"] = 161, ["del"] = 162, ["pgdn"] = 163, ["pgup"] = 164, ["home"] = 165, ["end"] = 166,
            ["mwheelup"] = 206, ["mwheeldown"] = 205, ["pause"] = 153, ["semicolon"] = 59, ["command"] = 150
        };
        for (int index = 1; index <= 15; index++)
            names[FormattableString.Invariant($"f{index}")] = 166 + index;
        for (int index = 1; index <= 5; index++)
            names[FormattableString.Invariant($"mouse{index}")] = 199 + index;
        for (int index = 1; index <= 16; index++)
            names[FormattableString.Invariant($"aux{index}")] = 206 + index;
        string[] keypad = ["home", "uparrow", "pgup", "leftarrow", "5", "rightarrow", "end", "downarrow",
            "pgdn", "enter", "ins", "del", "slash", "minus", "plus", "numlock", "star", "equals"];
        int[] extended = [181, 191, 223, 224, 225, 228, 229, 230, 231, 232, 233, 236, 241, 242, 243, 246, 248, 249, 250, 252];
        for (int index = 0; index < keypad.Length; index++)
            names["kp_" + keypad[index]] = 182 + index;
        for (int index = 0; index < extended.Length; index++)
            names[extended[index].ToString(CultureInfo.InvariantCulture)] = 128 + index;
        return names;
    }

    private sealed record Edit(ByteSpan Span, byte[] Replacement)
    {
        public int Change => Replacement.Length - Span.Length;
    }
    private sealed record CanonicalLine(ByteSpan Span, int Index);
}
