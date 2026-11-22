using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace CoD2Unbound.Core.Tests;

internal sealed class MemoryPlatform(string root) :
    ISettingsFolder, ISecretStore, IGameFiles, IGameDiscovery, IProcessSupervisor, IExternalLinks, IFileAssociations
{
    private readonly Dictionary<string, ImmutableArray<byte>> files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);
    private readonly Dictionary<int, MemoryProcess> processes = new();
    private int nextPid;
    public string Root { get; } = root;
    public ImmutableArray<GameInstallation> Installations { get; set; } = [];
    public Uri? OpenedLink { get; private set; }
    public Queue<LauncherActivation> Activations { get; } = new();

    public void PutFile(string name) => files[name] = [];
    public bool IsRegularFile(string path, string relativePath) => path == Root && files.ContainsKey(relativePath);

    public ValueTask<ImmutableArray<byte>?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ImmutableArray<byte>?>(files.TryGetValue(name, out var value) ? value : null);
    }

    public ValueTask WriteAsync(string name, ImmutableArray<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        files[name] = data;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        files.Remove(name);
        return ValueTask.CompletedTask;
    }

    ValueTask<string?> ISecretStore.ReadAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(secrets.GetValueOrDefault(name));
    }

    ValueTask ISecretStore.WriteAsync(string name, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null)
            secrets.Remove(name);
        else
            secrets[name] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ImmutableArray<GameInstallation>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Installations);
    }

    public ValueTask<IEngineProcess> StartAsync(EngineProcessStart start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (start.HomeDirectory != Root || start.PrivateFiles.Any(file => files.ContainsKey(file.RelativePath)))
            throw new InvalidOperationException("Unexpected engine home or existing launch file.");
        foreach (var file in start.PrivateFiles)
            files.Add(file.RelativePath, file.Data);
        var process = new MemoryProcess(++nextPid, id =>
        {
            processes.Remove(id);
            foreach (var file in start.PrivateFiles)
                files.Remove(file.RelativePath);
        });
        processes.Add(process.Id, process);
        return ValueTask.FromResult<IEngineProcess>(process);
    }

    public ValueTask<ImmutableArray<int>> FindEngineProcessesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ImmutableArray<int>>([.. processes.Keys]);
    }

    public ValueTask OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenedLink = uri;
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<LauncherActivation> ReadActivationsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        while (Activations.TryDequeue(out var activation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return activation;
        }
    }

    private sealed class MemoryProcess(int id, Action<int> stop) : IEngineProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id { get; } = id;
        public Task<int> Exited => completion.Task;

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!completion.Task.IsCompleted)
            {
                stop(Id);
                completion.SetResult(0);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
    }
}
