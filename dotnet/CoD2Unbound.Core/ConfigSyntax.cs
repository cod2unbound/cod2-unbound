using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public enum ConfigDialect { Stock, Cod2x }
public enum ConfigTokenKind { Bare, Quoted, Remainder }
public enum ConfigCommandKind { Setting, Bind, Unbind, Unbindall, Exec, Vstr, Comment, Empty, Unknown }
public enum ConfigTokenizationFailure { TooManyTokens, TokenBufferOverflow }
public enum ConfigDiagnosticKind { IgnoredNulSuffix, CommandBufferOverflow, LineLimit, TokenizationFailure, UnterminatedQuote }

public readonly record struct ByteSpan(int Start, int End)
{
    public int Length => End - Start;
    internal bool Overlaps(ByteSpan other) => Start < other.End && other.Start < End;
    internal ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> bytes) => bytes[Start..End];
}

public sealed record ConfigToken(ByteSpan Span, ImmutableArray<byte> Value, ConfigTokenKind Kind, bool Closed)
{
    public string Text => Encoding.Latin1.GetString(Value.AsSpan());
}

public sealed record ConfigTokenization(ImmutableArray<ConfigToken> Tokens, ImmutableArray<ByteSpan> Comments,
    ConfigTokenizationFailure? Failure);

public sealed record ConfigCommand(ByteSpan Span, ByteSpan Separator, ConfigTokenization Syntax, bool SplitAtLimit)
{
    public ImmutableArray<ConfigToken> Tokens => Syntax.Tokens;
    public ImmutableArray<ByteSpan> Comments => Syntax.Comments;
    public ConfigCommandKind Kind => Tokens.IsEmpty
        ? Comments.IsEmpty ? ConfigCommandKind.Empty : ConfigCommandKind.Comment
        : Tokens[0].Text.ToLowerInvariant() switch
        {
            "set" or "seta" or "sets" or "setu" => ConfigCommandKind.Setting,
            "bind" => ConfigCommandKind.Bind,
            "unbind" => ConfigCommandKind.Unbind,
            "unbindall" => ConfigCommandKind.Unbindall,
            "exec" => ConfigCommandKind.Exec,
            "vstr" => ConfigCommandKind.Vstr,
            _ => ConfigCommandKind.Unknown
        };
}

public sealed record ConfigDiagnostic(ConfigDiagnosticKind Kind, ByteSpan? Span = null, ByteSpan? Discarded = null,
    ConfigTokenizationFailure? Failure = null);

public enum ConfigEditFailure
{
    InvalidSettingName, InvalidKey, UnrepresentableText, UnsafeValue, UnsafeBinding,
    BindTooLong, CommandTooLong, CommandBufferOverflow, LossyEdit
}

public sealed class ConfigEditException(ConfigEditFailure failure) : Exception(failure switch
{
    ConfigEditFailure.InvalidSettingName => "Setting names must contain only ASCII letters, numbers and underscores.",
    ConfigEditFailure.InvalidKey => "This key name is not supported by the game.",
    ConfigEditFailure.UnrepresentableText => "The game uses single-byte text. This text contains a character it cannot preserve.",
    ConfigEditFailure.UnsafeValue => "Setting values cannot contain quotes, line breaks or NUL bytes, or end in a backslash.",
    ConfigEditFailure.UnsafeBinding => "This binding cannot be quoted without changing the commands the game reads.",
    ConfigEditFailure.BindTooLong => "Bindings can contain at most 1023 bytes.",
    ConfigEditFailure.CommandTooLong => "This command exceeds the game's safe line length of 4094 bytes.",
    ConfigEditFailure.CommandBufferOverflow => "This config exceeds the game's command buffer of 65535 bytes.",
    _ => "This edit would change other commands or discard part of the existing config."
})
{
    public ConfigEditFailure Failure { get; } = failure;
}
