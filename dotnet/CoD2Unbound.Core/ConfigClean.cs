using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public sealed record ConfigCleanLeftOut(ByteSpan Span, ImmutableArray<byte> Text, ImmutableArray<string> Reasons,
    ImmutableArray<string> Tokens);
public sealed record ConfigCleanResult(ImmutableArray<byte> Bytes, int SettingLines, int Binds, int Kept, int Comments,
    ImmutableArray<ConfigCleanLeftOut> LeftOut, ImmutableDictionary<string, string> Settings,
    ImmutableDictionary<string, int> Reasons);

public enum ConfigCleanFailure { Unreadable, DifferentReadings }
public sealed class ConfigCleanException(ConfigCleanFailure failure) : Exception(failure.ToString())
{
    public ConfigCleanFailure Failure { get; } = failure;
}

/// <summary>Projects import review into portable setup text for catalogue and Share.</summary>
public static partial class ConfigClean
{
    private static readonly HashSet<ConfigImportRisk> LeftOutRisks =
    [
        ConfigImportRisk.Identity, ConfigImportRisk.Chat, ConfigImportRisk.Credential, ConfigImportRisk.FilesystemPath,
        ConfigImportRisk.RemoteConsole, ConfigImportRisk.Connect, ConfigImportRisk.Quit, ConfigImportRisk.UnknownSetting,
        ConfigImportRisk.InertName, ConfigImportRisk.ProtectedSetting, ConfigImportRisk.Malformed, ConfigImportRisk.Limit,
        ConfigImportRisk.Cycle, ConfigImportRisk.NegativeWait, ConfigImportRisk.UnresolvedExec, ConfigImportRisk.UnresolvedVariable
    ];
    private static readonly string[] HostingPrefixes = ["sv_", "g_", "scr_", "ui_", "net_socks", "rcon"];

    public static bool Hosting(string name) => HostingPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static async ValueTask<ConfigCleanResult> CleanAsync(ImmutableArray<byte> source, IConfigImportCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var verdicts = new SortedDictionary<int, Verdict>();
        var loader = new ConfigImportLoader(catalog);
        var safety = new ConfigImportSafety(catalog);
        foreach (var dialect in new[] { ConfigDialect.Stock, ConfigDialect.Cod2x })
        {
            var bundle = await loader.LoadAsync(new ConfigImportInput.Paste(source), dialect, cancellationToken).ConfigureAwait(false);
            var entry = bundle.Files.FirstOrDefault(file => file.Path == bundle.EntryPath) ??
                throw new ConfigCleanException(ConfigCleanFailure.Unreadable);
            if (!source.AsSpan().SequenceEqual(entry.Document.Serialized.AsSpan()))
                throw new ConfigCleanException(ConfigCleanFailure.DifferentReadings);

            foreach (var candidate in safety.Review(bundle).Candidates)
            {
                if (candidate.Location.File != bundle.EntryPath || !candidate.Location.Context.IsEmpty)
                    continue;

                var span = candidate.Location.Span;
                if (!verdicts.TryGetValue(span.Start, out var verdict))
                    verdicts[span.Start] = verdict = new(span, candidate.Command);
                if (span != verdict.Span)
                    throw new ConfigCleanException(ConfigCleanFailure.DifferentReadings);
                Consider(candidate, verdict);
            }
        }

        using var output = new MemoryStream(source.Length);
        var leftOut = ImmutableArray.CreateBuilder<ConfigCleanLeftOut>();
        var reasons = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var settingsNames = new HashSet<string>(StringComparer.Ordinal);
        int settingsCount = 0, binds = 0, kept = 0, comments = 0;
        foreach (var verdict in verdicts.Values)
        {
            if (verdict.Comment)
            {
                comments++;
                continue;
            }
            var command = verdict.Command;
            var text = verdict.Span.Slice(source.AsSpan());
            if (!verdict.Keep)
            {
                var rejected = verdict.Reasons.Count == 0 ? ["notImportable"] : verdict.Reasons.Order(StringComparer.Ordinal).ToImmutableArray();
                leftOut.Add(new(verdict.Span, ImmutableArray.Create(text), rejected, command.Tokens.Select(token => token.Text).ToImmutableArray()));
                foreach (string reason in rejected)
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                continue;
            }

            output.Write(text);
            output.WriteByte(10);
            kept++;
            if (command.Kind == ConfigCommandKind.Bind)
                binds++;
            if (command.Kind == ConfigCommandKind.Setting)
            {
                settingsCount++;
                if (command.Tokens.Length > 1)
                    settingsNames.Add(command.Tokens[1].Text.ToLowerInvariant());
            }
        }

        byte[] cleaned = output.ToArray();
        // A dropped assignment must not put its value back into the cleaned metadata.
        var cleanedDocument = new ConfigDocument(cleaned, ConfigDialect.Stock);
        var settings = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (string name in settingsNames)
            if (cleanedDocument.EffectiveSetting(name) is { } value)
                settings[name] = value;

        return new(ImmutableArray.Create(cleaned), settingsCount, binds, kept, comments, leftOut.ToImmutable(),
            settings.ToImmutable(), reasons.ToImmutable());
    }

    private static void Consider(ConfigImportCandidate candidate, Verdict verdict)
    {
        var command = candidate.Command;
        if (command.Tokens.IsEmpty)
        {
            verdict.Comment = true;
            verdict.Keep = false;
        }
        else if (command.Kind is not (ConfigCommandKind.Setting or ConfigCommandKind.Bind or ConfigCommandKind.Unbind or ConfigCommandKind.Unbindall))
        {
            verdict.Keep = false;
            verdict.Reasons.Add("looseCommand");
        }

        if (candidate.Classification == ConfigImportClassification.Blocked || !candidate.Importable || candidate.Reasons.Any(LeftOutRisks.Contains))
            verdict.Keep = false;
        verdict.Reasons.UnionWith(candidate.Reasons.Select(reason => reason.Key()));

        if (command.Kind == ConfigCommandKind.Setting && command.Tokens.Length > 1 && Hosting(command.Tokens[1].Text))
        {
            verdict.Keep = false;
            verdict.Reasons.Add("hosting");
        }
        if (command.Kind != ConfigCommandKind.Bind || command.Tokens.IsEmpty)
            return;
        var words = ScriptWords().Matches(command.Tokens[^1].Text.ToLowerInvariant()).Select(word => word.Value).ToArray();
        if (words.Any(Hosting))
        {
            verdict.Keep = false;
            verdict.Reasons.Add("hosting");
        }
        if (words.Contains("wait"))
        {
            verdict.Keep = false;
            verdict.Reasons.Add("macro");
        }
    }

    private sealed class Verdict(ByteSpan span, ConfigCommand command)
    {
        internal ByteSpan Span { get; } = span;
        internal ConfigCommand Command { get; } = command;
        internal bool Keep { get; set; } = true;
        internal bool Comment { get; set; }
        internal HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptWords();
}
