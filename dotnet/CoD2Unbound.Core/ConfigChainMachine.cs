using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public enum ConfigChainProblem
{
    UsesWait, WaitDisabled, NonYieldingRecursion, YieldingLoop, ExecutionLimit, BufferLimit,
    UnresolvedVariable, UnresolvedFile, InvalidTokens, NegativeWait, ReleaseTail, HeldActionWithoutRelease,
    SymbolicSetting, VstrTooLong
}
public sealed record ConfigChainIssue(ConfigChainProblem Problem, string? Name = null)
{
    public bool BlocksCompilation => Problem is ConfigChainProblem.NonYieldingRecursion or ConfigChainProblem.YieldingLoop or
        ConfigChainProblem.ExecutionLimit or ConfigChainProblem.BufferLimit or ConfigChainProblem.InvalidTokens or
        ConfigChainProblem.NegativeWait or ConfigChainProblem.VstrTooLong;
}
public sealed record ConfigChainTrace(ImmutableArray<ImmutableArray<ConfigToken>> Commands,
    ImmutableArray<ConfigChainIssue> Issues, bool Complete);

public sealed class ConfigChainMachine(ConfigDialect dialect, IReadOnlyDictionary<string, ImmutableArray<byte>>? files = null,
    bool waitDisabled = false)
{
    private const int BufferLimit = 65_536;
    private readonly Dictionary<string, ImmutableArray<byte>> variables = new(StringComparer.Ordinal);
    private readonly HashSet<string> referenced = new(StringComparer.Ordinal);
    private readonly HashSet<string> archived = new(StringComparer.Ordinal);
    private readonly HashSet<string> unsafeArchived = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ImmutableArray<byte>> Variables => variables;
    public IReadOnlySet<string> ReferencedVariables => referenced;
    public IReadOnlySet<string> ArchivedVariables => archived;
    public IReadOnlySet<string> UnsafeArchivedVariables => unsafeArchived;

    private sealed record Frame(ImmutableArray<ConfigCommand> Commands, int Length, string? Label, string State, int Epoch)
    {
        public int Index { get; set; }
        public int Consumed => Index == 0 ? 0 : Commands[Index - 1].Separator.End;
    }

    public ConfigChainTrace KeyEvent(ImmutableArray<byte> binding, int key, bool down, uint time)
    {
        if (binding.IsEmpty || (!down && binding[0] != 43))
            return new([], [], true);
        bool held = binding[0] == 43;
        var text = binding.ToBuilder();
        if (held)
        {
            if (!down)
                text[0] = 45;
            text.AddRange(Encoding.ASCII.GetBytes(FormattableString.Invariant($" {key} {unchecked((int)time)}\n")));
            if (text.Count > 1023)
                text.Count = 1023;
        }
        else
            text.Add(10);
        var trace = Execute(text.ToImmutable());
        var issues = trace.Issues.ToBuilder();
        if (held && binding.Contains((byte)59))
            issues.Add(new(ConfigChainProblem.ReleaseTail));
        if (trace.Commands.Skip(held ? 1 : 0).Any(tokens => tokens[0].Value is [43, ..]))
            issues.Add(new(ConfigChainProblem.HeldActionWithoutRelease));
        return trace with { Issues = issues.ToImmutable() };
    }

    public ConfigChainTrace Execute(ImmutableArray<byte> bytes)
    {
        if (bytes.Contains((byte)0))
            return new([], [new(ConfigChainProblem.InvalidTokens)], false);
        if (bytes.Length >= BufferLimit)
            return new([], [new(ConfigChainProblem.BufferLimit)], false);
        var commands = ImmutableArray.CreateBuilder<ImmutableArray<ConfigToken>>();
        var issues = ImmutableArray.CreateBuilder<ConfigChainIssue>();
        bool complete = true;
        void Flag(ConfigChainProblem problem, string? name = null, bool unresolved = false)
        {
            var issue = new ConfigChainIssue(problem, name);
            if (!issues.Contains(issue))
                issues.Add(issue);
            complete &= !issue.BlocksCompilation && !unresolved;
        }

        int epoch = 0, budget = 8192;
        var frames = new List<Frame> { Parse(bytes, null, "", epoch) };
        while (frames.Count > 0)
        {
            var frame = frames[^1];
            if (frame.Index == frame.Commands.Length)
            {
                frames.RemoveAt(frames.Count - 1);
                continue;
            }
            if (budget-- == 0 || frames.Count > 64)
            {
                Flag(ConfigChainProblem.ExecutionLimit);
                break;
            }

            var command = frame.Commands[frame.Index++];
            if (command.Syntax.Failure is not null || command.SplitAtLimit)
            {
                Flag(ConfigChainProblem.InvalidTokens);
                break;
            }
            var tokens = command.Tokens;
            if (tokens.IsEmpty)
                continue;
            string name = tokens[0].Text.ToLowerInvariant();
            string argument = tokens.Length >= 2 ? tokens[1].Text.ToLowerInvariant() : "";
            string? label = null;
            ImmutableArray<byte> expansion = default;
            if (name == "vstr" && tokens.Length == 2)
            {
                referenced.Add(argument);
                if (!variables.TryGetValue(argument, out var value))
                    Flag(ConfigChainProblem.UnresolvedVariable, argument, unresolved: true);
                else if (value.Length > 1022)
                {
                    Flag(ConfigChainProblem.VstrTooLong, argument);
                    break;
                }
                else
                {
                    label = "vstr:" + argument;
                    expansion = [.. value, 10, 10];
                }
            }
            else if (name == "exec" && tokens.Length == 2)
            {
                if (ConfigImportScript.ExecPath(tokens[1].Value.AsSpan()) is { } path && files?.TryGetValue(path, out var value) == true)
                {
                    label = "exec:" + path;
                    if (value.Length >= BufferLimit)
                    {
                        Flag(ConfigChainProblem.BufferLimit);
                        break;
                    }
                    expansion = [.. value, 10];
                }
                else
                    Flag(ConfigChainProblem.UnresolvedFile, tokens[1].Text, unresolved: true);
            }
            else
            {
                commands.Add(tokens);
                if (command.Kind == ConfigCommandKind.Setting && tokens.Length >= 3)
                {
                    variables[argument] = ConfigDocument.Combine(tokens, 2, binding: false);
                    if (name == "seta")
                        archived.Add(argument);
                }
                else if (name is "toggle" or "togglep" && tokens.Length >= 3)
                {
                    string? current = variables.TryGetValue(argument, out var value) ? Encoding.Latin1.GetString(value.AsSpan()) : null;
                    int next = 2;
                    for (int index = 2; index < tokens.Length - 1; index++)
                    {
                        if (tokens[index].Text.Equals(current ?? "", StringComparison.OrdinalIgnoreCase))
                        {
                            next = index + 1;
                            break;
                        }
                    }
                    variables[argument] = tokens[next].Value;
                    Flag(ConfigChainProblem.SymbolicSetting, argument);
                }
                else if (name == "setfromdvar" && tokens.Length == 3)
                {
                    string source = tokens[2].Text.ToLowerInvariant();
                    referenced.Add(source);
                    if (variables.TryGetValue(source, out var value))
                        variables[argument] = value;
                    else
                        Flag(ConfigChainProblem.UnresolvedVariable, source, unresolved: true);
                }
                else if (name == "wait")
                {
                    Flag(ConfigChainProblem.UsesWait);
                    if (waitDisabled && dialect == ConfigDialect.Cod2x)
                        Flag(ConfigChainProblem.WaitDisabled);
                    else
                    {
                        int count = tokens.Length == 2 ? WaitCount(tokens[1].Value.AsSpan()) : 1;
                        if (count < 0)
                        {
                            Flag(ConfigChainProblem.NegativeWait);
                            break;
                        }
                        if (count > 0)
                        {
                            epoch++;
                            RecordArchivedValues();
                        }
                    }
                }
                else if (variables.ContainsKey(name) && tokens.Length > 1)
                    variables[name] = ConfigDocument.Combine(tokens, 1, binding: false);
            }

            if (label is null)
                continue;
            string state = State();
            if (frames.LastOrDefault(candidate => candidate.Label == label && candidate.State == state) is { } previous)
            {
                Flag(previous.Epoch == epoch ? ConfigChainProblem.NonYieldingRecursion : ConfigChainProblem.YieldingLoop);
                break;
            }
            if (frames.Sum(candidate => candidate.Length - candidate.Consumed) + expansion.Length > BufferLimit)
            {
                Flag(ConfigChainProblem.BufferLimit);
                break;
            }
            frames.Add(Parse(expansion, label, state, epoch));
        }
        RecordArchivedValues();
        return new(commands.ToImmutable(), issues.ToImmutable(), complete);
    }

    private Frame Parse(ImmutableArray<byte> value, string? label, string state, int epoch) =>
        new(new ConfigDocument(value.AsSpan(), dialect).Commands, value.Length, label, state, epoch);

    internal string State(bool includeArchived = false)
    {
        var output = new StringBuilder();
        foreach (var (name, value) in variables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            output.Append(name.Length).Append(':').Append(name).Append(value.Length).Append(':').Append(Encoding.Latin1.GetString(value.AsSpan()));
        if (includeArchived)
        {
            output.Append('|');
            foreach (string key in archived.Order(StringComparer.Ordinal))
                output.Append(key.Length).Append(':').Append(key);
        }
        return output.ToString();
    }

    private void RecordArchivedValues()
    {
        foreach (string name in archived)
            if (variables.TryGetValue(name, out var value) && (value.Contains((byte)34) || value.LastOrDefault() == 92))
                unsafeArchived.Add(name);
    }

    private static int WaitCount(ReadOnlySpan<byte> text)
    {
        while (!text.IsEmpty && text[0] <= 32)
            text = text[1..];
        bool negative = !text.IsEmpty && text[0] == 45;
        if (!text.IsEmpty && text[0] is 43 or 45)
            text = text[1..];
        int number = 0;
        foreach (byte digit in text)
        {
            if (digit is < 48 or > 57)
                break;
            number = Math.Min(1_000_000, number * 10 + digit - 48);
        }
        return negative ? -number : number;
    }
}
