using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public enum ConfigImportClassification { Safe, NeedsOk, Blocked }

public enum ConfigImportRisk
{
    UnknownForwardedCommand, UnknownSetting, InertName, Credential, FilesystemPath, ProtectedSetting,
    Connect, Quit, RemoteConsole, Identity, Chat, FileWrite, UnbindAll, Network, SideEffect,
    UnresolvedExec, UnresolvedVariable, Cycle, Limit, Malformed, NegativeWait, Script, KeyRelease
}

public static class ConfigImportRiskText
{
    public static string Key(this ConfigImportRisk risk)
    {
        string name = risk.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static string Summary(this ConfigImportRisk risk) => risk switch
    {
        ConfigImportRisk.UnknownForwardedCommand => "Unknown command; the game may send it to the server",
        ConfigImportRisk.UnknownSetting => "Creates a helper variable or a setting we cannot verify",
        ConfigImportRisk.InertName => "Has no effect in CoD2",
        ConfigImportRisk.Credential => "Contains a password, key or login credential",
        ConfigImportRisk.FilesystemPath => "Contains a filesystem path",
        ConfigImportRisk.ProtectedSetting => "Protected by the game; cannot be imported as a setting",
        ConfigImportRisk.Connect => "Joins or leaves a server you did not choose",
        ConfigImportRisk.Quit => "Closes the game",
        ConfigImportRisk.RemoteConsole => "Sends an administrator command to a server",
        ConfigImportRisk.Identity => "Changes your name or player identity",
        ConfigImportRisk.Chat => "Sends chat or a response to other players",
        ConfigImportRisk.FileWrite => "Writes files on your Mac",
        ConfigImportRisk.UnbindAll => "Clears all your key bindings",
        ConfigImportRisk.Network => "Changes downloads, redirects or network destinations",
        ConfigImportRisk.SideEffect => "Changes the game session or restarts a subsystem",
        ConfigImportRisk.UnresolvedExec => "Runs another file that was not included or cannot be safely applied",
        ConfigImportRisk.UnresolvedVariable => "Runs a variable whose text cannot be determined",
        ConfigImportRisk.Cycle => "Repeats a script dependency; the game has no loop protection",
        ConfigImportRisk.Limit => "Exceeds the import or game parser limits",
        ConfigImportRisk.Malformed => "The game cannot read this command reliably",
        ConfigImportRisk.NegativeWait => "Can stall the game command buffer",
        ConfigImportRisk.Script => "Stores or runs a command chain",
        ConfigImportRisk.KeyRelease => "Extra commands also run when you release the key",
        _ => throw new ArgumentOutOfRangeException(nameof(risk))
    };
}

public sealed record ConfigImportLocation(string File, ImmutableArray<string> Context, ByteSpan Span);
public enum ConfigImportDependencyKind { Exec, Vstr }
public sealed record ConfigImportDependency(ConfigImportDependencyKind Kind, string Name, bool Resolved,
    ImmutableArray<ConfigImportLocation> Locations);
public sealed record ConfigImportEffect(ConfigImportLocation Location, ConfigCommand Command, ConfigImportClassification Classification,
    ImmutableArray<ConfigImportRisk> Reasons, string Details);

public sealed record ConfigImportCandidate(ConfigImportLocation Location, ConfigCommand Command,
    ConfigImportClassification Classification, bool Importable, ImmutableArray<ConfigImportRisk> Reasons,
    ImmutableArray<ConfigImportEffect> Effects, ImmutableArray<ConfigImportDependency> Dependencies, string Details)
{
    public string Id => FormattableString.Invariant($"{Location.File}:{Location.Span.Start}");
    public bool SelectedByDefault => Classification == ConfigImportClassification.Safe && Importable;
}

public sealed record ConfigImportReview(ConfigImportBundle Bundle, ImmutableArray<ConfigImportCandidate> Candidates);
