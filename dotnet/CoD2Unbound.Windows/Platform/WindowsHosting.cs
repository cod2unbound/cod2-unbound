using System.Collections.Immutable;
using System.Net;
using System.Net.NetworkInformation;
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
    private readonly WindowsHostingFiles files;
    private readonly SemaphoreSlim writes = new(1);
    internal bool LoopbackOnly { get; }
    internal ZpamDownload Zpam { get; }
    internal ImmutableDictionary<string, HostedRunState> Running { get; private set; } =
        ImmutableDictionary.Create<string, HostedRunState>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HostedServer> activeSettings = new(StringComparer.OrdinalIgnoreCase);

    internal WindowsHosting(string home, WindowsEngineInventory inventory, IGameFiles gameFiles, bool loopbackOnly)
    {
        this.inventory = inventory;
        LoopbackOnly = loopbackOnly;
        root = Path.Join(home, "hosting");
        folder = new(root);
        var processes = new WindowsProcessSupervisor(inventory, "", logInEngineHome: true);
        var endpoint = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        files = new(root, loopbackOnly);
        Zpam = new(files, files);
        session = new HostingSession(processes, files, gameFiles, new WindowsUdpTransport(loopbackOnly: true),
            TimeProvider.System, port => Available(endpoint, port), endpoint.ToString());
    }

    internal async Task<ImmutableArray<HostedServer>> ReadAsync() =>
        await folder.ReadAsync(ServersFile, default) is { } bytes ? HostedServerJson.Read(bytes.AsSpan()) : [];

    internal bool IsActive(string id) => Running.TryGetValue(id, out var state) && IsActive(state);
    internal bool HasActiveServers => Running.Values.Any(IsActive);
    internal HostedServer ActiveSettings(HostedServer server) => activeSettings.GetValueOrDefault(server.Id) ?? server;

    internal HostedRunState DashboardState(HostedRunState state)
    {
#if UPDATE_FIXTURES
        if (LoopbackOnly && Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_HOSTING_BOARD") == "full" &&
            state is { Phase: HostedPhase.Running, Status: { } status })
        {
            var roster = Enumerable.Range(0, status.MaxPlayers)
                .Select(index => new HostedStatusPlayer(index, $"Rifle {index + 1}", 0, 12 + index)).ToImmutableArray();
            return state with { Status = status with { Players = roster } };
        }
#endif
        return state;
    }

    private static bool IsActive(HostedRunState state) =>
        state.Phase is HostedPhase.Starting or HostedPhase.Running or HostedPhase.Stopping;

    internal async Task SaveAsync(HostedServer server)
    {
        await writes.WaitAsync();
        try
        {
            await using var lease = await WindowsGameSessionHost.OpenLeaseAsync(root, ".hosted-servers.lock", default);
            var saved = await ReadAsync();
            var previous = saved.FirstOrDefault(item => item.Id.Equals(server.Id, StringComparison.OrdinalIgnoreCase));
            int index = previous is null ? -1 : saved.IndexOf(previous);
            await folder.WriteAsync(ServersFile, HostedServerJson.Write(index >= 0 ? saved.SetItem(index, server) : saved.Add(server)), default);
        }
        finally { writes.Release(); }
    }

    internal async Task DeleteAsync(string id)
    {
        await writes.WaitAsync();
        try
        {
            await using var lease = await WindowsGameSessionHost.OpenLeaseAsync(root, ".hosted-servers.lock", default);
            if (IsActive(id))
                throw new IOException("Stop the server before deleting it.");
            var saved = await ReadAsync();
            await folder.WriteAsync(ServersFile, HostedServerJson.Write(
                [.. saved.Where(server => !server.Id.Equals(id, StringComparison.OrdinalIgnoreCase))]), default);
        }
        finally { writes.Release(); }
    }

    internal async Task StartAsync(HostedServer server, string data)
    {
        if (LoopbackOnly)
            server = server with { Reach = HostedReach.Network };
        var binary = inventory.Installed.FirstOrDefault(item => item.Role == EngineRole.Dedicated && item.Edition == server.Engine);
        await using (await WindowsGameSessionHost.OpenLeaseAsync(root, ".hosting-start.lock", default))
        {
            var state = await session.StartAsync(server, binary, data, root);
            if (IsActive(state))
                activeSettings.TryAdd(server.Id, server);
        }
        await RefreshAsync();
    }

    internal async Task StopAsync(string id)
    {
        await session.StopAsync(id);
        await RefreshAsync();
    }

    internal async Task ShutdownAsync()
    {
        await session.ShutdownAsync();
        await RefreshAsync();
    }

    internal async Task PollAsync()
    {
        await session.PollAsync();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        Running = await session.SnapshotAsync();
        foreach (string id in activeSettings.Keys.Where(id => !IsActive(id)).ToArray())
            activeSettings.Remove(id);
    }

    internal Task<bool> NextMapAsync(string id) => session.NextMapAsync(id);
    internal Task<bool> KickAsync(string id, HostedStatusPlayer player) => session.KickAsync(id, player);

    internal async Task<string> ExportAsync(HostedServer server, string parent, bool passwords, bool matchSettings)
    {
        string? version = server.Rules == HostedRules.Zpam && matchSettings ? files.InstalledVersion(server.ZpamLine) : null;
        var template = await files.ReadTemplateAsync(version, default);
        return await session.ExportAsync(server, parent, template, Zpam.State(server.ZpamLine).Release, passwords, matchSettings);
    }

    internal string LogFolder(string id) => WindowsFiles.Under(root, $"servers/{id}/logs");
    internal Task<string?> ReadLogAsync(string id) => WindowsLauncherSession.ReadLatestLogAsync(LogFolder(id), 24 * 1024);

    internal (HostedJoinAddresses Addresses, bool Wifi) JoinAddresses(HostedServer server)
    {
        List<string> addresses = [];
        bool wifi = false;
#if UPDATE_FIXTURES
        if (LoopbackOnly && Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_HOSTING_BOARD") == "wifi")
            wifi = true;
#endif
        if (!LoopbackOnly)
        {
            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                        adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .Select(adapter => (Adapter: adapter, Properties: adapter.GetIPProperties()))
                    .OrderByDescending(entry => entry.Properties.GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)));
                foreach (var (adapter, properties) in adapters)
                {
                    var local = properties.UnicastAddresses.Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(address => address.Address.ToString()).ToArray();
                    wifi |= local.Length > 0 && adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    addresses.AddRange(local);
                }
            }
            catch (NetworkInformationException) { }
        }
        return (HostedJoinAddresses.From(server, addresses, LoopbackOnly), wifi);
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

}
