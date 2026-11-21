using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public interface ISettingsFolder
{
    // Names are relative to the user-owned launcher settings root. Writes are private and atomic.
    string Root { get; }
    ValueTask<ImmutableArray<byte>?> ReadAsync(string name, CancellationToken cancellationToken);
    ValueTask WriteAsync(string name, ImmutableArray<byte> data, CancellationToken cancellationToken);
    ValueTask DeleteAsync(string name, CancellationToken cancellationToken);
}

public interface ISecretStore
{
    // Names are scoped to this launcher. A null value removes a secret.
    ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken);
    ValueTask WriteAsync(string name, string? value, CancellationToken cancellationToken);
}

public interface IGameFiles
{
    // True only for a readable regular file contained under root, without following symlinks or junctions.
    bool IsRegularFile(string root, string relativePath);
}

public enum EngineRole { Client, Dedicated }
public sealed record GameBinary(GameEdition Edition, EngineRole Role, string Path);
public sealed record GameInstallation(string DataPath, ImmutableArray<GameBinary> Binaries);

public interface IGameDiscovery
{
    ValueTask<ImmutableArray<GameInstallation>> DiscoverAsync(CancellationToken cancellationToken);
}

public sealed record PrivateLaunchFile(string RelativePath, ImmutableArray<byte> Data);
public sealed record EngineProcessStart(string Executable, string WorkingDirectory, string HomeDirectory,
    ImmutableArray<string> EngineArguments,
    ImmutableArray<PrivateLaunchFile> PrivateFiles)
{
    public ImmutableArray<string> LogSecrets { get; init; } = [];
    // Null removes an inherited value before the native process launches.
    public ImmutableDictionary<string, string?> EnvironmentChanges { get; init; } = ImmutableDictionary<string, string?>.Empty;
    public override string ToString() => "Engine process start";
}

public interface IProcessSupervisor
{
    // Publish launch-only files privately in the engine's home before spawn, and remove them on failure and exit.
    // Cancellation must not leave an unmanaged owned child. Retain stdout/stderr with credential redaction.
    ValueTask<IEngineProcess> StartAsync(EngineProcessStart start, CancellationToken cancellationToken);
    ValueTask<ImmutableArray<int>> FindEngineProcessesAsync(CancellationToken cancellationToken);
}

public interface IEngineProcess : IAsyncDisposable
{
    int Id { get; }
    Task<int> Exited { get; }
    // Complete after the owned child exits. The adapter bounds graceful shutdown before forcing it.
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IExternalLinks
{
    ValueTask OpenAsync(Uri uri, CancellationToken cancellationToken);
}

public enum LauncherActivationKind { ConnectLink, ConfigFile, DemoFile }
public sealed record LauncherActivation(LauncherActivationKind Kind, string Value);

public interface IFileAssociations
{
    // Supply initial and forwarded activations in delivery order; the core validates their contents.
    IAsyncEnumerable<LauncherActivation> ReadActivationsAsync(CancellationToken cancellationToken);
}
