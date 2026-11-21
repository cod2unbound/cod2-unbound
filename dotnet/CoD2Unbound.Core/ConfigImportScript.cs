using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

internal static class ConfigImportScript
{
    private static readonly HashSet<string> ReservedCommands = new(StringComparer.Ordinal)
    {
        "toggle", "togglep", "reset", "setfromdvar", "increase", "decrease", "wait",
        "connect", "reconnect", "disconnect", "quit", "rcon", "openscriptmenu", "match", "mr", "say", "say_team", "tell",
        "cmd", "callvote", "vote", "voicechat", "voiceteamchat", "mp_quickmessage", "writeconfig", "writedefaults",
        "condump", "record", "screenshot", "screenshotjpeg"
    };

    internal static (string Name, ImmutableArray<byte> Value)? Assignment(ConfigCommand command, ConfigDocument document,
        IConfigImportCatalog catalog, Func<string, bool> isKnown)
    {
        var tokens = command.Tokens;
        bool explicitlySet = command.Kind == ConfigCommandKind.Setting;
        if (tokens.Length < (explicitlySet ? 3 : 2))
            return null;
        string name = tokens[explicitlySet ? 1 : 0].Text.ToLowerInvariant();
        if (!explicitlySet && (command.Kind != ConfigCommandKind.Unknown || catalog.Command(name, document.Dialect) is not null ||
            ReservedCommands.Contains(name) || (catalog.Setting(name, document.Dialect) is null && !isKnown(name))))
            return null;

        var single = new ConfigDocument(command.Span.Slice(document.Serialized.AsSpan()), document.Dialect);
        return single.EffectiveSettingBytes(name) is { } value ? (name, value) : null;
    }

    internal static ImmutableArray<byte>? Binding(ConfigCommand command, ConfigDocument document) =>
        command.Tokens.Length < 3 ? null :
        new ConfigDocument(command.Span.Slice(document.Serialized.AsSpan()), document.Dialect).EffectiveBindBytes(command.Tokens[1].Text);

    internal static bool BindingWasTruncated(ConfigCommand command)
    {
        var arguments = command.Tokens.AsSpan()[2..];
        if (arguments.Length == 1)
            return arguments[0].Value.Length > 1023;

        int length = Math.Max(0, arguments.Length - 1);
        foreach (var argument in arguments)
            length += argument.Value.Length + (argument.Value.Contains((byte)' ') ? 2 : 0);
        return length > 1023;
    }

    internal static bool LooksExecutable(ImmutableArray<byte> bytes, ConfigDialect dialect)
    {
        var document = new ConfigDocument(bytes.AsSpan(), dialect);
        var populated = document.Commands.Where(command => !command.Tokens.IsEmpty).Take(2).ToArray();
        if (populated.Length != 1)
            return populated.Length > 1;

        var command = populated[0];
        string name = command.Tokens[0].Text.ToLowerInvariant();
        return name.StartsWith('+') || name.StartsWith('-') ||
            command.Kind is ConfigCommandKind.Setting or ConfigCommandKind.Bind or ConfigCommandKind.Exec or ConfigCommandKind.Vstr or
                ConfigCommandKind.Unbind or ConfigCommandKind.Unbindall ||
            name is "connect" or "reconnect" or "quit" or "rcon" or "openscriptmenu" or "match" or "mr" or "say" or "say_team" or
                "wait" or "toggle" or "togglep" or "setfromdvar" or "writeconfig";
    }

    internal static string? ExecPath(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 63)
            return null;
        foreach (byte value in bytes)
            if (value is < 32 or >= 127)
                return null;
        string name = Text(bytes).Replace('\\', '/');
        if (name.StartsWith('/') || name.EndsWith('/') || name.Contains("..", StringComparison.Ordinal) ||
            name.IndexOfAny([':', '%', '?', '#']) >= 0)
            return null;
        string[] segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Contains("."))
            return null;

        string normalized = string.Join('/', segments);
        if (!segments[^1].Contains('.'))
            normalized += ".cfg";
        return normalized.Length <= 63 ? normalized : null;
    }

    internal static string Text(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);
}

internal sealed class ConfigScriptBytes : IEqualityComparer<ImmutableArray<byte>>, IComparer<ImmutableArray<byte>>
{
    internal static ConfigScriptBytes Instance { get; } = new();

    public bool Equals(ImmutableArray<byte> left, ImmutableArray<byte> right) => left.AsSpan().SequenceEqual(right.AsSpan());

    public int GetHashCode(ImmutableArray<byte> bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes.AsSpan());
        return hash.ToHashCode();
    }

    public int Compare(ImmutableArray<byte> left, ImmutableArray<byte> right) => left.AsSpan().SequenceCompareTo(right.AsSpan());
}
