using System.Collections.Immutable;

namespace CoD2Unbound.Core;

internal sealed class ConfigImportGraph(IConfigImportCatalog catalog)
{
    private const int MaximumCommands = 8192;
    private const int MaximumCopyValues = 65_536;
    private readonly List<(string Destination, string Source)> copies = [];
    private readonly HashSet<ImmutableArray<byte>> visited = new(ConfigScriptBytes.Instance);
    private int count;

    internal Dictionary<string, HashSet<ImmutableArray<byte>>> Values { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, List<ConfigImportLocation>> Origins { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Referenced { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> ArchivedNames { get; } = new(StringComparer.Ordinal);
    internal bool Limited { get; private set; }

    internal void Build(ImmutableArray<ConfigImportFile> files)
    {
        foreach (var file in files)
            Collect(file.Document, file.Path, [], 0);

        // A bare write can precede a helper's explicit definition. Revisit with all names known.
        visited.Clear();
        count = 0;
        foreach (var file in files)
            Collect(file.Document, file.Path, [], 0);

        ResolveCopies();
    }

    private void Collect(ConfigDocument document, string file, ImmutableArray<string> context, int depth)
    {
        if (depth > 16 || count >= MaximumCommands)
        {
            Limited = true;
            return;
        }
        if (!visited.Add(document.Serialized))
            return;

        foreach (var command in document.Commands)
        {
            var tokens = command.Tokens;
            if (command.Kind != ConfigCommandKind.Setting || tokens.Length < 3)
                continue;
            string name = tokens[1].Text.ToLowerInvariant();
            ValuesFor(name);
            if (tokens[0].Text.Equals("seta", StringComparison.OrdinalIgnoreCase))
                ArchivedNames.Add(name);
        }

        foreach (var command in document.Commands)
        {
            if (++count >= MaximumCommands)
            {
                Limited = true;
                return;
            }
            var tokens = command.Tokens;
            var location = new ConfigImportLocation(file, context, command.Span);
            if (ConfigImportScript.Assignment(command, document, catalog, Values.ContainsKey) is { } assignment)
            {
                ValuesFor(assignment.Name).Add(assignment.Value);
                var origins = OriginsFor(assignment.Name);
                if (!origins.Any(origin => SameLocation(origin, location)))
                    origins.Add(location);
                if (ConfigImportScript.LooksExecutable(assignment.Value, document.Dialect))
                    Collect(new(assignment.Value.AsSpan(), document.Dialect), file, context.Add("variable:" + assignment.Name), depth + 1);
            }
            else if (command.Kind == ConfigCommandKind.Bind && ConfigImportScript.Binding(command, document) is { } binding)
            {
                Collect(new(binding.AsSpan(), document.Dialect), file, context.Add("bind:" + tokens[1].Text), depth + 1);
            }

            if (tokens.IsEmpty)
                continue;
            string first = tokens[0].Text.ToLowerInvariant();
            if (tokens.Length == 2 && command.Kind == ConfigCommandKind.Vstr)
                Referenced.Add(tokens[1].Text.ToLowerInvariant());
            if (tokens.Length == 3 && first == "setfromdvar")
            {
                string destination = tokens[1].Text.ToLowerInvariant();
                copies.Add((destination, tokens[2].Text.ToLowerInvariant()));
                OriginsFor(destination).Add(location);
            }
            if (tokens.Length > 2 && first is "toggle" or "togglep")
            {
                var variants = ValuesFor(tokens[1].Text.ToLowerInvariant());
                foreach (var token in tokens.AsSpan()[2..])
                    variants.Add(token.Value);
            }
        }
    }

    private void ResolveCopies()
    {
        int valueCount = Values.Values.Sum(set => set.Count);
        for (int pass = 0; pass < 32; pass++)
        {
            bool changed = false;
            foreach (var (destination, source) in copies)
            {
                var target = ValuesFor(destination);
                if (destination == source || !Values.TryGetValue(source, out var values))
                    continue;
                foreach (var value in values)
                {
                    if (!target.Add(value))
                        continue;
                    changed = true;
                    if (++valueCount > MaximumCopyValues)
                    {
                        Limited = true;
                        return;
                    }
                }
            }
            if (!changed)
                return;
        }
        Limited = true;
    }

    private HashSet<ImmutableArray<byte>> ValuesFor(string name)
    {
        if (!Values.TryGetValue(name, out var values))
            Values[name] = values = new(ConfigScriptBytes.Instance);
        return values;
    }

    private List<ConfigImportLocation> OriginsFor(string name)
    {
        if (!Origins.TryGetValue(name, out var origins))
            Origins[name] = origins = [];
        return origins;
    }

    private static bool SameLocation(ConfigImportLocation left, ConfigImportLocation right) =>
        left.Span == right.Span && left.File == right.File && left.Context.AsSpan().SequenceEqual(right.Context.AsSpan());
}
