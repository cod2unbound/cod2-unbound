using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsHosting
{
    private const string ServersFile = "hosted-servers.json";
    private readonly string root;
    private readonly WindowsEngineInventory inventory;
    private readonly WindowsSettingsFolder folder;
    private readonly HostingSession session;
    private readonly SemaphoreSlim writes = new(1);
    internal bool LoopbackOnly { get; }
    internal ImmutableDictionary<string, HostedRunState> Running { get; private set; } =
        ImmutableDictionary.Create<string, HostedRunState>(StringComparer.OrdinalIgnoreCase);

    internal WindowsHosting(string home, WindowsEngineInventory inventory, IGameFiles gameFiles, bool loopbackOnly)
    {
        this.inventory = inventory;
        LoopbackOnly = loopbackOnly;
        root = Path.Join(home, "hosting");
        folder = new(root);
        var processes = new WindowsProcessSupervisor(inventory, "", logInEngineHome: true);
        var endpoint = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        session = new HostingSession(processes, new HostingFiles(), gameFiles, new WindowsUdpTransport(loopbackOnly: true),
            TimeProvider.System, port => Available(endpoint, port), endpoint.ToString());
    }

    internal async Task<ImmutableArray<HostedServer>> ReadAsync() =>
        await folder.ReadAsync(ServersFile, default) is { } bytes ? HostedServerJson.Read(bytes.AsSpan()) : [];

    internal bool IsActive(string id) => Running.TryGetValue(id, out var state) && IsActive(state);
    internal bool HasActiveServers => Running.Values.Any(IsActive);

    private static bool IsActive(HostedRunState state) =>
        state.Phase is HostedPhase.Starting or HostedPhase.Running or HostedPhase.Stopping;

    internal async Task SaveAsync(HostedServer server)
    {
        await writes.WaitAsync();
        try
        {
            await using var lease = await WindowsGameSessionHost.OpenLeaseAsync(root, ".hosted-servers.lock", default);
            if (IsActive(server.Id))
                throw new IOException("Stop the server before changing its settings.");
            var saved = await ReadAsync();
            var previous = saved.FirstOrDefault(item => item.Id.Equals(server.Id, StringComparison.OrdinalIgnoreCase));
            int index = previous is null ? -1 : saved.IndexOf(previous);
            await folder.WriteAsync(ServersFile, HostedServerJson.Write(index >= 0 ? saved.SetItem(index, server) : saved.Add(server)), default);
        }
        finally { writes.Release(); }
    }

    internal async Task StartAsync(HostedServer server, string data)
    {
        if (LoopbackOnly)
            server = server with { Reach = HostedReach.Network };
        var binary = inventory.Installed.FirstOrDefault(item => item.Role == EngineRole.Dedicated && item.Edition == server.Engine);
        await using (await WindowsGameSessionHost.OpenLeaseAsync(root, ".hosting-start.lock", default))
            await session.StartAsync(server, binary, data, root);
        await RefreshAsync();
    }

    internal async Task StopAsync(string id)
    {
        await session.StopAsync(id);
        await RefreshAsync();
    }

    internal async Task StopAllAsync()
    {
        await session.StopAllAsync();
        await RefreshAsync();
    }

    internal async Task PollAsync()
    {
        await session.PollAsync();
        await RefreshAsync();
    }

    private async Task RefreshAsync() => Running = await session.SnapshotAsync();

    internal Task<string?> ReadLogAsync(string id)
    {
        string server = WindowsFiles.Under(root, "servers/" + id);
        return WindowsLauncherSession.ReadLatestLogAsync(Path.Join(server, "logs"));
    }

    private static bool Available(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException) { return false; }
    }

    private sealed class HostingFiles : IHostingFiles
    {
        public bool HasPackage(ZpamLine line) => false;

        public ValueTask<string?> PrepareAsync(string serverId, string home, ZpamLine? package, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var directory = new WindowsFiles.DirectoryHandles(Path.Combine(home, "main"), create: true);
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<string> PublishExportAsync(string parent, string baseName, ImmutableDictionary<string, string> files,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
